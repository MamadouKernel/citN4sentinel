using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Opérations ponctuelles : unitaire, sur un groupe, ou redémarrage tournant
/// (FR-040 à FR-045).
///
/// Elles ne court-circuitent PAS l'orchestrateur. Chacune fabrique un workflow
/// réel — versionné, validé, tracé — et passe par le même moteur, le même
/// verrou, le même pré-check qu'une séquence complète. Un chemin « rapide »
/// qui contournerait ces garde-fous serait exactement celui qu'on emprunterait
/// un soir d'incident, c'est-à-dire au pire moment.
///
/// La différence avec un workflow d'exploitation est ailleurs : celui-ci est
/// jetable. Il porte un code unique, ne réapparaît pas dans le catalogue, et
/// existe pour que le rapport d'exécution ait quelque chose à décrire.
/// </summary>
public sealed class AdHocOperationService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<AdHocOperationService> logger)
{
    /// <summary>
    /// Analyse d'impact : qui d'autre est concerné si l'on agit sur cette
    /// sélection (FR-042).
    ///
    /// À l'arrêt, tout ce qui dépend d'un composant arrêté devient inopérant,
    /// que ce soit voulu ou non. L'opérateur doit le voir avant de valider, pas
    /// le découvrir à l'appel d'un client.
    /// </summary>
    public async Task<ImpactAnalysis> AnalyseImpactAsync(
        Guid environmentId, IReadOnlyCollection<Guid> componentIds, StepAction action,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composants = await db.Components
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId)
            .ToDictionaryAsync(c => c.Id, ct);

        var dependances = await db.ComponentDependencies
            .AsNoTracking()
            .Where(d => composants.Keys.Contains(d.ComponentId) && d.Kind != DependencyKind.Informative)
            .ToListAsync(ct);

        var vises = componentIds.Where(composants.ContainsKey).ToHashSet();
        var entraines = new HashSet<Guid>();
        var manquants = new HashSet<Guid>();

        if (action is StepAction.Arreter or StepAction.Redemarrer)
        {
            // Propagation descendante : tout ce qui depend d'un composant
            // arrete tombe avec lui, et tout ce qui depend de CEUX-LA aussi.
            var aExplorer = new Queue<Guid>(vises);
            while (aExplorer.Count > 0)
            {
                var courant = aExplorer.Dequeue();
                foreach (var dep in dependances.Where(d => d.DependsOnComponentId == courant))
                {
                    if (vises.Contains(dep.ComponentId)) continue;
                    if (!entraines.Add(dep.ComponentId)) continue;
                    aExplorer.Enqueue(dep.ComponentId);
                }
            }
        }

        if (action is StepAction.Demarrer or StepAction.Redemarrer)
        {
            // Prerequis absents de la selection : ils devront deja tourner.
            foreach (var dep in dependances.Where(d => vises.Contains(d.ComponentId)))
                if (!vises.Contains(dep.DependsOnComponentId))
                    manquants.Add(dep.DependsOnComponentId);
        }

