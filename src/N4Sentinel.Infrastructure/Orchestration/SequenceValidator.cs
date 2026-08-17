using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Contrôle de cohérence d'une séquence au regard du graphe de dépendances
/// (FR-044).
///
/// Le cahier des charges est explicite : l'application doit EMPÊCHER une
/// séquence incompatible — typiquement démarrer XPS avant que le Bridge soit
/// confirmé opérationnel. Le refus est bloquant, pas un avertissement que
/// l'utilisateur peut ignorer d'un clic.
///
/// UNE ASYMÉTRIE À NE PAS PERDRE : l'ordre d'arrêt n'est PAS l'inverse de
/// l'ordre de démarrage. Sur N4, l'arrêt suit sa propre séquence, définie au
/// workflow. Ce validateur vérifie donc les dépendances dans le sens du
/// démarrage, et dans le sens inverse à l'arrêt — mais il n'impose jamais que
/// l'un soit le miroir de l'autre.
/// </summary>
public sealed class SequenceValidator(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    /// <summary>
    /// Vérifie qu'une suite d'étapes respecte le graphe de dépendances.
    /// Retourne la liste des violations ; vide si la séquence est acceptable.
    /// </summary>
    public Task<List<SequenceViolation>> ValidateAsync(
        Guid environmentId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
        ValidateAsync(environmentId, steps.Select(s => new SequenceStepInfo(
            s.Order, s.ComponentId, s.Action, s.CanRunInParallel)).ToList(), ct);

    /// <summary>
    /// Même contrôle, rejoué sur les étapes RÉELLEMENT préparées pour une
    /// exécution (FR-044) — et non plus seulement à l'édition d'un workflow.
    /// Un workflow validé aujourd'hui peut violer le graphe de dépendances de
    /// demain si une dépendance a changé entre-temps : la garantie doit tenir
    /// au lancement, pas seulement à la conception.
    /// </summary>
    public Task<List<SequenceViolation>> ValidateAsync(
        Guid environmentId, IReadOnlyList<ExecutionStep> steps, CancellationToken ct = default) =>
        ValidateAsync(environmentId, steps.Select(s => new SequenceStepInfo(
            s.Order, s.ComponentId, s.Action, s.CanRunInParallel)).ToList(), ct);

    private async Task<List<SequenceViolation>> ValidateAsync(
        Guid environmentId, IReadOnlyList<SequenceStepInfo> steps, CancellationToken ct)
    {
        var violations = new List<SequenceViolation>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composants = await db.Components
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId)
            .ToDictionaryAsync(c => c.Id, ct);

        var dependances = await db.ComponentDependencies
            .AsNoTracking()
            .Where(d => composants.Keys.Contains(d.ComponentId))
            .ToListAsync(ct);

        // Position de la premiere action portant sur chaque composant, dans
        // l'ordre du workflow.
        var demarrages = new Dictionary<Guid, int>();
        var arrets = new Dictionary<Guid, int>();

        foreach (var etape in steps.OrderBy(s => s.Order))
        {
            if (etape.ComponentId is null) continue;

            if (etape.Action is StepAction.Demarrer or StepAction.Redemarrer)
                demarrages.TryAdd(etape.ComponentId.Value, etape.Order);

            if (etape.Action is StepAction.Arreter or StepAction.Redemarrer)
                arrets.TryAdd(etape.ComponentId.Value, etape.Order);
        }

        foreach (var dep in dependances)
        {
            if (dep.Kind == DependencyKind.Informative) continue;

            composants.TryGetValue(dep.ComponentId, out var dependant);
            composants.TryGetValue(dep.DependsOnComponentId, out var prerequis);
            if (dependant is null || prerequis is null) continue;

            // --- Sens du demarrage : le prerequis doit etre demarre avant.
            if (demarrages.TryGetValue(dep.ComponentId, out var rangDependant))
            {
                if (!demarrages.TryGetValue(dep.DependsOnComponentId, out var rangPrerequis))
                {
                    // Le prerequis n'est pas dans la sequence : ce n'est une
                    // faute que s'il doit etre demarre, pas s'il tourne deja.
                    // On le signale sans bloquer, en demandant un controle.
                    violations.Add(new SequenceViolation
                    {
                        Blocking = false,
                        Order = rangDependant,
                        Message =
                            $"« {dependant.LogicalName} » dépend de « {prerequis.LogicalName} », qui n'est pas "
                            + "démarré par cette séquence. Si ce composant n'est pas déjà opérationnel, "
                            + "l'opération échouera. Ajoutez une étape de contrôle avant.",
                        Remedy = $"Insérer un contrôle de « {prerequis.LogicalName} » avant l'ordre {rangDependant}."
                    });
                }
                else if (rangPrerequis > rangDependant)
                {
                    violations.Add(new SequenceViolation
                    {
                        Blocking = true,
                        Order = rangDependant,
                        Message =
                            $"« {dependant.LogicalName} » est démarré à l'ordre {rangDependant}, avant "
                            + $"« {prerequis.LogicalName} » (ordre {rangPrerequis}) dont il dépend. "
                            + "Cette séquence ne peut pas aboutir.",
                        Remedy = $"Placer « {prerequis.LogicalName} » avant « {dependant.LogicalName} »."
                    });
                }
            }

            // --- Sens de l'arret : le dependant s'arrete avant son prerequis.
            if (arrets.TryGetValue(dep.DependsOnComponentId, out var rangArretPrerequis)
                && arrets.TryGetValue(dep.ComponentId, out var rangArretDependant)
                && rangArretPrerequis < rangArretDependant)
            {
                violations.Add(new SequenceViolation
                {
                    Blocking = true,
                    Order = rangArretPrerequis,
                    Message =
                        $"« {prerequis.LogicalName} » est arrêté à l'ordre {rangArretPrerequis}, alors que "
                        + $"« {dependant.LogicalName} », qui en dépend, ne s'arrête qu'à l'ordre {rangArretDependant}. "
                        + "Le composant dépendant se retrouverait privé de son prérequis en cours de fonctionnement.",
                    Remedy = $"Arrêter « {dependant.LogicalName} » avant « {prerequis.LogicalName} »."
                });
            }
        }

        // --- Regle N4 propre au Cluster : les noeuds bougent UN PAR UN, quel
        // que soit le sens (AC-05). Au demarrage, le premier doit avoir
        // rejoint le cluster Hazelcast avant que le suivant soit lance. A
        // l'arret, sortir plusieurs noeuds a la fois expose le cluster a une
        // perte de quorum — la meme regle protege les deux sens.
        var noeudsParallelesDemarrage = steps
            .Where(s => s.CanRunInParallel && s.ComponentId is not null
                        && s.Action is StepAction.Demarrer or StepAction.Redemarrer)
            .Where(s => composants.TryGetValue(s.ComponentId!.Value, out var c)
                        && c.Role == ComponentRole.ClusterNode)
            .ToList();

        if (noeudsParallelesDemarrage.Count > 1)
            violations.Add(new SequenceViolation
            {
                Blocking = true,
                Order = noeudsParallelesDemarrage[0].Order,
                Message =
                    $"{noeudsParallelesDemarrage.Count} nœuds Cluster sont déclarés parallélisables au démarrage. "
                    + "Les nœuds Cluster N4 se démarrent UN PAR UN : le premier doit avoir rejoint le cluster "
                    + "Hazelcast avant que le suivant soit lancé.",
                Remedy = "Décocher « parallélisable » sur les étapes de démarrage des nœuds Cluster."
            });

        var noeudsParallelesArret = steps
            .Where(s => s.CanRunInParallel && s.ComponentId is not null && s.Action == StepAction.Arreter)
            .Where(s => composants.TryGetValue(s.ComponentId!.Value, out var c)
                        && c.Role == ComponentRole.ClusterNode)
            .ToList();

        if (noeudsParallelesArret.Count > 1)
            violations.Add(new SequenceViolation
            {
                Blocking = true,
                Order = noeudsParallelesArret[0].Order,
                Message =
                    $"{noeudsParallelesArret.Count} nœuds Cluster sont déclarés parallélisables à l'arrêt. "
                    + "Les nœuds Cluster N4 s'arrêtent UN PAR UN : en sortir plusieurs à la fois expose le "
                    + "cluster Hazelcast restant à une perte de quorum.",
                Remedy = "Décocher « parallélisable » sur les étapes d'arrêt des nœuds Cluster."
            });

        return violations.OrderBy(v => v.Order).ToList();
    }

    /// <summary>
    /// Détecte un cycle dans le graphe de dépendances. Un cycle rend toute
    /// séquence de démarrage impossible : aucun des composants concernés ne peut
    /// être démarré en premier. Mieux vaut le dire au moment où la dépendance
    /// est saisie qu'un dimanche matin de reprise après incident.
    /// </summary>
    public async Task<string?> DetectCycleAsync(
        Guid environmentId, Guid componentId, Guid dependsOnId, CancellationToken ct = default)
    {
        if (componentId == dependsOnId)
            return "Un composant ne peut pas dépendre de lui-même.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composants = await db.Components
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId)
            .ToDictionaryAsync(c => c.Id, c => c.LogicalName, ct);

        var aretes = await db.ComponentDependencies
            .AsNoTracking()
            .Where(d => composants.Keys.Contains(d.ComponentId))
            .Select(d => new { d.ComponentId, d.DependsOnComponentId })
            .ToListAsync(ct);

        // Le graphe tel qu'il serait APRES ajout de la dependance proposee.
        var sortants = aretes
            .GroupBy(a => a.ComponentId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.DependsOnComponentId).ToList());

        if (!sortants.TryGetValue(componentId, out var liste))
            sortants[componentId] = liste = [];
        liste.Add(dependsOnId);

        // Parcours en profondeur depuis la cible : si l'on revient au point de
        // depart, la dependance proposee ferme un cycle.
        var chemin = new List<Guid>();
        var visites = new HashSet<Guid>();

        bool Descendre(Guid noeud)
        {
            if (noeud == componentId && chemin.Count > 0) return true;
            if (!visites.Add(noeud)) return false;

            chemin.Add(noeud);
            if (sortants.TryGetValue(noeud, out var suivants))
                foreach (var suivant in suivants)
                    if (Descendre(suivant)) return true;

            chemin.RemoveAt(chemin.Count - 1);
            return false;
        }

        if (!Descendre(dependsOnId)) return null;

        var noms = chemin.Select(id => composants.GetValueOrDefault(id, "?")).ToList();
        noms.Insert(0, composants.GetValueOrDefault(componentId, "?"));
        noms.Add(composants.GetValueOrDefault(componentId, "?"));

        return "Cette dépendance créerait un cycle : " + string.Join(" → ", noms)
             + ". Aucun de ces composants ne pourrait alors être démarré en premier.";
    }
}

/// <summary>
/// Forme minimale commune à <see cref="WorkflowStep"/> (à l'édition) et
/// <see cref="ExecutionStep"/> (au lancement réel) : tout ce dont le
/// validateur a besoin, rien de plus.
/// </summary>
internal sealed record SequenceStepInfo(int Order, Guid? ComponentId, StepAction Action, bool CanRunInParallel);

/// <summary>Incohérence relevée dans une séquence.</summary>
public sealed record SequenceViolation
{
    /// <summary>
    /// Vrai si la séquence doit être refusée. Faux si elle demande seulement
    /// une vérification — la nuance compte : tout signaler comme bloquant finit
    /// par entraîner à tout contourner.
    /// </summary>
    public bool Blocking { get; init; }

    public int Order { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Remedy { get; init; }
}
