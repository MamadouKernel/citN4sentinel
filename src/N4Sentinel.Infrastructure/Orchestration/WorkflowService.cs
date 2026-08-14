using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Gestion des workflows versionnés (FR-003, FR-004).
///
/// Règle centrale : UN WORKFLOW QUI A SERVI NE SE MODIFIE PLUS. Toute
/// modification produit une nouvelle version, et l'ancienne reste attachée aux
/// exécutions qu'elle a produites.
///
/// Sans cela, le rapport d'une opération de la semaine dernière décrirait des
/// étapes qui ne sont pas celles qui ont tourné. La traçabilité s'effondrerait
/// précisément le jour où elle compte : celui où l'on cherche à comprendre ce
/// qui s'est passé.
/// </summary>
public sealed class WorkflowService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<WorkflowService> logger)
{
    // -----------------------------------------------------------------------
    // Lecture
    // -----------------------------------------------------------------------
    public async Task<List<Workflow>> GetForEnvironmentAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Workflows
            .AsNoTracking()
            .Include(w => w.Steps)
            .Where(w => w.EnvironmentId == environmentId)
            .OrderBy(w => w.Kind).ThenBy(w => w.Code).ThenByDescending(w => w.Version)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Dernière version utilisable de chaque workflow. C'est ce qu'on propose
    /// au lancement : les versions antérieures existent pour l'historique, pas
    /// pour être rejouées.
    /// </summary>
    public async Task<List<Workflow>> GetRunnableAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var tous = await db.Workflows
            .AsNoTracking()
            .Include(w => w.Steps)
            .Where(w => w.EnvironmentId == environmentId
                        && (w.Status == LifecycleStatus.Valide || w.Status == LifecycleStatus.Actif))
            .ToListAsync(ct);