        return new ImpactAnalysis
        {
            Targeted = vises.Select(id => composants[id]).OrderBy(c => c.StartOrder).ToList(),
            Collateral = entraines.Where(composants.ContainsKey)
                                  .Select(id => composants[id]).OrderBy(c => c.StartOrder).ToList(),
            MissingPrerequisites = manquants.Where(composants.ContainsKey)
                                            .Select(id => composants[id]).OrderBy(c => c.StartOrder).ToList()
        };
    }

    /// <summary>
    /// Fabrique le workflow jetable correspondant à l'opération demandée.
    /// Il est créé directement en état Validé : il a été construit par
    /// l'application à partir du référentiel, pas saisi à la main.
    /// </summary>
    public async Task<AdHocResult> BuildAsync(
        Guid environmentId, IReadOnlyList<Guid> componentIds, StepAction action,
        AdHocShape shape, string actor, CancellationToken ct = default)
    {
        if (componentIds.Count == 0)
            return AdHocResult.Failed("Aucun composant sélectionné.");

        if (action is not (StepAction.Demarrer or StepAction.Arreter or StepAction.Redemarrer
                        or StepAction.Verifier))
            return AdHocResult.Failed($"L'action « {action} » ne se prête pas à une opération ponctuelle.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composants = await db.Components
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId && componentIds.Contains(c.Id))
            .ToListAsync(ct);

        if (composants.Count == 0)
            return AdHocResult.Failed("Aucun des composants sélectionnés n'existe dans cet environnement.");

        var nonPilotables = composants
            .Where(c => action != StepAction.Verifier && !c.CanBeControlled)
            .Select(c => c.LogicalName)
            .ToList();

        if (nonPilotables.Count > 0)
            return AdHocResult.Failed(
                $"Ces composants ne sont pas pilotables : {string.Join(", ", nonPilotables)}. "
                + "Un composant doit être déclaré pilotable ET validé pour recevoir une commande.");

        // A l'arret, on commence par les composants les plus peripheriques ;
        // au demarrage, par les plus fondamentaux. Ce n'est pas le meme tri
        // inverse : l'ordre d'arret N4 a ses propres regles.
        var ordonnes = action == StepAction.Arreter
            ? composants.OrderByDescending(c => c.StartOrder).ThenBy(c => c.LogicalName).ToList()
            : composants.OrderBy(c => c.StartOrder).ThenBy(c => c.LogicalName).ToList();

        var nature = shape switch
        {
            AdHocShape.RollingRestart => WorkflowKind.RollingRestart,
            AdHocShape.Groupe => WorkflowKind.OperationPartielle,
            _ => WorkflowKind.OperationUnitaire
        };

        if (action == StepAction.Verifier) nature = WorkflowKind.ControleSeul;

        var nom = shape switch
        {
            AdHocShape.RollingRestart => $"Redémarrage tournant — {composants.Count} composant(s)",
            AdHocShape.Groupe => $"{Verbe(action)} — {composants.Count} composant(s)",
            _ => $"{Verbe(action)} — {ordonnes[0].LogicalName}"
        };

        var workflow = new Workflow
        {
            EnvironmentId = environmentId,
            Code = $"PONCTUEL-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            Name = nom,
            Kind = nature,
            Version = 1,
            Status = LifecycleStatus.Valide,
            Description = $"Opération ponctuelle construite par {actor} depuis le référentiel. "
                        + "Elle passe par le moteur, le verrou et le pré-check comme toute autre séquence."
        };

        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);

        var etapes = shape == AdHocShape.RollingRestart
            ? ConstruireRolling(workflow.Id, ordonnes)
            : ConstruireSimple(workflow.Id, ordonnes, action);

        db.WorkflowSteps.AddRange(etapes);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Opération ponctuelle {Code} construite par {Acteur} : {Etapes} étapes.",
            workflow.Code, actor, etapes.Count);

        return AdHocResult.Ok(workflow.Id, workflow.Name, etapes.Count);
    }

    private static List<WorkflowStep> ConstruireSimple(
        Guid workflowId, List<N4Component> composants, StepAction action)
    {
        var etapes = new List<WorkflowStep>();
        var ordre = 1;

        foreach (var composant in composants)
        {
            etapes.Add(new WorkflowStep
            {
                WorkflowId = workflowId,
                Order = ordre++,
                Name = $"{Verbe(action)} {composant.LogicalName}",
                Action = action,
                ComponentId = composant.Id,
                ExpectedSeconds = action == StepAction.Demarrer ? 180 : 60,
                TimeoutSeconds = Delai(composant, action),
                FailurePolicy = StepFailurePolicy.Bloquer,
                CanRunInParallel = false
            });
        }

        return etapes;
    }

    /// <summary>
    /// Redémarrage tournant : UN COMPOSANT À LA FOIS, et le suivant n'est
    /// touché qu'après confirmation que le précédent est reparti.
    ///
    /// C'est toute la raison d'être d'un rolling restart : maintenir le service
    /// pendant l'opération. Redémarrer deux nœuds Cluster ensemble ferait
    /// exactement ce qu'on cherchait à éviter — et sur N4, un nœud doit avoir
    /// rejoint le cluster Hazelcast avant que le suivant soit arrêté.
    /// </summary>
    private static List<WorkflowStep> ConstruireRolling(Guid workflowId, List<N4Component> composants)
    {
        var etapes = new List<WorkflowStep>();
        var ordre = 1;

        foreach (var composant in composants)
        {
            etapes.Add(new WorkflowStep
            {
                WorkflowId = workflowId,
                Order = ordre++,
                Name = $"Arrêter {composant.LogicalName}",
                Action = StepAction.Arreter,
                ComponentId = composant.Id,
                ExpectedSeconds = 60,
                TimeoutSeconds = composant.Readiness.StopTimeoutSeconds,
                FailurePolicy = StepFailurePolicy.Bloquer,
                CanRunInParallel = false
            });

            etapes.Add(new WorkflowStep
            {
                WorkflowId = workflowId,
                Order = ordre++,
                Name = $"Démarrer {composant.LogicalName}",
                Action = StepAction.Demarrer,
                ComponentId = composant.Id,
                ExpectedSeconds = 180,
                TimeoutSeconds = composant.Readiness.LogReadyTimeoutSeconds,
                FailurePolicy = StepFailurePolicy.Bloquer,
                CanRunInParallel = false
            });

            // Le controle explicite avant de passer au suivant. Redondant avec
            // la preuve du demarrage ? Non : celle-ci dit que le composant a
            // fini de s'initialiser, celui-la qu'il tient toujours quelques
            // instants plus tard - le moment ou un noeud mal reintegre lache.
            etapes.Add(new WorkflowStep
            {
                WorkflowId = workflowId,
                Order = ordre++,
                Name = $"Confirmer {composant.LogicalName} avant de passer au suivant",
                Action = StepAction.Verifier,
                ComponentId = composant.Id,
                ExpectedSeconds = 10,
                TimeoutSeconds = 120,
                FailurePolicy = StepFailurePolicy.Bloquer,
                CanRunInParallel = false
            });
        }

        return etapes;
    }

    private static int Delai(N4Component composant, StepAction action) => action switch
    {
        StepAction.Demarrer => composant.Readiness.LogReadyTimeoutSeconds,
        StepAction.Arreter => composant.Readiness.StopTimeoutSeconds,
        StepAction.Redemarrer => composant.Readiness.StopTimeoutSeconds
                               + composant.Readiness.LogReadyTimeoutSeconds,
        _ => 120
    };

    private static string Verbe(StepAction action) => action switch
    {
        StepAction.Demarrer => "Démarrer",
        StepAction.Arreter => "Arrêter",
        StepAction.Redemarrer => "Redémarrer",
        _ => "Contrôler"
    };
}

/// <summary>Forme de l'opération ponctuelle.</summary>
public enum AdHocShape
{
    Unitaire = 0,
    Groupe = 1,
    RollingRestart = 2
}

public sealed record ImpactAnalysis
{
    /// <summary>Composants explicitement sélectionnés.</summary>
    public List<N4Component> Targeted { get; init; } = [];

    /// <summary>Composants qui deviendront inopérants sans avoir été sélectionnés.</summary>
    public List<N4Component> Collateral { get; init; } = [];

    /// <summary>Prérequis absents de la sélection : ils devront déjà tourner.</summary>
    public List<N4Component> MissingPrerequisites { get; init; } = [];

    public bool HasCollateral => Collateral.Count > 0;
}

public sealed record AdHocResult
{
    public bool Succeeded { get; init; }
    public Guid WorkflowId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int StepCount { get; init; }
    public string? Error { get; init; }

    public static AdHocResult Ok(Guid id, string nom, int etapes) =>
        new() { Succeeded = true, WorkflowId = id, Name = nom, StepCount = etapes };

    public static AdHocResult Failed(string error) => new() { Succeeded = false, Error = error };
}
