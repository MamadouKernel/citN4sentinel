using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Domain.Interfaces;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Ticketing;

/// <summary>
/// PB-26 · S3-09 · Collecte JMX périodique en tâche de fond.
///
/// RÈGLES (sécurité + stabilité) :
///   • Période par défaut 5 min (configurable).
///   • Double-garde SEC-001 : un endpoint dont <c>JolokiaBaseUrl</c> est vide
///     est traité en MODE SIMULÉ — AUCUN appel HTTP n'est émis ; on écrit
///     juste une date <c>LastPooledAt</c> + audit <c>JmxEchec</c> (pour info).
///     Une erreur de connexion n'arrête pas l'HostedService (catch System + journal Serilog).
///   • Démarrage différé de 30 s — ne court-circuite pas PremierDémarrage.
///   • Utilise <c>IServiceScopeFactory</c> (pas de singleton capturé : pool
///     de DbContext par exécution).
/// </summary>
public sealed class JmxCollectorHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<JmxCollectorHostedService> logger)
    : BackgroundService
{
    public static readonly TimeSpan DefaultStartDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(DefaultStartDelay, stoppingToken);
        logger.LogInformation(
            "[PB-26 JMX] Démarrage collecte périodique HostedService " +
            "(période = {Period}s, double-garde simulé activée)",
            DefaultPeriod.TotalSeconds);

        using var timer = new PeriodicTimer(DefaultPeriod);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await PasserUneIterationAsync(stoppingToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[PB-26 JMX] Erreur fatale itération — le job continue à la période suivante " +
                    "pour ne pas bloquer la surveillance (RG-022).");
            }
        }
    }

    private async Task PasserUneIterationAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>();
        var connector = scope.ServiceProvider.GetRequiredService<IJmxConnector>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var endpoints = await db.JmxEndpoints.AsNoTracking().ToListAsync(ct);
        foreach (var ep in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var estSimule = string.IsNullOrWhiteSpace(ep.JolokiaBaseUrl);
                if (estSimule)
                {
                    var dbEp = await db.JmxEndpoints.FirstAsync(x => x.Id == ep.Id, ct);
                    dbEp.LastPooledAt = DateTimeOffset.UtcNow;
                    dbEp.LastErrorAt = DateTimeOffset.UtcNow;
                    dbEp.LastErrorMessage = "Mode simulé (JolokiaBaseUrl non renseigné)";
                    dbEp.LastFailureMessage = "Mode simulé (JolokiaBaseUrl non renseigné)";
                    await db.SaveChangesAsync(ct);
                    await audit.WriteAsync(
                        AuditAction.JmxEchec, AuditOutcome.Refuse, actor: "systeme",
                        entityType: nameof(JmxEndpoint), entityId: ep.Id.ToString(),
                        entityLabel: ep.Name, reason: dbEp.LastErrorMessage, ct: ct);
                    continue;
                }

                // Appel HTTP réel via Jolokia : ReadMetric(QueueSize default bridge)
                _ = await connector.ReadMetricAsync(ep.Id,
                    metricName: "org.apache.activemq:type=Broker,brokerName=*,destinationType=Queue,destinationName=Bridge/Depth",
                    scope: null, ct: ct);

                var okEp = await db.JmxEndpoints.FirstAsync(x => x.Id == ep.Id, ct);
                okEp.LastPooledAt = DateTimeOffset.UtcNow;
                okEp.LastSuccessAt = DateTimeOffset.UtcNow;
                okEp.LastErrorMessage = null;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                var dbEp = await db.JmxEndpoints.FirstOrDefaultAsync(x => x.Id == ep.Id, ct);
                if (dbEp is not null)
                {
                    dbEp.LastPooledAt = DateTimeOffset.UtcNow;
                    dbEp.LastErrorAt = DateTimeOffset.UtcNow;
                    dbEp.LastErrorMessage = ex.Message.Length > 1000
                        ? ex.Message.Substring(0, 1000)
                        : ex.Message;
                    await db.SaveChangesAsync(ct);
                }
                logger.LogWarning(
                    ex, "[PB-26 JMX] Itération endpoint {Host}:{Port} en échec.",
                    ep.Host, ep.Port);
                await audit.WriteAsync(
                    action: AuditAction.JmxEchec, outcome: AuditOutcome.Echec, actor: "systeme",
                    entityType: nameof(JmxEndpoint), entityId: ep.Id.ToString(),
                    entityLabel: ep.Name, reason: ex.Message, ct: ct);
            }
        }
    }
}
