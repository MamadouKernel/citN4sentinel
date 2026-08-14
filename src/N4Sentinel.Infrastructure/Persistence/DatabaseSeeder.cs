using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Identity;

namespace N4Sentinel.Infrastructure.Persistence;

/// <summary>
/// Amorce la base : applique les migrations en attente, crée les huit rôles
/// du cahier des charges, crée l'administrateur et initialise l'écosystème Navis N4.
/// </summary>
public sealed class DatabaseSeeder(
    N4SentinelDbContext db,
    RoleManager<IdentityRole> roleManager,
    UserManager<ApplicationUser> userManager,
    IConfiguration configuration,
    ILogger<DatabaseSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Application de {Count} migration(s) : {Migrations}",
                pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(cancellationToken);
        }

        await SeedRolesAsync();
        await SeedFirstAdministratorAsync();
        await SeedTopologyDataAsync(cancellationToken);
        await SeedDiagnosticSignaturesAsync(cancellationToken);
    }

    /// <summary>
    /// Amorce le catalogue de signatures de diagnostic.
    ///
    /// N'ECRASE JAMAIS une signature existante : un exploitant qui a corrige
    /// une expression reguliere parce qu'elle produisait des faux positifs sur
    /// son site ne doit pas voir sa correction disparaitre au redemarrage.
    /// </summary>
    private async Task SeedDiagnosticSignaturesAsync(CancellationToken ct)
    {
        var codesExistants = await db.Signatures.Select(s => s.Code).ToListAsync(ct);

        var aAjouter = Diagnostic.SignatureCatalogue.Livrees()
            .Where(s => !codesExistants.Contains(s.Code))
            .ToList();

        if (aAjouter.Count == 0) return;

        db.Signatures.AddRange(aAjouter);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{Nombre} signature(s) de diagnostic amorcée(s) depuis la documentation éditeur.", aAjouter.Count);
    }

    private async Task SeedRolesAsync()
    {
        foreach (var role in N4Roles.All)
        {
            if (await roleManager.RoleExistsAsync(role)) continue;

            var result = await roleManager.CreateAsync(new IdentityRole(role));
            if (result.Succeeded)
                logger.LogInformation("Role cree : {Role}", role);
            else
                logger.LogError("Echec de creation du role {Role} : {Erreurs}",
                    role, string.Join(" | ", result.Errors.Select(e => e.Description)));
        }
    }

    private async Task SeedFirstAdministratorAsync()
    {
        var email = configuration["N4Sentinel:FirstAdmin:Email"];
        var password = configuration["N4Sentinel:FirstAdmin:Password"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogDebug("Aucun administrateur d'amorcage configure - etape ignoree.");
            return;
        }

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            logger.LogDebug("L'administrateur d'amorcage existe deja - aucune action.");
            return;
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Administrateur de la solution",
            Department = "DSI - Solutions IT et Projets"
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            logger.LogError("Echec de creation de l'administrateur d'amorcage : {Erreurs}",
                string.Join(" | ", created.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRolesAsync(user, [N4Roles.AdministrateurSolution, N4Roles.Auditeur]);

        logger.LogWarning(
            "Administrateur d'amorcage cree pour {Email}. Changez son mot de passe des la premiere connexion.", email);
    }

    private async Task SeedTopologyDataAsync(CancellationToken ct)
    {
        if (await db.Environments.AnyAsync(ct)) return;

        logger.LogInformation("Amorçage de la topologie initiale Navis N4 CIT...");

        var envProd = new N4Environment
        {
            Code = "PRD-N4",
            Name = "Production Navis N4 — Côte d'Ivoire Terminal",
            Kind = EnvironmentKind.Production,
            Status = LifecycleStatus.Actif,
            Criticality = CriticalityLevel.Critique,
            TimeZoneId = "Africa/Abidjan"
        };

        var envUat = new N4Environment
        {
            Code = "UAT-N4",
            Name = "Recette & Qualifications UAT — CIT",
            Kind = EnvironmentKind.UAT,
            Status = LifecycleStatus.Actif,
            Criticality = CriticalityLevel.Moyenne,
            TimeZoneId = "Africa/Abidjan"
        };

        db.Environments.AddRange(envProd, envUat);
        await db.SaveChangesAsync(ct);

        // Serveurs Nœuds pour PRD-N4
        var srvApp1 = new N4Server { EnvironmentId = envProd.Id, HostName = "CIT-N4-APP01", IpAddress = "10.150.10.11", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvApp2 = new N4Server { EnvironmentId = envProd.Id, HostName = "CIT-N4-APP02", IpAddress = "10.150.10.12", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvDb1 = new N4Server { EnvironmentId = envProd.Id, HostName = "CIT-N4-DB01", IpAddress = "10.150.10.21", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvDb2 = new N4Server { EnvironmentId = envProd.Id, HostName = "CIT-N4-DB02", IpAddress = "10.150.10.22", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvCenter = new N4Server { EnvironmentId = envProd.Id, HostName = "CIT-N4-MASTER", IpAddress = "10.150.10.30", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvXps = new N4Server { EnvironmentId = envProd.Id, HostName = "CIT-N4-XPS01", IpAddress = "10.150.10.40", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };

        db.Servers.AddRange(srvApp1, srvApp2, srvDb1, srvDb2, srvCenter, srvXps);
        await db.SaveChangesAsync(ct);

        // Composants selon le schéma fonctionnel Navis N4
        var components = new List<N4Component>
        {
            // 1. CLUSTER NODES (Nœuds de Calcul & Applicatifs)
            new() { EnvironmentId = envProd.Id, ServerId = srvApp1.Id, LogicalName = "Cluster Node 1", WindowsServiceName = "NavisN4Node1", Role = ComponentRole.ClusterNode, StartOrder = 1, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp1.Id, LogicalName = "Cluster Node 2", WindowsServiceName = "NavisN4Node2", Role = ComponentRole.ClusterNode, StartOrder = 2, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp2.Id, LogicalName = "Cluster Node 3", WindowsServiceName = "NavisN4Node3", Role = ComponentRole.ClusterNode, StartOrder = 3, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp2.Id, LogicalName = "Cluster Node 4", WindowsServiceName = "NavisN4Node4", Role = ComponentRole.ClusterNode, StartOrder = 4, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp1.Id, LogicalName = "Gate Node", WindowsServiceName = "NavisGateService", Role = ComponentRole.ClusterNode, StartOrder = 5, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp2.Id, LogicalName = "EDI Node", WindowsServiceName = "NavisEdiService", Role = ComponentRole.InterfaceEdi, StartOrder = 6, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp1.Id, LogicalName = "SmartAccess Node", WindowsServiceName = "NavisSmartAccess", Role = ComponentRole.ClusterNode, StartOrder = 7, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp2.Id, LogicalName = "Partenaire Externe", WindowsServiceName = "NavisExternalPartner", Role = ComponentRole.ClusterNode, StartOrder = 8, ControlMode = ControlMode.SuperviseSeulement },

            // 2. CLUSTER DATABASE (Bases de données)
            new() { EnvironmentId = envProd.Id, ServerId = srvDb1.Id, LogicalName = "Database Host", WindowsServiceName = "MSSQLSERVER", Role = ComponentRole.BaseDeDonnees, StartOrder = 0, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = envProd.Id, ServerId = srvDb2.Id, LogicalName = "Database Host (Replicated)", WindowsServiceName = "MSSQLSERVER", Role = ComponentRole.BaseDeDonnees, StartOrder = 0, ControlMode = ControlMode.SuperviseSeulement },

            // 3. MASTER / STANDBY NODE
            new() { EnvironmentId = envProd.Id, ServerId = srvCenter.Id, LogicalName = "Master Node", WindowsServiceName = "NavisCenterMaster", Role = ComponentRole.CenterNode, StartOrder = 1, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvCenter.Id, LogicalName = "Standby Node", WindowsServiceName = "NavisCenterStandby", Role = ComponentRole.StandbyCenterNode, StartOrder = 2, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvCenter.Id, LogicalName = "Share Folder", WindowsServiceName = "LanmanServer", Role = ComponentRole.DossierPartage, StartOrder = 0, ControlMode = ControlMode.SuperviseSeulement },

            // 4. MODULES XPS / ECN
            new() { EnvironmentId = envProd.Id, ServerId = srvXps.Id, LogicalName = "XPS Server", WindowsServiceName = "XpsServerEngine", Role = ComponentRole.Xps, StartOrder = 10, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvXps.Id, LogicalName = "Docing", WindowsServiceName = "XpsDocingService", Role = ComponentRole.Xps, StartOrder = 11, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvXps.Id, LogicalName = "Dispatcher", WindowsServiceName = "XpsDispatcherService", Role = ComponentRole.Xps, StartOrder = 12, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvXps.Id, LogicalName = "ECN4", WindowsServiceName = "ECN4Service", Role = ComponentRole.Ecn4, StartOrder = 13, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = envProd.Id, ServerId = srvXps.Id, LogicalName = "ECN4 Web", WindowsServiceName = "W3SVC", Role = ComponentRole.Ecn4Web, StartOrder = 14, ControlMode = ControlMode.Pilotable },

            // 5. INTERFACES & ÉCOSYSTÈME EXTERNE
            new() { EnvironmentId = envProd.Id, ServerId = srvApp1.Id, LogicalName = "Gate Operating System (GOS)", WindowsServiceName = "GosConnector", Role = ComponentRole.SystemeExterne, StartOrder = 20, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp2.Id, LogicalName = "Server SFTP (EDI)", WindowsServiceName = "SftpEdiService", Role = ComponentRole.SystemeExterne, StartOrder = 21, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp1.Id, LogicalName = "Billing System (IKOS)", WindowsServiceName = "IkosConnector", Role = ComponentRole.SystemeExterne, StartOrder = 22, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp2.Id, LogicalName = "Vehicle Booking system", WindowsServiceName = "VbsConnector", Role = ComponentRole.SystemeExterne, StartOrder = 23, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp1.Id, LogicalName = "Hyperion", WindowsServiceName = "HyperionConnector", Role = ComponentRole.SystemeExterne, StartOrder = 24, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = envProd.Id, ServerId = srvApp2.Id, LogicalName = "Reefer Runner", WindowsServiceName = "ReeferConnector", Role = ComponentRole.SystemeExterne, StartOrder = 25, ControlMode = ControlMode.SuperviseSeulement }
        };

        db.Components.AddRange(components);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Topologie Navis N4 amorcée avec succès ({Count} composants).", components.Count);
    }
}
