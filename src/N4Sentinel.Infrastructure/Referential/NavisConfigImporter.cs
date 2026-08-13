using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Referential;

/// <summary>
/// Importe un fichier Navis-Config.json des scripts PowerShell d'exploitation
/// dans le referentiel.
///
/// Interet : ce fichier decrit deja l'ecosysteme reel - noms d'hotes, noms
/// exacts des services Windows, chemins de journaux, marqueurs de demarrage et
/// delais. Le ressaisir a la main dans l'interface serait a la fois long et
/// source d'ecarts entre ce que pilotent les scripts et ce que croit
/// l'application.
///
/// Les composants importes arrivent en statut Brouillon : l'import alimente le
/// referentiel, il ne le valide pas. La validation reste un acte humain.
///
/// Particularite geree nativement : chez CIT, Bridge et XPS partagent le meme
/// serveur physique. Les hotes sont dedupliques, comme le font les scripts.
/// </summary>
public sealed class NavisConfigImporter(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    public async Task<ImportReport> ImportAsync(
        Guid environmentId, string json, CancellationToken ct = default)
    {
        var report = new ImportReport();

        NavisConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<NavisConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            report.Errors.Add($"Fichier illisible : {ex.Message}");
            return report;
        }

        if (config is null)
        {
            report.Errors.Add("Fichier vide ou non exploitable.");
            return report;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environment = await db.Environments.FirstOrDefaultAsync(e => e.Id == environmentId, ct);
        if (environment is null)
        {
            report.Errors.Add("Environnement cible introuvable.");
            return report;
        }

        // --- Serveurs, dedupliques ------------------------------------------
        var hostNames = new List<string?>
            { config.CenterNode, config.StandbyNode, config.BridgeHost, config.XPSHost, config.ECN4Host };
        if (config.ClusterNodes is not null) hostNames.AddRange(config.ClusterNodes);

        var uniqueHosts = hostNames
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingServers = await db.Servers
            .Where(s => s.EnvironmentId == environmentId)
            .ToDictionaryAsync(s => s.HostName, StringComparer.OrdinalIgnoreCase, ct);

        var serversByHost = new Dictionary<string, N4Server>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in uniqueHosts)
        {
            if (existingServers.TryGetValue(host, out var existing))
            {
                serversByHost[host] = existing;
                report.ServersSkipped++;
                continue;
            }

            var server = new N4Server
            {
                EnvironmentId = environmentId,
                HostName = host,
                Status = LifecycleStatus.Brouillon,
                Description = "Importe depuis Navis-Config.json"
            };
            db.Servers.Add(server);
            serversByHost[host] = server;
            report.ServersCreated++;
        }

        // --- Composants -----------------------------------------------------
        var existingNames = await db.Components
            .Where(c => c.EnvironmentId == environmentId)
            .Select(c => c.LogicalName)
            .ToListAsync(ct);
        var known = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        var order = 0;

        // Les noeuds Cluster demarrent en premier, un par un.
        if (config.ClusterNodes is not null)
        {
            var index = 0;
            foreach (var node in config.ClusterNodes.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                index++;
                AddComponent($"Cluster Node {index} ({node})", ComponentRole.ClusterNode,
                    config.ServiceNames?.Cluster, node, "Cluster", ++order);
            }
        }

        AddComponent("Center Node", ComponentRole.CenterNode,
            config.ServiceNames?.Center, config.CenterNode, "Center", ++order);

        AddComponent("Standby Center Node", ComponentRole.StandbyCenterNode,
            config.ServiceNames?.Standby, config.StandbyNode, "Standby", ++order);

        AddComponent("XPS Bridge Daemon", ComponentRole.BridgeDaemon,
            config.ServiceNames?.Bridge, config.BridgeHost, "Bridge", ++order);

        AddComponent("Service XPS", ComponentRole.Xps,
            config.ServiceNames?.XPS, config.XPSHost, "XPS", ++order);

        AddComponent("ECN4 Daemon", ComponentRole.Ecn4,
            config.ServiceNames?.ECN4, config.ECN4Host, "ECN4", ++order);

        AddComponent("ECN4Web", ComponentRole.Ecn4Web,
            config.ServiceNames?.ECN4Web, config.ECN4Host, "ECN4Web", ++order);

        // La base de donnees est supervisee, jamais pilotee : N4 Sentinel n'a
        // pas vocation a demarrer ou arreter un serveur SQL.
        if (!string.IsNullOrWhiteSpace(config.DatabaseHost) && !known.Contains("Base de donnees N4"))
        {
            db.Components.Add(new N4Component
            {
                EnvironmentId = environmentId,
                LogicalName = "Base de donnees N4",
                Role = ComponentRole.BaseDeDonnees,
                ControlMode = ControlMode.SuperviseSeulement,
                Criticality = CriticalityLevel.Critique,
                Status = LifecycleStatus.Brouillon,
                Port = config.DatabasePort,
                StartOrder = 0,
                Description = $"{config.DatabaseEngine} sur {config.DatabaseHost}. Supervision uniquement - toute investigation approfondie reste du ressort du DBA."
            });
            report.ComponentsCreated++;
        }

        if (!string.IsNullOrWhiteSpace(config.SharedFolder) && !known.Contains("Dossier partage N4"))
        {
            db.Components.Add(new N4Component
            {
                EnvironmentId = environmentId,
                LogicalName = "Dossier partage N4",
                Role = ComponentRole.DossierPartage,
                ControlMode = ControlMode.SuperviseSeulement,
                Criticality = CriticalityLevel.Critique,
                Status = LifecycleStatus.Brouillon,
                StartOrder = 0,
                Endpoint = config.SharedFolder,
                Description = "Contient amq et conf. Une corruption ici empeche le demarrage du Center."
            });
            report.ComponentsCreated++;
        }

        await db.SaveChangesAsync(ct);
        return report;

        // ------------------------------------------------------------------
        void AddComponent(string name, ComponentRole role, string? serviceName,
                          string? host, string readinessKey, int startOrder)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                report.Warnings.Add($"{name} : aucun hote declare dans le fichier, composant ignore.");
                return;
            }

            if (known.Contains(name))
            {
                report.ComponentsSkipped++;
                return;
            }

            var readiness = config!.Readiness?.Components is not null
                            && config.Readiness.Components.TryGetValue(readinessKey, out var r)
                ? r : null;

            var defaults = config.Readiness?.Defaults;

            db.Components.Add(new N4Component
            {
                EnvironmentId = environmentId,
                ServerId = serversByHost.TryGetValue(host, out var srv) ? srv.Id : null,
                Server = serversByHost.GetValueOrDefault(host),
                LogicalName = name,
                Role = role,
                WindowsServiceName = serviceName,
                ControlMode = ControlMode.Pilotable,
                Criticality = role is ComponentRole.CenterNode or ComponentRole.ClusterNode
                    ? CriticalityLevel.Critique : CriticalityLevel.Elevee,
                Status = LifecycleStatus.Brouillon,
                StartOrder = startOrder,
                Description = "Importe depuis Navis-Config.json",
                Readiness = new ReadinessProfile
                {
                    LogPath = readiness?.LogPath,
                    ReadyPatterns = readiness?.ReadyPatterns ?? [],
                    ErrorPatterns = readiness?.ErrorPatterns ?? defaults?.ErrorPatterns ?? [],
                    IgnorePatterns = readiness?.IgnorePatterns ?? defaults?.IgnorePatterns ?? [],
                    ServiceRunningTimeoutSeconds = readiness?.ServiceRunningTimeoutSeconds
                                                   ?? defaults?.ServiceRunningTimeoutSeconds ?? 300,
                    LogReadyTimeoutSeconds = readiness?.LogReadyTimeoutSeconds
                                              ?? defaults?.LogReadyTimeoutSeconds ?? 1800,
                    StopTimeoutSeconds = readiness?.StopTimeoutSeconds
                                          ?? defaults?.StopTimeoutSeconds ?? 600,
                    PollIntervalSeconds = readiness?.PollIntervalSeconds
                                           ?? defaults?.PollIntervalSeconds ?? 10,
                    ProgressEverySeconds = readiness?.ProgressEverySeconds
                                            ?? defaults?.ProgressEverySeconds ?? 60,
                    PostReadySettleSeconds = readiness?.PostReadySettleSeconds
                                              ?? defaults?.PostReadySettleSeconds ?? 0
                }
            });

            if (readiness?.ReadyPatterns is null || readiness.ReadyPatterns.Count == 0)
                report.Warnings.Add($"{name} : aucun marqueur de demarrage dans le fichier. " +
                                    "Relevez-le avec Find-N4ReadinessPattern.ps1 avant toute exploitation.");

            report.ComponentsCreated++;
        }
    }
}

