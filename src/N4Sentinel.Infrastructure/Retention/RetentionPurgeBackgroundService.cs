using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace N4Sentinel.Infrastructure.Retention;

/// <summary>
/// SEC-009 : applique automatiquement la politique de rétention configurée
/// (<see cref="RetentionPolicy"/>), au lieu de dépendre d'un administrateur
/// qui penserait à cliquer sur « Purger » régulièrement.
///
/// Ne purge jamais l'audit (voir <see cref="RetentionPolicy"/>) — seuls les
/// journaux de diagnostic et l'historique d'exécution le sont, et seulement
/// ce qui est clos/terminé.
/// </summary>
public sealed class RetentionPurgeBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<RetentionPurgeBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Service d'arrière-plan de purge de rétention démarré (intervalle : {Interval}h).", Interval.TotalHours);

        try
        {
            // Délai initial : laisser l'application démarrer proprement avant
            // toute purge, comme les autres services d'arrière-plan.
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var retention = scope.ServiceProvider.GetRequiredService<RetentionPolicyService>();

                    var resultat = await retention.ApplyAsync("Système (purge planifiée)", stoppingToken);

                    logger.LogInformation(
                        "Purge de rétention automatique : {Sources} extrait(s) de journal, {Constats} constat(s), "
                        + "{Executions} exécution(s), {Signaux} relevé(s) de supervision supprimé(s).",
                        resultat.LogSourcesToDelete, resultat.LogFindingsToDelete,
                        resultat.ExecutionsToDelete, resultat.SignalsToDelete);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erreur imprévue durant la purge de rétention automatique.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Arrêt propre et silencieux à la fermeture de l'hôte ASP.NET Core.
        }

        logger.LogInformation("Service d'arrière-plan de purge de rétention arrêté.");
    }
}
