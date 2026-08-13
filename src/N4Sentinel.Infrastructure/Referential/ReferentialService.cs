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

    public async Task SeedDefaultTopologyForEnvironmentAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var env = await db.Environments.FirstOrDefaultAsync(e => e.Id == environmentId, ct);
        if (env is null) return;

        if (await db.Components.AnyAsync(c => c.EnvironmentId == environmentId, ct)) return;

        var prefix = env.Code.Replace("-", "").ToUpperInvariant();
        var srvApp1 = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-APP01", IpAddress = "10.150.10.11", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvApp2 = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-APP02", IpAddress = "10.150.10.12", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvDb1 = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-DB01", IpAddress = "10.150.10.21", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvDb2 = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-DB02", IpAddress = "10.150.10.22", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvCenter = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-MASTER", IpAddress = "10.150.10.30", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvXps = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-XPS01", IpAddress = "10.150.10.40", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };

        db.Servers.AddRange(srvApp1, srvApp2, srvDb1, srvDb2, srvCenter, srvXps);
        await db.SaveChangesAsync(ct);

        var components = new List<N4Component>
        {
            // 1. CLUSTER NODES
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Cluster Node 1", WindowsServiceName = "NavisN4Node1", Role = ComponentRole.ClusterNode, StartOrder = 1, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Cluster Node 2", WindowsServiceName = "NavisN4Node2", Role = ComponentRole.ClusterNode, StartOrder = 2, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Cluster Node 3", WindowsServiceName = "NavisN4Node3", Role = ComponentRole.ClusterNode, StartOrder = 3, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Cluster Node 4", WindowsServiceName = "NavisN4Node4", Role = ComponentRole.ClusterNode, StartOrder = 4, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Gate Node", WindowsServiceName = "NavisGateService", Role = ComponentRole.ClusterNode, StartOrder = 5, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "EDI Node", WindowsServiceName = "NavisEdiService", Role = ComponentRole.InterfaceEdi, StartOrder = 6, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "SmartAccess Node", WindowsServiceName = "NavisSmartAccess", Role = ComponentRole.ClusterNode, StartOrder = 7, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Partenaire Externe", WindowsServiceName = "NavisExternalPartner", Role = ComponentRole.ClusterNode, StartOrder = 8, ControlMode = ControlMode.SuperviseSeulement },

            // 2. CLUSTER DATABASE
            new() { EnvironmentId = env.Id, ServerId = srvDb1.Id, LogicalName = "Database Host", WindowsServiceName = "MSSQLSERVER", Role = ComponentRole.BaseDeDonnees, StartOrder = 0, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvDb2.Id, LogicalName = "Database Host (Replicated)", WindowsServiceName = "MSSQLSERVER", Role = ComponentRole.BaseDeDonnees, StartOrder = 0, ControlMode = ControlMode.SuperviseSeulement },

            // 3. MASTER / STANDBY NODE
            new() { EnvironmentId = env.Id, ServerId = srvCenter.Id, LogicalName = "Master Node", WindowsServiceName = "NavisCenterMaster", Role = ComponentRole.CenterNode, StartOrder = 1, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvCenter.Id, LogicalName = "Standby Node", WindowsServiceName = "NavisCenterStandby", Role = ComponentRole.StandbyCenterNode, StartOrder = 2, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvCenter.Id, LogicalName = "Share Folder", WindowsServiceName = "LanmanServer", Role = ComponentRole.DossierPartage, StartOrder = 0, ControlMode = ControlMode.SuperviseSeulement },

            // 4. MODULES XPS / ECN
            new() { EnvironmentId = env.Id, ServerId = srvXps.Id, LogicalName = "XPS Server", WindowsServiceName = "XpsServerEngine", Role = ComponentRole.Xps, StartOrder = 10, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvXps.Id, LogicalName = "Docing", WindowsServiceName = "XpsDocingService", Role = ComponentRole.Xps, StartOrder = 11, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvXps.Id, LogicalName = "Dispatcher", WindowsServiceName = "XpsDispatcherService", Role = ComponentRole.Xps, StartOrder = 12, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvXps.Id, LogicalName = "ECN4", WindowsServiceName = "ECN4Service", Role = ComponentRole.Ecn4, StartOrder = 13, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvXps.Id, LogicalName = "ECN4 Web", WindowsServiceName = "W3SVC", Role = ComponentRole.Ecn4Web, StartOrder = 14, ControlMode = ControlMode.Pilotable },

            // 5. INTERFACES & ÉCOSYSTÈME EXTERNE
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Gate Operating System (GOS)", WindowsServiceName = "GosConnector", Role = ComponentRole.SystemeExterne, StartOrder = 20, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Server SFTP (EDI)", WindowsServiceName = "SftpEdiService", Role = ComponentRole.SystemeExterne, StartOrder = 21, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Billing System (IKOS)", WindowsServiceName = "IkosConnector", Role = ComponentRole.SystemeExterne, StartOrder = 22, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Vehicle Booking system", WindowsServiceName = "VbsConnector", Role = ComponentRole.SystemeExterne, StartOrder = 23, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Hyperion", WindowsServiceName = "HyperionConnector", Role = ComponentRole.SystemeExterne, StartOrder = 24, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Reefer Runner", WindowsServiceName = "ReeferConnector", Role = ComponentRole.SystemeExterne, StartOrder = 25, ControlMode = ControlMode.SuperviseSeulement }
        };

        db.Components.AddRange(components);
        await db.SaveChangesAsync(ct);
    }
}