public sealed class ImportReport
{
    public int ServersCreated { get; set; }
    public int ServersSkipped { get; set; }
    public int ComponentsCreated { get; set; }
    public int ComponentsSkipped { get; set; }
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];
    public bool Succeeded => Errors.Count == 0;
}

// ---------------------------------------------------------------------------
// Reflet du format Navis-Config.json. Volontairement tolerant : un fichier
// incomplet doit produire un import partiel documente, pas une exception.
// ---------------------------------------------------------------------------
internal sealed class NavisConfig
{
    public string? CenterNode { get; set; }
    public string? StandbyNode { get; set; }
    public List<string>? ClusterNodes { get; set; }
    public string? BridgeHost { get; set; }
    public string? XPSHost { get; set; }
    public string? ECN4Host { get; set; }
    public NavisServiceNames? ServiceNames { get; set; }
    public string? SharedFolder { get; set; }
    public string? DatabaseHost { get; set; }
    public int? DatabasePort { get; set; }
    public string? DatabaseEngine { get; set; }
    public NavisReadiness? Readiness { get; set; }
}

internal sealed class NavisServiceNames
{
    public string? Center { get; set; }
    public string? Cluster { get; set; }
    public string? Standby { get; set; }
    public string? Bridge { get; set; }
    public string? XPS { get; set; }
    public string? ECN4 { get; set; }
    public string? ECN4Web { get; set; }
}

internal sealed class NavisReadiness
{
    public NavisReadinessEntry? Defaults { get; set; }
    public Dictionary<string, NavisReadinessEntry>? Components { get; set; }
}

internal sealed class NavisReadinessEntry
{
    public string? LogPath { get; set; }
    public List<string>? ReadyPatterns { get; set; }
    public List<string>? ErrorPatterns { get; set; }
    public List<string>? IgnorePatterns { get; set; }
    public int? ServiceRunningTimeoutSeconds { get; set; }
    public int? LogReadyTimeoutSeconds { get; set; }
    public int? StopTimeoutSeconds { get; set; }
    public int? PollIntervalSeconds { get; set; }
    public int? ProgressEverySeconds { get; set; }
    public int? PostReadySettleSeconds { get; set; }
}
