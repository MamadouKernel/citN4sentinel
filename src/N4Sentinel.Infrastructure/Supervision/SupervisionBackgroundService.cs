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

    /// <summary>
    /// Le balayage des composants non déclarés (SUP-06, AC-15) interroge
    /// chaque serveur pour des services non attendus : plus coûteux qu'un
    /// relevé d'état, il n'a pas besoin du même rythme que le sondage
    /// classique. Une fois toutes les dix passes (~5 min à intervalle par
    /// défaut) suffit à repérer un nœud ajouté sans le laisser longtemps
    /// invisible.
    /// </summary>
    private const int PassesEntreBalayages = 10;
    private int _passe;

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

                    if (_passe++ % PassesEntreBalayages == 0)
                        await ScanUndeclaredComponentsAsync(stoppingToken);
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
            .Select(c => new { c.Id, c.EnvironmentId, c.Role })
            .ToListAsync(ct);

        if (composants.Count == 0) return;

        foreach (var composant in composants)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var previous = stateCache.GetSnapshot(composant.Id);
                var current = await supervisionService.EvaluateComponentAsync(composant.Id, ct, previous);

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

        // FR-032/033 : le conflit Center/Standby ne se lit pas sur un
        // composant isole, mais sur la paire — d'ou une passe separee, une
        // fois tous les instantanes de ce tour a jour.
        foreach (var environmentId in composants.Select(c => c.EnvironmentId).Distinct())
        {
            var centerId = composants.FirstOrDefault(c => c.EnvironmentId == environmentId && c.Role == Domain.ComponentRole.CenterNode)?.Id;
            var standbyId = composants.FirstOrDefault(c => c.EnvironmentId == environmentId && c.Role == Domain.ComponentRole.StandbyCenterNode)?.Id;

            if (centerId is null && standbyId is null) continue;

            try
            {
                var center = centerId is { } cid ? stateCache.GetSnapshot(cid) : null;
                var standby = standbyId is { } sid ? stateCache.GetSnapshot(sid) : null;
                await alertService.EvaluateCenterConflictAsync(environmentId, center, standby, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Échec de la détection de conflit Center/Standby pour {Env}", environmentId);
            }
        }
    }

    /// <summary>
    /// SUP-06 / AC-15 : signale, sans jamais déclarer à la place de
    /// l'opérateur — voir <see cref="UndeclaredComponentScanner"/>. Un
    /// serveur injoignable pendant ce balayage n'est pas en soi une anomalie
    /// (il l'est déjà via le sondage classique) : seul un composant non
    /// déclaré détecté sur un serveur joignable est journalisé ici.
    /// </summary>
    private async Task ScanUndeclaredComponentsAsync(CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>();
        var scanner = scope.ServiceProvider.GetRequiredService<UndeclaredComponentScanner>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environnements = await db.Environments
            .AsNoTracking()
            .Where(e => e.Status == Domain.LifecycleStatus.Valide || e.Status == Domain.LifecycleStatus.Actif)
            .Select(e => new { e.Id, e.Code })
            .ToListAsync(ct);

        foreach (var env in environnements)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var resultat = await scanner.ScanAsync(env.Id, ct);
                if (!resultat.Succeeded || resultat.Undeclared.Count == 0) continue;

                logger.LogWarning(
                    "{Nombre} composant(s) non déclaré(s) détecté(s) sur {Env} : {Composants}.",
                    resultat.Undeclared.Count, env.Code,
                    string.Join(", ", resultat.Undeclared.Select(u => $"{u.ServiceName}@{u.HostName}")));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Échec du balayage des composants non déclarés pour {Env}", env.Code);
            }
        }
    }
}
