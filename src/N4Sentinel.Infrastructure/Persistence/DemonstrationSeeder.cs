using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;

namespace N4Sentinel.Infrastructure.Persistence;

/// <summary>
/// Crée un environnement de démonstration complet, pointé sur le simulateur N4.
///
/// À quoi cela sert : sans jeu de données, découvrir l'application impose de
/// saisir sept composants, leurs chemins de journaux et leurs marqueurs avant
/// de voir le moindre écran peuplé. Cela décourage l'évaluation, et rend toute
/// démonstration laborieuse.
///
/// L'environnement créé est de type Test et pointe sur la machine locale : il
/// ne peut donc rien atteindre en production, même par erreur de manipulation.
/// Il est supprimable d'un appel.
/// </summary>
public sealed class DemonstrationSeeder(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<DemonstrationSeeder> logger)
{
    public const string CodeEnvironnement = "DEMO";

    /// <summary>
    /// Crée l'environnement de démonstration. Sans effet s'il existe déjà.
    /// </summary>
    /// <param name="racineSimulateur">
    /// Racine du simulateur, telle que passée à New-N4Simulateur.ps1.
    /// Les chemins de journaux en découlent.
    /// </param>
    public async Task<Guid?> SeedAsync(
        string racineSimulateur = @"C:\N4Simulateur", CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.Environments.AnyAsync(e => e.Code == CodeEnvironnement, ct))
        {
            logger.LogInformation("L'environnement de démonstration existe déjà.");
            return null;
        }

        var env = new N4Environment
        {
            Code = CodeEnvironnement,
            Name = "Démonstration sur simulateur N4",
            Kind = EnvironmentKind.Test,
            Criticality = CriticalityLevel.Faible,
            Status = LifecycleStatus.Brouillon,
            Description = "Environnement de découverte, pointé sur le simulateur local. "
                        + "Ne désigne aucun serveur réel. Supprimable sans conséquence.",
            TechnicalOwner = "Équipe Solutions IT"
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync(ct);

        // Un seul serveur : la machine locale. Le simulateur n'écrit que des
        // journaux, il ne crée pas de machines.
        var serveur = new N4Server
        {
            EnvironmentId = env.Id,
            HostName = Environment.MachineName,
            Status = LifecycleStatus.Brouillon,
            Criticality = CriticalityLevel.Faible,
            Description = "Machine hébergeant le simulateur. Interrogée en local, sans WinRM."
        };
        db.Servers.Add(serveur);
        await db.SaveChangesAsync(ct);

        var composants = new List<N4Component>();
        var ordre = 0;

        // Les nœuds Cluster d'abord, un par un : c'est la contrainte N4 que
        // l'ordre de démarrage doit refléter.
        for (var i = 1; i <= 2; i++)
        {
            composants.Add(Creer(
                env.Id, serveur.Id, $"Cluster Node {i}", ComponentRole.ClusterNode,
                $"N4Sim Cluster Node {i}", ++ordre,
                Path.Combine(racineSimulateur, "ProgramData", "Navis", $"cluster{i}", "logs", "navis-apex.log"),
                [@"Web tier servlet 'action' initialized in \d+ ms"],
                CriticalityLevel.Critique));
        }

        composants.Add(Creer(
            env.Id, serveur.Id, "Center Node", ComponentRole.CenterNode,
            "N4Sim Center Node", ++ordre,
            Path.Combine(racineSimulateur, "ProgramData", "Navis", "center", "logs", "navis-apex.log"),
            [@"Web tier servlet 'action' initialized in \d+ ms"],
            CriticalityLevel.Critique));

        // Le Standby n'écrit PAS le marqueur du tier web : c'est le
        // comportement normal d'une instance en veille, pas un défaut.
        composants.Add(Creer(
            env.Id, serveur.Id, "Standby Center Node", ComponentRole.StandbyCenterNode,
            "N4Sim Standby Center Node", ++ordre,
            Path.Combine(racineSimulateur, "ProgramData", "Navis", "standby", "logs", "navis-apex.log"),
            [@"Standby mode active - waiting for master lock"],
            CriticalityLevel.Elevee));

        composants.Add(Creer(
            env.Id, serveur.Id, "XPS Bridge Daemon", ComponentRole.BridgeDaemon,
            "N4Sim XPS Bridge Daemon", ++ordre,
            Path.Combine(racineSimulateur, "ProgramData", "Navis", "bridge", "logs", "navis-bridged_*.log"),
            [@"Connection established to Center node - bridge is ACTIVE"],
            CriticalityLevel.Elevee));

        // Le journal XPS est horodaté dans son nom et recréé à chaque
        // démarrage : d'où le caractère générique.
        composants.Add(Creer(
            env.Id, serveur.Id, "Service XPS", ComponentRole.Xps,
            "N4Sim XPS Service", ++ordre,
            Path.Combine(racineSimulateur, "ProgramData", "Navis", "xps", "log", "xps_*.log"),
            [@"XPS initialization complete - \d+ equipment loaded"],
            CriticalityLevel.Elevee));

        composants.Add(Creer(
            env.Id, serveur.Id, "ECN4 Daemon", ComponentRole.Ecn4,
            "N4Sim ECN4 Daemon", ++ordre,
            Path.Combine(racineSimulateur, "ProgramData", "Navis", "ecn4", "logs", "navis-ecn4_*.log"),
            [@"ECN4 startup complete - listening for equipment"],
            CriticalityLevel.Moyenne));

        composants.Add(Creer(
            env.Id, serveur.Id, "ECN4Web", ComponentRole.Ecn4Web,
            "N4Sim ECN4web", ++ordre,
            Path.Combine(racineSimulateur, "ProgramData", "Navis", "ecn4web", "logs", "navis-ecn4web_*.log"),
            [@"Server startup in \d+ ms"],
            CriticalityLevel.Moyenne));

        db.Components.AddRange(composants);
        await db.SaveChangesAsync(ct);

        // Dépendances : la règle N4 que les séquences devront respecter.
        var parRole = composants.ToDictionary(c => c.LogicalName);
        var dependances = new List<ComponentDependency>
        {
            Lier(parRole["Center Node"], parRole["Cluster Node 1"]),
            Lier(parRole["Center Node"], parRole["Cluster Node 2"]),
            Lier(parRole["XPS Bridge Daemon"], parRole["Center Node"]),
            // XPS ne démarre que si le Bridge est prouvé opérationnel : c'est
            // la dépendance la plus importante de l'écosystème.
            Lier(parRole["Service XPS"], parRole["XPS Bridge Daemon"]),
            Lier(parRole["ECN4 Daemon"], parRole["Service XPS"]),
            Lier(parRole["ECN4Web"], parRole["ECN4 Daemon"])
        };

        db.ComponentDependencies.AddRange(dependances);
        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Environnement de démonstration créé : {Composants} composants, {Dependances} dépendances, "
            + "pointé sur {Racine}. Il reste en Brouillon : aucune opération n'y est possible avant validation.",
            composants.Count, dependances.Count, racineSimulateur);

