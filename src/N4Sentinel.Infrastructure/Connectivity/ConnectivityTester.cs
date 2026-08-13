using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Connectivity;

/// <summary>
/// Test de la configuration technique SANS ACTION MUTATIVE (FR-007).
///
/// Verifie ce qui est verifiable depuis le reseau avant qu'un environnement ou
/// un workflow ne soit active : resolution de nom, joignabilite, ports requis.
/// Aucun service n'est demarre, arrete ni interroge de maniere intrusive.
///
/// PERIMETRE DE CETTE VERSION (S1) : controles reseau uniquement. La
/// verification de l'etat reel des services Windows, des processus et des
/// journaux applicatifs passe par le connecteur WinRM, livre au sprint 2.
/// Les controles correspondants sont donc rapportes "Impossible a verifier",
/// et non "Satisfait" : on n'affirme pas ce qu'on n'a pas mesure.
/// </summary>
public sealed class ConnectivityTester(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<ConnectivityTester> logger)
{
    /// <summary>Teste tous les composants d'un environnement.</summary>
    public async Task<EnvironmentTestReport> TestEnvironmentAsync(
        Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environment = await db.Environments
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == environmentId, ct)
            ?? throw new InvalidOperationException("Environnement introuvable.");

        var components = await db.Components
            .AsNoTracking()
            .Include(c => c.Server)
            .Where(c => c.EnvironmentId == environmentId)
            .OrderBy(c => c.StartOrder).ThenBy(c => c.LogicalName)
            .ToListAsync(ct);

