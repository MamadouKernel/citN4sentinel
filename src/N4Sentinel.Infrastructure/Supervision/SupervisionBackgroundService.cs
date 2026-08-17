using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Edi;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Referential;

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
    Observability.MetricsService metrics,
    ILogger<SupervisionBackgroundService> logger) : BackgroundService
{
    /// <summary>
    /// NFR-004 : « quelques secondes » au pire cas. Un sondage à 10 s couvre
    /// la totalité des composants d'un environnement bien plus vite que
    /// l'ancien réglage à 30 s, sans pour autant saturer les serveurs sondés
    /// à chaque passe.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Le balayage des composants non déclarés (SUP-06, AC-15) interroge
    /// chaque serveur pour des services non attendus : plus coûteux qu'un
    /// relevé d'état, il n'a pas besoin du même rythme que le sondage
    /// classique. Une fois toutes les trente passes (~5 min à intervalle par
    /// défaut) suffit à repérer un nœud ajouté sans le laisser longtemps
    /// invisible.
    /// </summary>
    private const int PassesEntreBalayages = 30;
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
                var chrono = System.Diagnostics.Stopwatch.StartNew();
                var succes = false;
                try
                {
                    await PollAllComponentsAsync(stoppingToken);
                    succes = true;

                    if (_passe++ % PassesEntreBalayages == 0)
                    {
                        await ScanUndeclaredComponentsAsync(stoppingToken);
                        await PollSharedFoldersAndEdiAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erreur imprévue durant la boucle de collecte de supervision.");
                }
                finally
                {
                    // NFR-008 : latence et fiabilité du sondage, consultables
                    // sans grep un fichier de log.
                    metrics.RecordSupervisionPoll(chrono.Elapsed, succes);
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

        // §3.18 "Contrôle / Signal" : l'instantané ne doit pas vivre
        // seulement en cache memoire (perdu au redemarrage) — chaque passage
        // est aussi persiste, un par composant, pour l'historique.
        var releves = new List<ComponentSignal>(composants.Count);

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

                releves.Add(new ComponentSignal
                {
                    ComponentId = composant.Id,
                    EnvironmentId = composant.EnvironmentId,
                    ComponentName = current.LogicalName,
                    SignalType = "ÉtatConsolidé",
                    Target = current.HostName,
                    Value = current.State.ToString(),
                    Threshold = null,
                    CapturedAt = DateTimeOffset.UtcNow,
                    Quality = QualiteReleve(current)
                });

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

        if (releves.Count > 0)
        {
            try
            {
                db.ComponentSignals.AddRange(releves);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Échec de persistance des relevés de supervision de cette passe.");
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

    /// <summary>
    /// FR-053 : les dossiers partagés (suspicion de corruption ActiveMQ/KahaDB
    /// comprise) et le suivi EDI ne se rafraîchissaient qu'à la visite manuelle
    /// d'un opérateur sur l'écran correspondant — un incident pouvait donc
    /// rester invisible tant que personne n'allait le chercher. Même rythme que
    /// <see cref="ScanUndeclaredComponentsAsync"/> (~5 min) : ces contrôles font
    /// une lecture réseau par dossier, plus coûteux que le sondage classique.
    /// </summary>
    private async Task PollSharedFoldersAndEdiAsync(CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>();
        var referentiel = scope.ServiceProvider.GetRequiredService<ReferentialService>();
        var dossiers = scope.ServiceProvider.GetRequiredService<SharedFolderHealthService>();
        var edi = scope.ServiceProvider.GetRequiredService<EdiTrackingService>();
        var alertes = scope.ServiceProvider.GetRequiredService<AlertService>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environnements = await db.Environments
            .AsNoTracking()
            .Where(e => e.Status == LifecycleStatus.Valide || e.Status == LifecycleStatus.Actif)
            .Select(e => new { e.Id, e.Code })
            .ToListAsync(ct);

        foreach (var env in environnements)
        {
            if (ct.IsCancellationRequested) break;

            List<N4Component> composants;
            try
            {
                composants = (await referentiel.GetComponentsAsync(env.Id))
                    .Where(c => c.SharedFolder.IsConfigured)
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Échec de lecture du référentiel des dossiers partagés pour {Env}", env.Code);
                continue;
            }

            foreach (var composant in composants)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var relevé = await dossiers.EvaluateAsync(composant, ct);
                    if (relevé.SuspectedCorruption)
                        logger.LogWarning(
                            "Suspicion de corruption sur le dossier partagé {Composant} ({Env}) : {Indices}.",
                            composant.LogicalName, env.Code, string.Join(", ", relevé.CorruptionIndicators));
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Échec du contrôle du dossier partagé {Composant}", composant.LogicalName);
                }

                if (composant.SharedFolder.Category != SharedFolderCategory.EchangeEdi) continue;

                try
                {
                    var fichiers = await edi.SyncAsync(composant, ct);
                    await alertes.EvaluateEdiAsync(composant, fichiers, ct);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Échec de la synchronisation EDI pour {Composant}", composant.LogicalName);
                }
            }
        }
    }

    /// <summary>Fiabilité du relevé (§3.18) : combien de signaux distincts l'ont réellement confirmé.</summary>
    private static string QualiteReleve(ComponentHealthSnapshot s)
    {
        if (s.State == ComponentState.Inconnu || s.State == ComponentState.NonSupervise) return "Inconnu";

        var signauxConcordants = 0;
        if (s.ProcessId is not null) signauxConcordants++;
        if (s.LogProofStatus == LogProofState.Proved) signauxConcordants++;
        if (s.HoldsActiveRole == true) signauxConcordants++;

        return signauxConcordants >= 2 ? "Confirmé" : "Incomplet";
    }
}