        return env.Id;
    }

    /// <summary>Supprime l'environnement de démonstration et tout ce qu'il porte.</summary>
    public async Task<bool> RemoveAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var env = await db.Environments.FirstOrDefaultAsync(e => e.Code == CodeEnvironnement, ct);
        if (env is null) return false;

        // Les dépendances ne se suppriment pas en cascade depuis la cible :
        // on les retire explicitement avant les composants.
        var composants = await db.Components.Where(c => c.EnvironmentId == env.Id).Select(c => c.Id).ToListAsync(ct);
        var liens = await db.ComponentDependencies
            .Where(d => composants.Contains(d.ComponentId) || composants.Contains(d.DependsOnComponentId))
            .ToListAsync(ct);

        db.ComponentDependencies.RemoveRange(liens);
        await db.SaveChangesAsync(ct);

        db.Environments.Remove(env);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Environnement de démonstration supprimé.");
        return true;
    }

    private static N4Component Creer(
        Guid envId, Guid serverId, string nom, ComponentRole role, string service,
        int ordre, string journal, List<string> marqueurs, CriticalityLevel criticite) => new()
    {
        EnvironmentId = envId,
        ServerId = serverId,
        LogicalName = nom,
        Role = role,
        WindowsServiceName = service,
        ControlMode = ControlMode.Pilotable,
        Criticality = criticite,
        Status = LifecycleStatus.Brouillon,
        StartOrder = ordre,
        Description = "Composant de démonstration, pointé sur le simulateur.",
        Readiness = new ReadinessProfile
        {
            LogPath = journal,
            ReadyPatterns = marqueurs,
            ErrorPatterns =
            [
                "OutOfMemoryError", "NegativeArraySizeException",
                "FATAL", "Unable to (start|connect)", "SocketTimeoutException"
            ],
            LogReadyTimeoutSeconds = 300,
            PollIntervalSeconds = 5,
            ProgressEverySeconds = 30
        }
    };

    private static ComponentDependency Lier(N4Component composant, N4Component dependDe) => new()
    {
        ComponentId = composant.Id,
        DependsOnComponentId = dependDe.Id,
        Kind = DependencyKind.RequisAuDemarrage,
        Notes = $"{composant.LogicalName} exige que {dependDe.LogicalName} soit opérationnel."
    };
}
