using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Contrôles préalables à toute opération mutative (FR-012).
///
/// LE PRINCIPE : découvrir qu'un serveur est injoignable à la septième étape
/// d'un arrêt complet est le pire moment possible. L'écosystème est alors à
/// moitié éteint, et le composant qu'on ne peut pas joindre est précisément
/// celui qu'il faudrait arrêter. Tout ce qui peut être vérifié avant que la
/// première commande parte doit l'être avant.
///
/// UN CONTRÔLE BLOQUANT EN ÉCHEC INTERDIT L'EXÉCUTION. Il n'existe aucun
/// mécanisme pour le contourner : c'est la seule façon qu'un contrôle bloquant
/// reste bloquant le jour où quelqu'un est pressé.
///
/// Les contrôles non bloquants, eux, informent sans empêcher. La nuance
/// compte : tout déclarer bloquant finirait par entraîner à tout contourner.
/// </summary>
public sealed class PreflightService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector,
    EnvironmentLockService locks,
    ILogger<PreflightService> logger)
{
    public async Task<PreflightReport> RunAsync(Guid executionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions
            .AsNoTracking()
            .Include(x => x.Steps)
            .Include(x => x.Environment)
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);

        if (execution is null)
            return PreflightReport.Impossible("Exécution introuvable.");

        var controles = new List<PreflightCheck>();

        await ControlerVerrouAsync(execution, controles, ct);

        var composants = await ChargerComposantsAsync(db, execution, ct);

        ControlerPilotabilite(execution, composants, controles);
        ControlerMarqueurs(execution, composants, controles);
        await ControlerPrerequisAsync(db, execution, composants, controles, ct);
        await ControlerServeursAsync(composants, controles, execution.IsSimulation, ct);
        ControlerImpact(execution, composants, controles);

        var rapport = new PreflightReport
        {
            ExecutionId = executionId,
            RunAt = DateTimeOffset.UtcNow,
            Checks = controles
        };

        // Le rapport est conserve avec l'execution : trois mois plus tard, on
        // doit pouvoir dire ce qui avait ete verifie avant de lancer, pas
        // seulement ce qui s'est passe pendant.
        await using var ecriture = await dbFactory.CreateDbContextAsync(ct);
        var suivi = await ecriture.Executions.FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (suivi is not null)
        {
            suivi.PreflightJson = JsonSerializer.Serialize(controles);
            suivi.PreflightAt = rapport.RunAt;
            suivi.PreflightBlocked = rapport.HasBlockingFailure;
            await ecriture.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Pré-check de {Correlation} : {Total} contrôle(s), {Echecs} échec(s) bloquant(s), {Reserves} réserve(s).",
            execution.CorrelationId, controles.Count, rapport.BlockingFailures, rapport.Warnings);

        return rapport;
    }

    // -----------------------------------------------------------------------
    // Contrôles
    // -----------------------------------------------------------------------
    private async Task ControlerVerrouAsync(
        WorkflowExecution execution, List<PreflightCheck> controles, CancellationToken ct)
    {
        if (execution.IsSimulation)
        {
            controles.Add(PreflightCheck.NonApplicable(
                "Verrou d'environnement",
                "Une simulation n'émet aucune commande : elle ne prend pas le verrou et n'empêche personne de travailler."));
            return;
        }

        var verrou = await locks.GetAsync(execution.EnvironmentId, ct);

        if (verrou is null || verrou.IsExpired || verrou.ExecutionId == execution.Id)
        {
            controles.Add(PreflightCheck.Reussi(
                "Verrou d'environnement",
                "Aucune autre opération mutative n'est en cours sur cet environnement."));
            return;
        }

        controles.Add(PreflightCheck.Echec(
            "Verrou d'environnement",
            $"Une opération est déjà en cours : « {verrou.Reason} », lancée par {verrou.HeldBy} "
            + $"à {verrou.AcquiredAt.ToLocalTime():HH:mm}.",
            "Attendez qu'elle se termine, ou demandez à son auteur de l'annuler.",
            bloquant: true));
    }

    private static void ControlerPilotabilite(
        WorkflowExecution execution, Dictionary<Guid, N4Component> composants, List<PreflightCheck> controles)
    {
        var mutatives = execution.Steps
            .Where(s => s.Action is StepAction.Demarrer or StepAction.Arreter or StepAction.Redemarrer)
            .ToList();

        if (mutatives.Count == 0)
        {
            controles.Add(PreflightCheck.NonApplicable(
                "Composants pilotables",
                "Cette opération ne comporte aucune action mutative."));
            return;
        }

        var refus = new List<string>();

        foreach (var etape in mutatives)
        {
            if (etape.ComponentId is null)
            {
                refus.Add($"« {etape.Name} » agit sur un composant mais n'en désigne aucun");
                continue;
            }

            if (!composants.TryGetValue(etape.ComponentId.Value, out var composant))
            {
                refus.Add($"« {etape.Name} » vise un composant qui n'existe plus au référentiel");
                continue;
            }

            if (!composant.CanBeControlled)
                refus.Add($"« {composant.LogicalName} » n'est pas pilotable "
                        + $"(mode {composant.ControlMode}, état {composant.Status})");

            if (string.IsNullOrWhiteSpace(composant.WindowsServiceName))
                refus.Add($"« {composant.LogicalName} » n'a aucun nom de service Windows renseigné");
        }

        if (refus.Count == 0)
        {
            controles.Add(PreflightCheck.Reussi(
                "Composants pilotables",
                $"Les {mutatives.Count} action(s) mutative(s) portent sur des composants déclarés pilotables et validés."));
            return;
        }

        controles.Add(PreflightCheck.Echec(
            "Composants pilotables",
            string.Join(" ; ", refus) + ".",
            "Le référentiel doit être complet et le composant déclaré pilotable ET validé. "
            + "Aucune commande n'est envoyée à un composant en brouillon, quelle que soit l'urgence.",
            bloquant: true));
    }

    private static void ControlerMarqueurs(
        WorkflowExecution execution, Dictionary<Guid, N4Component> composants, List<PreflightCheck> controles)
    {
        var demarrages = execution.Steps
            .Where(s => s.Action is StepAction.Demarrer or StepAction.Redemarrer && s.ComponentId is not null)
            .Select(s => s.ComponentId!.Value)
            .Distinct()
            .ToList();

        if (demarrages.Count == 0) return;

        var sansPreuve = demarrages
            .Where(id => composants.TryGetValue(id, out var c) && !c.Readiness.IsProvable)
            .Select(id => composants[id].LogicalName)
            .ToList();

        if (sansPreuve.Count == 0)
        {
            controles.Add(PreflightCheck.Reussi(
                "Preuve de démarrage",
                $"Les {demarrages.Count} composant(s) à démarrer portent un marqueur de journal exploitable."));
            return;
        }

        // NON BLOQUANT, et c'est un choix. Interdire une operation faute de
        // marqueur reviendrait a rendre l'outil inutilisable sur un site qui
        // n'a pas encore fait son releve - alors qu'il peut deja rendre
        // service. Mais la reserve doit etre dite, et elle se retrouvera dans
        // le rapport d'execution.
        controles.Add(PreflightCheck.Avertissement(
            "Preuve de démarrage",
            $"{sansPreuve.Count} composant(s) sans marqueur de journal : {string.Join(", ", sansPreuve)}. "
            + "Leur démarrage applicatif ne sera PAS prouvé — l'opération se terminera avec réserve, "
            + "et l'état de ces composants restera « à confirmer ».",
            "L'écran Marqueurs relève le marqueur sur un démarrage réel et lève définitivement cette réserve."));
    }

    /// <summary>
    /// Vérifie que ce dont dépendent les composants à démarrer est soit
    /// démarré par la séquence elle-même, soit déjà opérationnel.
    /// </summary>
    private async Task ControlerPrerequisAsync(
        N4SentinelDbContext db, WorkflowExecution execution,
        Dictionary<Guid, N4Component> composants, List<PreflightCheck> controles, CancellationToken ct)
    {
        var demarres = execution.Steps
            .Where(s => s.Action is StepAction.Demarrer or StepAction.Redemarrer && s.ComponentId is not null)
            .Select(s => s.ComponentId!.Value)
            .ToHashSet();

        if (demarres.Count == 0) return;

        var dependances = await db.ComponentDependencies
            .AsNoTracking()
            .Include(d => d.DependsOnComponent)
            .Where(d => demarres.Contains(d.ComponentId) && d.Kind != DependencyKind.Informative)
            .ToListAsync(ct);

        var externes = dependances
            .Where(d => !demarres.Contains(d.DependsOnComponentId))
            .ToList();

        if (externes.Count == 0)
        {
            controles.Add(PreflightCheck.Reussi(
                "Prérequis de dépendance",
                "Tous les prérequis des composants à démarrer sont eux-mêmes démarrés par cette séquence."));
            return;
        }

        var noms = externes
            .Select(d => d.DependsOnComponent?.LogicalName ?? "composant inconnu")
            .Distinct()
            .ToList();

        controles.Add(PreflightCheck.Avertissement(
            "Prérequis de dépendance",
            $"{noms.Count} prérequis ne sont pas démarrés par cette séquence : {string.Join(", ", noms)}. "
            + "S'ils ne sont pas déjà opérationnels, les composants qui en dépendent échoueront.",
            "Ajoutez une étape de contrôle en tête de séquence, ou vérifiez leur état sur l'écran de supervision."));
    }

    private async Task ControlerServeursAsync(
        Dictionary<Guid, N4Component> composants, List<PreflightCheck> controles,
        bool simulation, CancellationToken ct)
    {
        var serveurs = composants.Values
            .Where(c => c.Server is not null)
            .Select(c => c.Server!)
            .DistinctBy(s => s.Id)
            .ToList();

        if (serveurs.Count == 0) return;

        var injoignables = new List<string>();
        var reserves = new List<string>();

        foreach (var serveur in serveurs)
        {
            ct.ThrowIfCancellationRequested();

            var resolution = await targetFactory.CreateAsync(serveur, ct);
            if (!resolution.Succeeded)
            {
                injoignables.Add($"{serveur.HostName} : {resolution.Error}");
                continue;
            }

            var ping = await connector.PingAsync(resolution.Target!, ct);
            if (!ping.Succeeded)
            {
                injoignables.Add($"{serveur.HostName} : {ping.Error}");
                continue;
            }

            var systeme = await connector.GetSystemAsync(resolution.Target!, ct);
            if (!systeme.Succeeded || systeme.Value is null) continue;

            // L'ecart d'horloge est une cause documentee de statuts
            // DISCONNECTED trompeurs sur N4, et une cause frequente d'incident
            // majeur. Le signaler AVANT evite de chercher la panne ailleurs.
            if (systeme.Value.ClockSkewSeconds is { } ecart && Math.Abs(ecart) > 5)
                reserves.Add($"{serveur.HostName} : écart d'horloge de {ecart:0.0} s avec le serveur Sentinel");

            foreach (var disque in systeme.Value.Disks.Where(d => d.FreePercent < 10))
                reserves.Add($"{serveur.HostName} : disque {disque.Drive} à {disque.FreePercent:0.0} % libre");
        }

        if (injoignables.Count > 0)
        {
            controles.Add(PreflightCheck.Echec(
                "Joignabilité des serveurs",
                string.Join(" ; ", injoignables) + ".",
                "Un serveur injoignable découvert en cours de séquence laisse l'écosystème dans un état "
                + "intermédiaire. Corrigez l'accès avant de lancer.",
                // Une simulation n'emet rien : l'injoignabilite est une
                // information utile, pas un motif de refus.
                bloquant: !simulation));
        }
        else
        {
            controles.Add(PreflightCheck.Reussi(
                "Joignabilité des serveurs",
                $"Les {serveurs.Count} serveur(s) concerné(s) répondent et acceptent l'exécution distante."));
        }

        if (reserves.Count > 0)
            controles.Add(PreflightCheck.Avertissement(
                "Ressources et horloge",
                string.Join(" ; ", reserves) + ".",
                "Un écart d'horloge supérieur à cinq secondes produit des statuts N4 trompeurs. "
                + "Un disque presque plein empêche l'écriture des journaux — donc la preuve de démarrage."));
    }

    /// <summary>
    /// Énonce ce qui tombera avec les composants arrêtés, même s'ils ne sont
    /// pas dans la séquence. L'opérateur doit le savoir avant, pas le découvrir
    /// à l'appel d'un client.
    /// </summary>
    private static void ControlerImpact(
        WorkflowExecution execution, Dictionary<Guid, N4Component> composants, List<PreflightCheck> controles)
    {
        var arretes = execution.Steps
            .Where(s => s.Action is StepAction.Arreter or StepAction.Redemarrer && s.ComponentId is not null)
            .Select(s => s.ComponentId!.Value)
            .ToHashSet();

        if (arretes.Count == 0) return;

        var noms = arretes
            .Where(composants.ContainsKey)
            .Select(id => composants[id].LogicalName)
            .ToList();

        var critiques = arretes
            .Where(id => composants.TryGetValue(id, out var c) && c.Criticality >= CriticalityLevel.Elevee)
            .Select(id => composants[id].LogicalName)
            .ToList();

        var detail = $"{noms.Count} composant(s) seront arrêtés : {string.Join(", ", noms)}.";
        if (critiques.Count > 0)
            detail += $" Dont {critiques.Count} de criticité élevée : {string.Join(", ", critiques)}.";

        controles.Add(PreflightCheck.Avertissement("Impact de l'arrêt", detail,
            "Les étapes déjà exécutées ne sont pas défaites en cas d'annulation : "
            + "une séquence d'arrêt interrompue laisse l'écosystème à moitié éteint."));
    }

    // -----------------------------------------------------------------------
    private static async Task<Dictionary<Guid, N4Component>> ChargerComposantsAsync(
        N4SentinelDbContext db, WorkflowExecution execution, CancellationToken ct)
    {
        var ids = execution.Steps
            .Where(s => s.ComponentId is not null)
            .Select(s => s.ComponentId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return [];

        return await db.Components
            .AsNoTracking()
            .Include(c => c.Server)
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);
    }

    /// <summary>Relit le rapport conservé avec une exécution.</summary>
    public static List<PreflightCheck> Relire(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<PreflightCheck>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}

