using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Service d'arrière-plan qui fait vivre le moteur d'orchestration.
///
/// Il fait trois choses, et rien d'autre :
///   1. au démarrage de l'application, réconcilier les exécutions laissées en
///      vol par un arrêt brutal (NFR-002) ;
///   2. prendre en charge les exécutions passées en cours depuis un écran ;
///   3. prolonger les verrous d'environnement tant qu'une opération vit.
///
/// C'est ce service qui rend l'orchestration indépendante de la présence d'un
/// utilisateur devant son écran : une séquence d'arrêt N4 dure vingt minutes,
/// personne ne doit avoir à garder un onglet ouvert pour qu'elle aboutisse.
/// </summary>
public sealed class OrchestrationBackgroundService(
    OrchestrationEngine engine,
    ILogger<OrchestrationBackgroundService> logger) : BackgroundService
{
    /// <summary>
    /// Cadence de veille, quand rien ne tourne. Volontairement lente : une
    /// requête toutes les trois secondes, jour et nuit, finirait par noyer dans
    /// le journal applicatif les quelques lignes qui comptent vraiment le jour
    /// d'une opération.
    /// </summary>
    private static readonly TimeSpan CadenceVeille = TimeSpan.FromSeconds(20);

    /// <summary>Cadence quand une opération est en cours : la réactivité prime.</summary>
    private static readonly TimeSpan CadenceActive = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan CadenceVerrous = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Laisser la base et les migrations se mettre en place avant de
        // prononcer un verdict sur des executions en cours.
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);

        try
        {
            await engine.ReconcileAtStartupAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "La réconciliation au démarrage a échoué. "
                + "Les exécutions en cours restent en l'état et ne seront pas reprises automatiquement.");
        }

        var prochainVerrous = DateTimeOffset.UtcNow;
        var cadence = CadenceVeille;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var actif = await engine.PickUpAsync(stoppingToken);
                cadence = actif ? CadenceActive : CadenceVeille;

                // Les verrous se prolongent INDEPENDAMMENT de l'existence d'un
                // pilote actif : une execution en pause ou en attente de
                // reconciliation n'a plus de pilote, mais tient toujours son
                // environnement dans un etat intermediaire. Laisser son verrou
                // expirer permettrait a une seconde operation de demarrer sur
                // un ecosysteme a moitie eteint.
                if (DateTimeOffset.UtcNow >= prochainVerrous)
                {
                    await engine.RenewLocksAsync(stoppingToken);
                    prochainVerrous = DateTimeOffset.UtcNow.Add(CadenceVerrous);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Une passe ratee ne doit pas tuer le moteur : la suivante
                // repartira de l'etat en base, qui est la seule verite.
                logger.LogError(ex, "Passe du moteur d'orchestration en échec.");
            }

            try { await Task.Delay(cadence, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
