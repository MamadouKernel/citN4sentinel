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
        await SeedKnowledgeDocumentsAsync(cancellationToken);
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

        await userManager.AddToRolesAsync(user, N4Roles.All);

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

    private async Task SeedKnowledgeDocumentsAsync(CancellationToken ct)
    {
        if (await db.Documents.AnyAsync(ct)) return;

        logger.LogInformation("Amorçage de la base de connaissances initiale N4 Sentinel...");

        var doc1 = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            Reference = "CIT-N4-ARCH-01",
            Title = "Guide d'Architecture & Supervision Navis N4 (Côte d'Ivoire Terminal)",
            Kind = DocumentKind.GuideEditeur,
            Status = LifecycleStatus.Valide,
            AppliesToVersion = "3.8.25",
            SectionCount = 2,
            PageCount = 3,
            ValidatedBy = "DSI CIT / Auditeur N4",
            ValidatedAt = DateTimeOffset.UtcNow
        };

        var doc2 = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            Reference = "CIT-N4-SOP-02",
            Title = "Procédure Opérationnelle SOP-014 — Bascule Center Node & Continuité DB",
            Kind = DocumentKind.ProcedureInterne,
            Status = LifecycleStatus.Valide,
            AppliesToVersion = "3.8.25",
            SectionCount = 2,
            PageCount = 4,
            ValidatedBy = "DSI CIT / Exploitation",
            ValidatedAt = DateTimeOffset.UtcNow
        };

        var doc3 = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            Reference = "CIT-N4-EDI-03",
            Title = "Manuel d'Intégration et Débuggage des Flux EDI (BAPLIE, CODECO, COARRI)",
            Kind = DocumentKind.ModeOperatoire,
            Status = LifecycleStatus.Valide,
            AppliesToVersion = "3.8.25",
            SectionCount = 2,
            PageCount = 5,
            ValidatedBy = "DSI CIT / Equipe EDI",
            ValidatedAt = DateTimeOffset.UtcNow
        };

        var doc4 = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            Reference = "CIT-N4-LOG-04",
            Title = "Journaux Navis N4 — Emplacements, Nomenclature et Rotation",
            Kind = DocumentKind.GuideEditeur,
            Status = LifecycleStatus.Valide,
            AppliesToVersion = "3.8.25",
            SectionCount = 3,
            PageCount = 3,
            ValidatedBy = "DSI CIT / Exploitation",
            ValidatedAt = DateTimeOffset.UtcNow
        };

        db.Documents.AddRange(doc1, doc2, doc3, doc4);
        await db.SaveChangesAsync(ct);

        var sections = new List<DocumentSection>
        {
            new()
            {
                DocumentId = doc1.Id,
                Ordinal = 1,
                PageNumber = 1,
                Heading = "Structure du Cluster N4 et rôles des nœuds",
                Content = "Le système Navis N4 déployé à Côte d'Ivoire Terminal repose sur un cluster à 4 nœuds applicatifs principaux (Cluster Node 1 à 4), un Master Center Node et un Standby Center Node pour la continuité de service. Le marqueur de démarrage du Center Node est matérialisé par la présence du fichier de verrouillage dans le dossier partagé réseau LanmanServer.",
                SearchText = "le systeme navis n4 deploye a cote d ivoire terminal repose sur un cluster a 4 noeuds applicatifs principaux cluster node 1 a 4 un master center node et un standby center node pour la continuite de service le marqueur de demarrage du center node est materialise par la presence du fichier de verrouillage dans le dossier partage reseau lanmanserver"
            },
            new()
            {
                DocumentId = doc1.Id,
                Ordinal = 2,
                PageNumber = 3,
                Heading = "Prise en charge JMX et Supervision de la mémoire Heap JVM",
                Content = "La supervision JMX surveille en temps réel l'utilisation de la mémoire Heap de la JVM Java de Navis N4. Un seuil d'alerte critique est déclenché à 85% d'occupation. En cas de dépassement prolongé supérieur à 90%, l'AIOps prédictif préconise une purge du cache ou un redémarrage ordonné du nœud impacté après bascule des sessions de guérite GOS.",
                SearchText = "la supervision jmx surveille en temps reel l utilisation de la memoire heap de la jvm java de navis n4 un seuil d alerte critique est meintenu a 85 d occupation en cas de depassement prolonge superieur a 90 l aiops predictif preconise une purge du cache ou un redemarrage ordonne du noeud impacte apres bascule des sessions de guerite gos"
            },
            new()
            {
                DocumentId = doc2.Id,
                Ordinal = 1,
                PageNumber = 1,
                Heading = "Procédure de Bascule et Redémarrage du Center Node",
                Content = "Pour basculer le Center Node Navis N4 du nœud principal vers le Standby Node, l'opérateur doit d'abord vérifier la synchronisation du dossier partagé. Ensuite, arrêter le service Windows NavisCenterMaster sur CIT-N4-MASTER, attendre la libération des verrous SQL Server, puis démarrer le service NavisCenterStandby.",
                SearchText = "pour basculer le center node navis n4 du noeud principal vers le standby node l operateur doit d abord verifier la synchronisation du dossier partage ensuite arreter le service windows naviscentermaster sur cit n4 master attendre la liberation des verrous sql server puis demarrer le service naviscenterstandby"
            },
            new()
            {
                DocumentId = doc2.Id,
                Ordinal = 2,
                PageNumber = 4,
                Heading = "Résolution des blocages de file KahaDB ActiveMQ",
                Content = "Lorsque la file de messages KahaDB ActiveMQ du bus d'événements ECN4 enregistre une accumulation d'anomalies de verrouillage, exécuter la procédure de décompaction KahaDB. Arrêter le service ECN4, purger le répertoire lock du dossier partagé, puis redémarrer le dispatcher XPS.",
                SearchText = "lorsque la file de messages kahadb activemq du bus d événements ecn4 enregistre une accumulation d anomalies de verrouillage executer la procedure de decompaction kahadb arreter le service ecn4 purger le repertoire lock du dossier partage puis redemarrer le dispatcher xps"
            },
            new()
            {
                DocumentId = doc3.Id,
                Ordinal = 1,
                PageNumber = 2,
                Heading = "Traitement et validation des fichiers EDI BAPLIE",
                Content = "Les fichiers EDI BAPLIE décrivant le plan de chargement des navires conteneurs sont reçus via le serveur SFTP sécurisé CIT-N4-APP02. Le composant NavisEdiService contrôle la conformité de la syntaxe EDIFACT UN/EDIFACT. En cas de rejet pour conteneur non référencé, la signature d'anomalie SIG-EDI-042 identifie la ligne d'erreur dans le journal applicatif.",
                SearchText = "les fichiers edi baplie decrivant le plan de chargement des navires conteneurs sont recus via le serveur sftp securise cit n4 app02 le composant navisediservice controle la conformite de la syntaxe edifact un edifact en cas de rejet pour conteneur non reference la signature d anomalie sig edi 042 identifie la ligne d erreur dans le journal applicatif"
            },
            new()
            {
                DocumentId = doc3.Id,
                Ordinal = 2,
                PageNumber = 5,
                Heading = "Supervision des interfaces Gate GOS et IKOS Billing",
                Content = "Les connecteurs GOS (Gate Operating System) et IKOS Billing communiquent avec Navis N4 via des requêtes HTTPS sécurisées et des messages XML API. La latence moyenne d'échange ne doit pas dépasser 250 ms pour éviter la formation de files d'attente de camions aux portes du terminal.",
                SearchText = "les connecteurs gos gate operating system et ikos billing communiquent avec navis n4 via des requetes https securisees et des messages xml api la latence moyenne d echange ne doit pas depasser 250 ms pour eviter la formation de files d attente de camions aux portes du terminal"
            },
            new()
            {
                DocumentId = doc4.Id,
                Ordinal = 1,
                PageNumber = 1,
                Heading = "Emplacement des journaux par composant",
                Content = "Sous Windows, chaque nœud N4 écrit ses journaux dans C:\\ProgramData\\Navis\\node{n}\\logs (sous Linux : /opt/navis/configuration/node{n}/logs). Le daemon Bridge écrit dans C:\\ProgramData\\Navis\\bridged\\logs, et XPS dans C:\\ProgramData\\Navis\\xps\\log — si XPS échoue avant le chargement de sa configuration de journalisation, il bascule temporairement sur C:\\Program Files\\xps\\. Le dossier ProgramData est masqué par défaut dans l'explorateur Windows. Les journaux clients XPS (« Sparcs N4 Client ») se trouvent sous ..\\Sparcs N4 Client\\Private\\Logs. Les journaux de base de données dépendent du moteur utilisé.",
                SearchText = "sous windows chaque noeud n4 ecrit ses journaux dans c programdata navis node n logs sous linux opt navis configuration node n logs le daemon bridge ecrit dans c programdata navis bridged logs et xps dans c programdata navis xps log si xps echoue avant le chargement de sa configuration de journalisation il bascule temporairement sur c program files xps le dossier programdata est masque par defaut dans l explorateur windows les journaux clients xps sparcs n4 client se trouvent sous sparcs n4 client private logs les journaux de base de donnees dependent du moteur utilise"
            },
            new()
            {
                DocumentId = doc4.Id,
                Ordinal = 2,
                PageNumber = 2,
                Heading = "Nomenclature des fichiers journaux",
                Content = "Le journal principal du cluster et du Center Node est navis-apex.log ; les autres composants suivent le motif navis-[service].log (navis-bridged.log pour le Bridge Daemon, navis-ecn4.log pour ECN4). XPS nomme ses fichiers xps_aaaammjjhhmmss###.log, avec un second journal xps_messages_... dédié aux communications entrantes/sortantes. Les nœuds Tomcat (hors XPS) produisent en plus [service-]stdout_aaaammjj.log (activité normale, utile pour confirmer un démarrage) et [service-]stderr_aaaammjj.log (erreurs). Pour choisir le bon fichier parmi plusieurs candidats, se fier à la date de dernière modification plutôt qu'au nom seul.",
                SearchText = "le journal principal du cluster et du center node est navis apex log les autres composants suivent le motif navis service log navis bridged log pour le bridge daemon navis ecn4 log pour ecn4 xps nomme ses fichiers xps aaaammjjhhmmss log avec un second journal xps messages dedie aux communications entrantes sortantes les noeuds tomcat hors xps produisent en plus service stdout aaaammjj log activite normale utile pour confirmer un demarrage et service stderr aaaammjj log erreurs pour choisir le bon fichier parmi plusieurs candidats se fier a la date de derniere modification plutot qu au nom seul"
            },
            new()
            {
                DocumentId = doc4.Id,
                Ordinal = 3,
                PageNumber = 3,
                Heading = "Rotation, taille et niveau de journalisation",
                Content = "La taille de rotation (MaxFileSize) et le nombre de fichiers conservés (MaxBackupIndex) se règlent dans log4j.properties ; à titre d'exemple éditeur, navis-apex.log tourne fréquemment autour de 10 Mo. Le niveau de journalisation par nœud se configure via Administration > Settings > Logging (Logging view) — ces réglages ne sont pas persistés en base et se perdent au redémarrage du service. Attention : passer com.navis.control ou com.navis.control.esb en DEBUG expose le mot de passe de la base ECI en clair dans le journal ; l'éditeur recommande de forcer explicitement com.navis.control.esb.mule.ControlDynamicMuleConfigurer=INFO dans log4j.properties pour l'éviter.",
                SearchText = "la taille de rotation maxfilesize et le nombre de fichiers conserves maxbackupindex se reglent dans log4j properties a titre d exemple editeur navis apex log tourne frequemment autour de 10 mo le niveau de journalisation par noeud se configure via administration settings logging logging view ces reglages ne sont pas persistes en base et se perdent au redemarrage du service attention passer com navis control ou com navis control esb en debug expose le mot de passe de la base eci en clair dans le journal l editeur recommande de forcer explicitement com navis control esb mule controldynamicmuleconfigurer info dans log4j properties pour l eviter"
            }
        };

        db.DocumentSections.AddRange(sections);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Base de connaissances initiale amorcée avec succès (4 documents, 9 sections).");
    }
}