public enum PreflightOutcome
{
    Reussi = 0,
    Avertissement = 1,
    Echec = 2,
    NonApplicable = 3
}

public sealed record PreflightCheck
{
    public string Name { get; init; } = string.Empty;
    public PreflightOutcome Outcome { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string? Remedy { get; init; }

    /// <summary>
    /// Un contrôle bloquant en échec interdit l'exécution, sans contournement
    /// possible. C'est la seule façon qu'il reste bloquant le jour où quelqu'un
    /// est pressé.
    /// </summary>
    public bool IsBlocking { get; init; }

    public static PreflightCheck Reussi(string nom, string detail) =>
        new() { Name = nom, Outcome = PreflightOutcome.Reussi, Detail = detail };

    public static PreflightCheck Avertissement(string nom, string detail, string? conseil = null) =>
        new() { Name = nom, Outcome = PreflightOutcome.Avertissement, Detail = detail, Remedy = conseil };

    public static PreflightCheck Echec(string nom, string detail, string? conseil, bool bloquant) =>
        new() { Name = nom, Outcome = PreflightOutcome.Echec, Detail = detail, Remedy = conseil, IsBlocking = bloquant };

    public static PreflightCheck NonApplicable(string nom, string detail) =>
        new() { Name = nom, Outcome = PreflightOutcome.NonApplicable, Detail = detail };
}

public sealed record PreflightReport
{
    public Guid ExecutionId { get; init; }
    public DateTimeOffset RunAt { get; init; }
    public List<PreflightCheck> Checks { get; init; } = [];
    public string? Error { get; init; }

    public int BlockingFailures =>
        Checks.Count(c => c.Outcome == PreflightOutcome.Echec && c.IsBlocking);

    public int Warnings => Checks.Count(c => c.Outcome == PreflightOutcome.Avertissement);

    public bool HasBlockingFailure => BlockingFailures > 0;

    /// <summary>Vrai si l'exécution peut être lancée.</summary>
    public bool Cleared => Error is null && !HasBlockingFailure;

    public static PreflightReport Impossible(string error) => new() { Error = error };
}