        var servers = await db.Servers
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId)
            .ToListAsync(ct);

        var report = new EnvironmentTestReport
        {
            EnvironmentId = environmentId,
            EnvironmentCode = environment.Code,
            StartedAt = DateTimeOffset.UtcNow
        };

        foreach (var server in servers)
            report.Results.AddRange(await TestServerAsync(server, ct));

        foreach (var component in components)
            report.Results.AddRange(await TestComponentAsync(component, ct));

        if (components.Count == 0 && servers.Count == 0)
        {
            report.Results.Add(new CheckResult
            {
                Target = environment.Code,
                CheckName = "Inventaire",
                Outcome = CheckOutcome.Bloquant,
                Message = "Aucun serveur ni composant declare dans cet environnement.",
                Recommendation = "Declarez au moins un serveur et ses composants avant d'activer l'environnement."
            });
        }

        report.CompletedAt = DateTimeOffset.UtcNow;
        return report;
    }

    private async Task<List<CheckResult>> TestServerAsync(N4Server server, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        var label = server.HostName;

        // 1. Resolution de nom. Un nom qui ne se resout pas rend tout le reste
        //    sans objet : on marque bloquant et on n'insiste pas.
        var dns = await ResolveAsync(server.HostName, ct);
        results.Add(dns);
        if (dns.Outcome == CheckOutcome.Bloquant)
        {
            results.Add(Skipped(label, "Joignabilite", "Nom non resolu."));
            results.Add(Skipped(label, $"Port WinRM {server.WinRmPort}", "Nom non resolu."));
            return results;
        }

        // 2. Ping. Un echec n'est PAS bloquant : ICMP est frequemment filtre
        //    sur les reseaux d'exploitation. C'est un indice, pas une preuve.
        results.Add(await PingAsync(server.HostName, ct));

        // 3. Port WinRM : c'est lui qui conditionne tout le pilotage a venir.
        results.Add(await TestPortAsync(server.HostName, server.WinRmPort,
            $"Port WinRM {server.WinRmPort}",
            "Sans ce port, aucun pilotage ni collecte a distance n'est possible. " +
            "Sur le serveur cible, en administrateur : Enable-PSRemoting -Force"));

        return results;
    }

    private async Task<List<CheckResult>> TestComponentAsync(N4Component component, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        var label = component.LogicalName;

        if (component.ControlMode == ControlMode.NonSupervise)
        {
            results.Add(new CheckResult
            {
                Target = label,
                CheckName = "Perimetre",
                Outcome = CheckOutcome.NonApplicable,
                Message = "Composant declare non supervise : aucun controle effectue."
            });
            return results;
        }

        if (component.Server is null)
        {
            results.Add(new CheckResult
            {
                Target = label,
                CheckName = "Hebergement",
                Outcome = component.Role == ComponentRole.SystemeExterne
                    ? CheckOutcome.NonApplicable
                    : CheckOutcome.Avertissement,
                Message = "Aucun serveur associe a ce composant.",
                Recommendation = "Associez un serveur, sauf s'il s'agit d'un systeme externe hors perimetre."
            });
            return results;
        }

        // Port applicatif, quand il est declare.
        if (component.Port is > 0)
        {
            results.Add(await TestPortAsync(component.Server.HostName, component.Port.Value,
                $"Port applicatif {component.Port}",
                "Le port declare ne repond pas. Le composant est peut-etre a l'arret : " +
                "c'est normal avant un demarrage, anormal en exploitation.",
                targetLabel: label));
        }

        // Nom de service Windows : on ne peut pas encore l'interroger, mais on
        // peut verifier qu'il a ete renseigne. Un composant pilotable sans nom
        // de service ne pourra jamais etre pilote.
        if (component.ControlMode == ControlMode.Pilotable)
        {
            results.Add(string.IsNullOrWhiteSpace(component.WindowsServiceName)
                ? new CheckResult
                {
                    Target = label,
                    CheckName = "Service Windows",
                    Outcome = CheckOutcome.Bloquant,
                    Message = "Composant declare pilotable mais sans nom de service Windows.",
                    Recommendation = "Relevez le nom exact sur le serveur : " +
                                     "Get-Service | Where-Object { $_.DisplayName -like '*Navis*' }"
                }
                : new CheckResult
                {
                    Target = label,
                    CheckName = "Service Windows",
                    Outcome = CheckOutcome.ImpossibleAVerifier,
                    Message = $"'{component.WindowsServiceName}' declare. Etat reel non verifiable sans le connecteur WinRM.",
                    Recommendation = "Verification effective au sprint 2, avec le connecteur."
                });
        }

        // Preuve de demarrage : sans elle, l'orchestrateur ne pourra jamais
        // conclure autrement qu'a "A confirmer".
        if (component.ControlMode == ControlMode.Pilotable)
        {
            results.Add(component.Readiness.IsProvable
                ? new CheckResult
                {
                    Target = label,
                    CheckName = "Preuve de demarrage",
                    Outcome = CheckOutcome.Satisfait,
                    Message = $"{component.Readiness.ReadyPatterns.Count} marqueur(s) configure(s) sur {component.Readiness.LogPath}"
                }
                : new CheckResult
                {
                    Target = label,
                    CheckName = "Preuve de demarrage",
                    Outcome = CheckOutcome.Avertissement,
                    Message = "Aucun chemin de journal ou aucun marqueur de demarrage configure.",
                    Recommendation = "Relevez le marqueur sur un demarrage reussi avec Find-N4ReadinessPattern.ps1, " +
                                     "puis renseignez-le. Sans lui, l'etat du composant restera 'A confirmer'."
                });
        }

        return results;
    }

    private async Task<CheckResult> ResolveAsync(string hostName, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostName, ct);
            return new CheckResult
            {
                Target = hostName,
                CheckName = "Resolution DNS",
                Outcome = CheckOutcome.Satisfait,
                Message = string.Join(", ", addresses.Select(a => a.ToString()))
            };
        }
        catch (Exception ex)
        {
            return new CheckResult
            {
                Target = hostName,
                CheckName = "Resolution DNS",
                Outcome = CheckOutcome.Bloquant,
                Message = $"Nom non resolu : {ex.Message}",
                Recommendation = "Verifiez l'orthographe du nom d'hote, le suffixe DNS et le serveur DNS utilise."
            };
        }
    }

    private async Task<CheckResult> PingAsync(string hostName, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(hostName, TimeSpan.FromSeconds(4), cancellationToken: ct);

            return reply.Status == IPStatus.Success
                ? new CheckResult
                {
                    Target = hostName,
                    CheckName = "Ping",
                    Outcome = CheckOutcome.Satisfait,
                    Message = $"Reponse en {reply.RoundtripTime} ms"
                }
                : new CheckResult
                {
                    Target = hostName,
                    CheckName = "Ping",
                    Outcome = CheckOutcome.Avertissement,
                    Message = $"Pas de reponse ICMP ({reply.Status}).",
                    Recommendation = "ICMP est souvent filtre sur les reseaux d'exploitation. " +
                                     "Ce n'est bloquant que si le port WinRM ne repond pas non plus."
                };
        }
        catch (Exception ex)
        {
            return new CheckResult
            {
                Target = hostName,
                CheckName = "Ping",
                Outcome = CheckOutcome.ImpossibleAVerifier,
                Message = ex.Message
            };
        }
    }

    private async Task<CheckResult> TestPortAsync(
        string hostName, int port, string checkName, string recommendation,
        string? targetLabel = null, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(hostName, port, cts.Token);

            return new CheckResult
            {
                Target = targetLabel ?? hostName,
                CheckName = checkName,
                Outcome = CheckOutcome.Satisfait,
                Message = $"Port ouvert sur {hostName} ({sw.ElapsedMilliseconds} ms)"
            };
        }
        catch (OperationCanceledException)
        {
            return new CheckResult
            {
                Target = targetLabel ?? hostName,
                CheckName = checkName,
                Outcome = CheckOutcome.Bloquant,
                Message = $"Aucune reponse de {hostName}:{port} apres {timeoutMs} ms.",
                Recommendation = recommendation
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Echec du test de port {Host}:{Port}", hostName, port);
            return new CheckResult
            {
                Target = targetLabel ?? hostName,
                CheckName = checkName,
                Outcome = CheckOutcome.Bloquant,
                Message = $"{hostName}:{port} - {ex.Message}",
                Recommendation = recommendation
            };
        }
    }

    private static CheckResult Skipped(string target, string checkName, string why) => new()
    {
        Target = target,
        CheckName = checkName,
        Outcome = CheckOutcome.ImpossibleAVerifier,
        Message = $"Controle non effectue : {why}"
    };
}

/// <summary>Resultat d'un controle unitaire, horodate (FR-012).</summary>
public sealed class CheckResult
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string Target { get; init; } = string.Empty;
    public string CheckName { get; init; } = string.Empty;
    public CheckOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Recommendation { get; init; }
}

/// <summary>Synthese d'un test de configuration d'environnement.</summary>
public sealed class EnvironmentTestReport
{
    public Guid EnvironmentId { get; init; }
    public string EnvironmentCode { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; set; }
    public List<CheckResult> Results { get; } = [];

    public int Satisfied => Results.Count(r => r.Outcome == CheckOutcome.Satisfait);
    public int Warnings => Results.Count(r => r.Outcome == CheckOutcome.Avertissement);
    public int Blocking => Results.Count(r => r.Outcome == CheckOutcome.Bloquant);
    public int Unverifiable => Results.Count(r => r.Outcome == CheckOutcome.ImpossibleAVerifier);

    public TimeSpan Duration => CompletedAt - StartedAt;

    /// <summary>
    /// Un environnement ne peut etre active tant qu'un controle bloquant
    /// subsiste. Les avertissements, eux, n'empechent pas l'activation : ils
    /// doivent etre compris, pas forcement corriges.
    /// </summary>
    public bool CanActivate => Blocking == 0;
}
