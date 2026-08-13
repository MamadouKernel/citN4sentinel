using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Supervision;

/// <summary>
/// Service d'arrière-plan effectuant la collecte automatique périodique de santé.
///
/// Crée un IServiceScope pour consommer les services Scoped (IDbContextFactory, SupervisionService)
/// sans violer la validation de durée de vie d'ASP.NET Core.
/// </summary>
public sealed class SupervisionBackgroundService(
    IServiceProvider serviceProvider,
    SupervisionStateCache stateCache,
    ILogger<SupervisionBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Service d'arrière-plan de supervision démarré (intervalle : {Interval}s).", PollInterval.TotalSeconds);

        try
        {
            // Premier délai court pour laisser l'application démarrer proprement
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollAllComponentsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erreur imprévue durant la boucle de collecte de supervision.");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Arrêt propre et silencieux du service d'arrière-plan à la fermeture de l'hôte ASP.NET Core
        }

        logger.LogInformation("Service d'arrière-plan de supervision arrêté.");
    }

    private async Task PollAllComponentsAsync(CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>();
        var supervisionService = scope.ServiceProvider.GetRequiredService<SupervisionService>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var alertService = scope.ServiceProvider.GetRequiredService<AlertService>();

        var composants = await db.Components
            .AsNoTracking()
            .Where(c => c.Environment != null && (c.Environment.Status == Domain.LifecycleStatus.Valide || c.Environment.Status == Domain.LifecycleStatus.Actif))
            .Select(c => new { c.Id, c.EnvironmentId })
            .ToListAsync(ct);

        if (composants.Count == 0) return;

        foreach (var composant in composants)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var previous = stateCache.GetSnapshot(composant.Id);
                var current = await supervisionService.EvaluateComponentAsync(composant.Id, ct);

                stateCache.UpdateSnapshot(current);

                if (previous is not null && previous.State != current.State)
                {
                    logger.LogWarning("Changement d'état détecté pour {Composant} ({Env}) : {AncienState} -> {NouveauState}. Verdict : {Verdict}",
                        current.LogicalName, current.EnvironmentCode, previous.State, current.State, current.Verdict);
                }

                // Les alertes sont derivees du meme instantane, dans la meme
                // passe : elles ne peuvent donc jamais contredire ce
                // qu'affiche le tableau de bord.
                await alertService.EvaluateAsync(current, composant.EnvironmentId, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Échec d'évaluation du composant {Id}", composant.Id);
            }
        }
    }
}
