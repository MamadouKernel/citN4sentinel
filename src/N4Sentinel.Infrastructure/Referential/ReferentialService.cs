using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Referential;

/// <summary>
/// Operations du referentiel technique : environnements, serveurs, composants.
///
/// Porte les regles du cycle de validation (FR-006) : le passage d'un statut a
/// l'autre n'est pas libre, et une regression volontaire vers Brouillon reste
/// possible tant que rien n'a ete execute sur l'objet.
/// </summary>
public sealed class ReferentialService(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    // -----------------------------------------------------------------------
    // Lecture
    // -----------------------------------------------------------------------
    public async Task<List<N4Environment>> GetEnvironmentsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Environments
            .AsNoTracking()
            .OrderBy(e => e.Kind).ThenBy(e => e.Code)
            .ToListAsync(ct);
    }

    public async Task<N4Environment?> GetEnvironmentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Environments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<List<N4Server>> GetServersAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Servers
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId)
            .OrderBy(s => s.HostName)
            .ToListAsync(ct);
    }

    public async Task<N4Server?> GetServerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Servers
            .Include(s => s.Environment)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>
    /// Composants heberges par un serveur. Sert a montrer, avant suppression,
    /// ce qui deviendrait orphelin.
    /// </summary>
    public async Task<List<N4Component>> GetComponentsOnServerAsync(Guid serverId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Components
            .AsNoTracking()
            .Where(c => c.ServerId == serverId)
            .OrderBy(c => c.StartOrder).ThenBy(c => c.LogicalName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Suppression d'un serveur. Refusee tant qu'il porte des composants :
    /// les detacher en silence produirait des composants sans hote, donc
    /// injoignables, sans que personne ne l'ait decide.
    /// </summary>
    public async Task<string?> DeleteServerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var portes = await db.Components
            .Where(c => c.ServerId == id)
            .Select(c => c.LogicalName)
            .ToListAsync(ct);

        if (portes.Count > 0)
            return $"Suppression refusee : ce serveur heberge {string.Join(", ", portes)}. " +
                   "Reaffectez ou supprimez ces composants d'abord.";

        var serveur = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (serveur is null) return "Serveur introuvable.";

        db.Servers.Remove(serveur);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<List<N4Component>> GetComponentsAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Components
            .AsNoTracking()
            .Include(c => c.Server)
            .Where(c => c.EnvironmentId == environmentId)
            .OrderBy(c => c.StartOrder).ThenBy(c => c.LogicalName)
            .ToListAsync(ct);
    }

    public async Task<N4Component?> GetComponentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Components
            .Include(c => c.Server)
            .Include(c => c.Environment)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<int> CountComponentsAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Components.CountAsync(c => c.EnvironmentId == environmentId, ct);
    }

    // -----------------------------------------------------------------------
    // Ecriture
    // -----------------------------------------------------------------------
    // NOTE IMPORTANTE SUR LA MISE A JOUR
    // On recharge l'entite existante et on lui applique les valeurs, plutot
    // que d'appeler Update() sur un objet detache. Update() marque TOUTES les
    // proprietes comme modifiees, meme celles qui n'ont pas bouge : le journal
    // d'audit enregistrerait alors l'objet entier a chaque enregistrement,
    // et deviendrait illisible. Avec SetValues, EF ne retient que les
    // differences reelles.

    public async Task SaveEnvironmentAsync(N4Environment environment, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Environments.FirstOrDefaultAsync(e => e.Id == environment.Id, ct);

        if (existing is null) db.Environments.Add(environment);
        else db.Entry(existing).CurrentValues.SetValues(environment);

        await db.SaveChangesAsync(ct);
    }

    public async Task SaveServerAsync(N4Server server, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Servers.FirstOrDefaultAsync(s => s.Id == server.Id, ct);

        if (existing is null) db.Servers.Add(server);
        else db.Entry(existing).CurrentValues.SetValues(server);

        await db.SaveChangesAsync(ct);
    }

    public async Task SaveComponentAsync(N4Component component, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Components.FirstOrDefaultAsync(c => c.Id == component.Id, ct);

        if (existing is null)
        {
            db.Components.Add(component);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(component);

            // Le profil de demarrage est un type possede : ses valeurs se
            // reportent separement, sinon les marqueurs et delais edites a
            // l'ecran seraient silencieusement perdus.
            var readiness = db.Entry(existing).Reference(c => c.Readiness).TargetEntry;
            if (readiness is not null)
                readiness.CurrentValues.SetValues(component.Readiness);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Suppression d'un composant. Refusee si un autre composant en depend :
    /// casser le graphe de dependances en silence produirait des sequences
    /// incoherentes plus tard.
    /// </summary>
    public async Task<string?> DeleteComponentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var dependents = await db.ComponentDependencies
            .Where(d => d.DependsOnComponentId == id)
            .Select(d => d.Component!.LogicalName)
            .ToListAsync(ct);

        if (dependents.Count > 0)
            return $"Suppression refusee : {string.Join(", ", dependents)} depend(ent) de ce composant.";

        var component = await db.Components.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (component is null) return "Composant introuvable.";

        db.Components.Remove(component);
        await db.SaveChangesAsync(ct);
        return null;
    }

    // -----------------------------------------------------------------------
    // Cycle de validation (FR-006)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Transitions autorisees. Le cycle n'est pas lineaire : on peut renvoyer
    /// un objet en Brouillon pour le corriger, et desactiver depuis n'importe
    /// quel statut valide.
    /// </summary>
    public static IReadOnlyList<LifecycleStatus> AllowedTransitions(LifecycleStatus current) => current switch
    {
        LifecycleStatus.Brouillon => [LifecycleStatus.AValider],
        LifecycleStatus.AValider => [LifecycleStatus.Valide, LifecycleStatus.Brouillon],
        LifecycleStatus.Valide => [LifecycleStatus.Actif, LifecycleStatus.Brouillon, LifecycleStatus.Desactive],
        LifecycleStatus.Actif => [LifecycleStatus.Desactive],
        LifecycleStatus.Desactive => [LifecycleStatus.Valide],
        _ => []
    };

    /// <summary>
    /// Fait evoluer le statut d'un environnement, en verifiant que la
    /// transition est permise ET que l'activation repose sur un referentiel
    /// reellement renseigne.
    /// </summary>
    public async Task<string?> ChangeEnvironmentStatusAsync(
        Guid id, LifecycleStatus target, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environment = await db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (environment is null) return "Environnement introuvable.";

        if (!AllowedTransitions(environment.Status).Contains(target))
            return $"Transition refusee : {environment.Status} ne peut pas passer directement a {target}.";

        if (target == LifecycleStatus.Actif)
        {
            var componentCount = await db.Components.CountAsync(c => c.EnvironmentId == id, ct);
            if (componentCount == 0)
                return "Activation refusee : aucun composant declare dans cet environnement.";

            var orphans = await db.Components
                .Where(c => c.EnvironmentId == id
                         && c.ControlMode == ControlMode.Pilotable
                         && (c.WindowsServiceName == null || c.WindowsServiceName == ""))
                .Select(c => c.LogicalName)
                .ToListAsync(ct);

            if (orphans.Count > 0)
                return $"Activation refusee : composant(s) pilotable(s) sans nom de service Windows - {string.Join(", ", orphans)}.";
        }

        environment.Status = target;
        await db.SaveChangesAsync(ct);
        return null;
    }

    // -----------------------------------------------------------------------
    // Audit
    // -----------------------------------------------------------------------
    public async Task<List<AuditEntry>> GetAuditEntriesAsync(
        Guid? environmentId = null, int take = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.AuditEntries.AsNoTracking();
        if (environmentId.HasValue)
            query = query.Where(a => a.EnvironmentId == environmentId.Value);

        return await query
            .OrderByDescending(a => a.OccurredAt)
            .Take(take)
            .ToListAsync(ct);
    }
}
