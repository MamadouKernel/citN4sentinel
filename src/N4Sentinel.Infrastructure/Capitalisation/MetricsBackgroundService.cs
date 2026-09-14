using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace N4Sentinel.Infrastructure.Capitalisation;

/// <summary>
/// HostedService quotidien (1 exécution / jour, 02:15 UTC) pour recalculer
/// les métriques BusinessSuccessMetrics des périodes Jour / Semaine / Mois
/// terminées. Fait un Upsert par (ReportingPeriod, MetricCode, EnvironmentId).
/// Singleton via IServiceScopeFactory pour résoudre le Scoped
/// BusinessMetricsService à chaque tick.
/// </summary>
public sealed class MetricsBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<MetricsBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan HeureExecutionUtc = new(2, 15, 0);
    private static readonly TimeSpan UnJour = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Service calcul métriques démarré — chaque jour à {H} UTC. Prochain calcul : {Prochain}.",
            HeureExecutionUtc,
            ProchaineExecution());

        while (!stoppingToken.IsCancellationRequested)
        {
            var attendre = ProchaineExecution() - DateTimeOffset.UtcNow;
            if (attendre.Ticks > 0)
            {
                try
                {
                    await Task.Delay(attendre, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            var chrono = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var scope = scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<BusinessMetricsService>();

                var jour = DateTimeOffset.UtcNow.AddDays(-1);
                var errJ = await svc.CalculerPourTousLesEnvAsync(
                    BusinessMetricsService.Periode.Jour, jour, stoppingToken);
                var errS = await svc.CalculerPourTousLesEnvAsync(
                    BusinessMetricsService.Periode.Semaine, jour, stoppingToken);
                var errM = await svc.CalculerPourTousLesEnvAsync(
                    BusinessMetricsService.Periode.Mois, jour, stoppingToken);

                chrono.Stop();
                if (errJ is null && errS is null && errM is null)
                {
                    logger.LogInformation(
                        "[METBUS] Calcul J/S/M terminé en {Ms} ms (période référence {Jour}).",
                        chrono.ElapsedMilliseconds,
                        jour.Date.ToString("yyyy-MM-dd"));
                }
                else
                {
                    logger.LogWarning(
                        "[METBUS] Calcul terminé avec réserves en {Ms} ms — J={J} | S={S} | M={M}.",
                        chrono.ElapsedMilliseconds, errJ, errS, errM);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                chrono.Stop();
                logger.LogError(
                    ex,
                    "[METBUS] Échec calcul métriques après {Ms} ms. Nouvelle tentative dans 24 h.",
                    chrono.ElapsedMilliseconds);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Service calcul métriques arrêté.");
    }

    private static DateTimeOffset ProchaineExecution()
    {
        var maintenant = DateTimeOffset.UtcNow;
        var aujourdhui = maintenant.Date.Add(HeureExecutionUtc);
        if (aujourdhui > maintenant) return aujourdhui;
        return aujourdhui.Add(UnJour);
    }
}