        return tous
            .GroupBy(w => w.Code)
            .Select(g => g.OrderByDescending(w => w.Version).First())
            .Where(w => w.Steps.Count > 0)
            .OrderBy(w => w.Kind)
            .ToList();
    }

    public async Task<Workflow?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Workflows
            .AsNoTracking()
            .Include(w => w.Steps.OrderBy(s => s.Order))
            .Include(w => w.Environment)
            .FirstOrDefaultAsync(w => w.Id == id, ct);
    }

    // -----------------------------------------------------------------------
    // Écriture
    // -----------------------------------------------------------------------
    public async Task<Guid> CreateAsync(Workflow workflow, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        workflow.Version = 1;
        workflow.Status = LifecycleStatus.Brouillon;
        workflow.HasBeenExecuted = false;

        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);
        return workflow.Id;
    }

    /// <summary>
    /// Enregistre une modification.
    ///
    /// Tant que le workflow n'a jamais servi, il est modifié sur place — un
    /// brouillon n'a pas d'historique à préserver. Dès qu'une exécution s'en
    /// est servie, une NOUVELLE VERSION est créée et l'ancienne reste intacte.
    /// </summary>
    public async Task<SaveWorkflowResult> SaveAsync(
        Guid workflowId, string name, string? description, List<WorkflowStep> steps,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existant = await db.Workflows
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId, ct);

        if (existant is null) return SaveWorkflowResult.Failed("Workflow introuvable.");

        var erreur = Valider(steps);
        if (erreur is not null) return SaveWorkflowResult.Failed(erreur);

        if (!existant.HasBeenExecuted)
        {
            existant.Name = name;
            existant.Description = description;

            db.WorkflowSteps.RemoveRange(existant.Steps);
            foreach (var (etape, index) in steps.Select((s, i) => (s, i)))
            {
                etape.Id = Guid.NewGuid();
                etape.WorkflowId = existant.Id;
                etape.Order = index + 1;
                db.WorkflowSteps.Add(etape);
            }

            await db.SaveChangesAsync(ct);
            return SaveWorkflowResult.Ok(existant.Id, existant.Version, nouvelleVersion: false);
        }

        // Le workflow a servi : on ne le touche pas. Nouvelle version.
        var derniereVersion = await db.Workflows
            .Where(w => w.EnvironmentId == existant.EnvironmentId && w.Code == existant.Code)
            .MaxAsync(w => w.Version, ct);

        var nouveau = new Workflow
        {
            EnvironmentId = existant.EnvironmentId,
            Code = existant.Code,
            Name = name,
            Kind = existant.Kind,
            Version = derniereVersion + 1,
            Status = LifecycleStatus.Brouillon,
            Description = description,
            RequiresApproval = existant.RequiresApproval,
            HasBeenExecuted = false
        };

        db.Workflows.Add(nouveau);
        await db.SaveChangesAsync(ct);

        foreach (var (etape, index) in steps.Select((s, i) => (s, i)))
        {
            etape.Id = Guid.NewGuid();
            etape.WorkflowId = nouveau.Id;
            etape.Order = index + 1;
            db.WorkflowSteps.Add(etape);
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Workflow {Code} : version {Ancienne} déjà exécutée, version {Nouvelle} créée. "
            + "L'ancienne reste attachée à ses exécutions.",
            existant.Code, existant.Version, nouveau.Version);

        return SaveWorkflowResult.Ok(nouveau.Id, nouveau.Version, nouvelleVersion: true);
    }

    // -----------------------------------------------------------------------
    // Génération des séquences N4 de référence
    // -----------------------------------------------------------------------
    /// <summary>
    /// Construit les trois séquences de base — démarrage, arrêt, contrôle — à
    /// partir du référentiel de l'environnement.
    ///
    /// Elles sont créées en BROUILLON, jamais validées d'office. Une séquence
    /// déduite d'un référentiel reste une proposition : c'est l'exploitation du
    /// site qui la relit, l'ajuste et la valide. Livrer une séquence
    /// automatiquement exécutable sur un écosystème qu'on n'a jamais vu serait
    /// exactement le genre de raccourci que ce projet existe pour éviter.
    ///
    /// L'ORDRE D'ARRÊT N'EST PAS L'INVERSE DE L'ORDRE DE DÉMARRAGE. Sur N4,
    /// l'arrêt suit sa propre séquence : les composants périphériques d'abord
    /// (ECN4Web, ECN4, XPS, Bridge), puis le Standby, puis le Center, puis les
    /// nœuds Cluster. On s'en approche par les rôles, pas par un tri inversé.
    /// </summary>
    public async Task<GenerationResult> GenerateDefaultsAsync(
        Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composants = await db.Components
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId && c.ControlMode != ControlMode.NonSupervise)
            .ToListAsync(ct);

        if (composants.Count == 0)
            return GenerationResult.Failed(
                "Aucun composant supervisé n'est déclaré pour cet environnement. "
                + "Le référentiel doit être renseigné avant de pouvoir en déduire une séquence.");

        var existants = await db.Workflows
            .Where(w => w.EnvironmentId == environmentId)
            .Select(w => w.Code)
            .ToListAsync(ct);

        var crees = new List<string>();

        if (!existants.Contains("DEMARRAGE-COMPLET"))
        {
            var ordonnes = composants
                .OrderBy(c => c.StartOrder)
                .ThenBy(c => RangDemarrage(c.Role))
                .ThenBy(c => c.LogicalName)
                .ToList();

            await CreerAsync(db, environmentId, "DEMARRAGE-COMPLET", "Démarrage complet de l'écosystème",
                WorkflowKind.DemarrageComplet, ordonnes, StepAction.Demarrer, ct);

            crees.Add("Démarrage complet");
        }

        if (!existants.Contains("ARRET-COMPLET"))
        {
            var ordonnes = composants
                .OrderBy(c => RangArret(c.Role))
                .ThenByDescending(c => c.StartOrder)
                .ThenBy(c => c.LogicalName)
                .ToList();

            await CreerAsync(db, environmentId, "ARRET-COMPLET", "Arrêt complet de l'écosystème",
                WorkflowKind.ArretComplet, ordonnes, StepAction.Arreter, ct);

            crees.Add("Arrêt complet");
        }

        if (!existants.Contains("CONTROLE-COMPLET"))
        {
            var ordonnes = composants.OrderBy(c => c.StartOrder).ThenBy(c => c.LogicalName).ToList();

            await CreerAsync(db, environmentId, "CONTROLE-COMPLET", "Contrôle de l'état de l'écosystème",
                WorkflowKind.ControleSeul, ordonnes, StepAction.Verifier, ct);

            crees.Add("Contrôle complet");
        }

        if (crees.Count == 0)
            return GenerationResult.Failed(
                "Les séquences de référence existent déjà pour cet environnement. "
                + "Elles ne sont pas régénérées : ce sont peut-être des versions relues et ajustées sur place.");

        return GenerationResult.Ok(crees);
    }

    private async Task CreerAsync(
        N4SentinelDbContext db, Guid environmentId, string code, string nom, WorkflowKind nature,
        List<N4Component> composants, StepAction action, CancellationToken ct)
    {
        var workflow = new Workflow
        {
            EnvironmentId = environmentId,
            Code = code,
            Name = nom,
            Kind = nature,
            Version = 1,
            Status = LifecycleStatus.Brouillon,
            Description =
                "Séquence déduite du référentiel : ordre de démarrage déclaré, rôles N4 et dépendances. "
                + "À RELIRE ET AJUSTER avant validation — elle n'a pas été vérifiée sur cet écosystème."
        };

        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);

        var ordre = 1;
        foreach (var composant in composants)
        {
            // Un composant non pilotable est CONTROLE, jamais commande : le
            // rattachement a la cartographie ne vaut pas autorisation d'agir.
            var actionReelle = composant.CanBeControlled ? action : StepAction.Verifier;

            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id,
                Order = ordre++,
                Name = $"{Verbe(actionReelle)} {composant.LogicalName}",
                Action = actionReelle,
                ComponentId = composant.Id,
                ExpectedSeconds = actionReelle == StepAction.Demarrer ? 180 : 60,
                TimeoutSeconds = actionReelle switch
                {
                    StepAction.Demarrer => composant.Readiness.LogReadyTimeoutSeconds,
                    StepAction.Arreter => composant.Readiness.StopTimeoutSeconds,
                    _ => 120
                },
                FailurePolicy = StepFailurePolicy.Bloquer,
                CanRunInParallel = false
            });
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Séquence « {Nom} » générée pour l'environnement {Env} : {Etapes} étapes, en brouillon.",
            nom, environmentId, composants.Count);
    }

    /// <summary>Ordre de démarrage N4 : Cluster → Center → Standby → Bridge → XPS → ECN4 → ECN4Web.</summary>
    private static int RangDemarrage(ComponentRole role) => role switch
    {
        ComponentRole.BaseDeDonnees => 0,
        ComponentRole.ActiveMq => 1,
        ComponentRole.DossierPartage => 2,
        ComponentRole.ClusterNode => 3,
        ComponentRole.CenterNode => 4,
        ComponentRole.StandbyCenterNode => 5,
        ComponentRole.BridgeDaemon => 6,
        ComponentRole.Xps => 7,
        ComponentRole.Ecn4 => 8,
        ComponentRole.Ecn4Web => 9,
        ComponentRole.InterfaceEdi => 10,
        _ => 50
    };

    /// <summary>
    /// Ordre d'arrêt N4 : périphérie d'abord, cœur en dernier. Ce n'est PAS
    /// l'inverse du rang de démarrage — le Standby s'arrête avant le Center,
    /// alors qu'il démarre après lui.
    /// </summary>
    private static int RangArret(ComponentRole role) => role switch
    {
        ComponentRole.InterfaceEdi => 0,
        ComponentRole.Ecn4Web => 1,
        ComponentRole.Ecn4 => 2,
        ComponentRole.Xps => 3,
        ComponentRole.BridgeDaemon => 4,
        ComponentRole.StandbyCenterNode => 5,
        ComponentRole.CenterNode => 6,
        ComponentRole.ClusterNode => 7,
        ComponentRole.ActiveMq => 8,
        ComponentRole.DossierPartage => 9,
        ComponentRole.BaseDeDonnees => 10,
        _ => 50
    };

    private static string Verbe(StepAction action) => action switch
    {
        StepAction.Demarrer => "Démarrer",
        StepAction.Arreter => "Arrêter",
        StepAction.Redemarrer => "Redémarrer",
        _ => "Contrôler"
    };

    public async Task<string?> ChangeStatusAsync(
        Guid workflowId, LifecycleStatus cible, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var workflow = await db.Workflows
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId, ct);

        if (workflow is null) return "Workflow introuvable.";

        if (cible is LifecycleStatus.Valide or LifecycleStatus.Actif && workflow.Steps.Count == 0)
            return "Un workflow sans étape ne peut pas être validé.";

        workflow.Status = cible;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Contrôles de cohérence appliqués avant enregistrement.
    ///
    /// Ils portent sur ce qui rendrait une séquence dangereuse, pas sur la
    /// forme. Une séquence acceptée ici doit pouvoir être jouée sans surprise.
    /// </summary>
    private static string? Valider(List<WorkflowStep> steps)
    {
        if (steps.Count == 0)
            return "Un workflow doit comporter au moins une étape.";

        foreach (var etape in steps)
        {
            if (string.IsNullOrWhiteSpace(etape.Name))
                return "Chaque étape doit porter un nom.";

            if (etape.Action is StepAction.Demarrer or StepAction.Arreter or StepAction.Redemarrer
                && etape.ComponentId is null)
                return $"L'étape « {etape.Name} » agit sur un composant mais n'en désigne aucun.";

            if (etape.Action == StepAction.InterventionManuelle && string.IsNullOrWhiteSpace(etape.Instruction))
                return $"L'étape « {etape.Name} » attend un geste humain sans dire lequel. "
                     + "Une intervention manuelle sans consigne reporte le travail sur l'opérateur.";

            // Reessayer un arret en boucle sans comprendre pourquoi il a echoue
            // est le meilleur moyen d'aggraver un incident (FR-004).
            if (etape.AutomaticRetry
                && etape.Action is StepAction.Arreter or StepAction.Redemarrer
                && etape.MaxRetries > 0)
                return $"L'étape « {etape.Name} » autorise une nouvelle tentative automatique sur une action "
                     + "d'arrêt. C'est interdit : un arrêt qui échoue doit être compris, pas rejoué.";

            if (etape.TimeoutSeconds <= 0)
                return $"L'étape « {etape.Name} » doit porter un délai maximal.";

            if (etape.ExpectedSeconds > etape.TimeoutSeconds)
                return $"L'étape « {etape.Name} » annonce une durée normale supérieure à son délai maximal.";
        }

        return null;
    }
}

public sealed record SaveWorkflowResult
{
    public bool Succeeded { get; init; }
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }

    /// <summary>Vrai si l'enregistrement a produit une nouvelle version.</summary>
    public bool CreatedNewVersion { get; init; }

    public string? Error { get; init; }

    public static SaveWorkflowResult Ok(Guid id, int version, bool nouvelleVersion) =>
        new() { Succeeded = true, WorkflowId = id, Version = version, CreatedNewVersion = nouvelleVersion };

    public static SaveWorkflowResult Failed(string error) => new() { Succeeded = false, Error = error };
}

public sealed record GenerationResult
{
    public bool Succeeded { get; init; }
    public List<string> Created { get; init; } = [];
    public string? Error { get; init; }

    public static GenerationResult Ok(List<string> crees) => new() { Succeeded = true, Created = crees };
    public static GenerationResult Failed(string error) => new() { Succeeded = false, Error = error };
}
