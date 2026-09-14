using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Edi;

/// <summary>
/// P6 — Surveillance EDI WatchFolder « light ».
///
/// Rôle : compléter le suivi EDI existant (<see cref="EdiTrackingService"/> +
/// <see cref="Supervision.AlertService"/>) par une seule chose qu'il ne fait
/// PAS encore automatiquement : ouvrir une session de diagnostic FR-059J dès
/// qu'un fichier EDI cumule plusieurs rejets consécutifs (seuil configurable).
///
/// Les alertes FR-059I (fichiers en attente trop vieux) sont déjà gérées par
/// Supervision.AlertService. Ce service ne les duplique PAS — il ne fait que
/// la partie « diagnostic automatique » des échecs répétés, et la
/// re-synchronisation périodique pour éviter que l'utilisateur n'ait besoin
/// de cliquer manuellement « Rafraîchir » toutes les 30 secondes.
/// </summary>
public sealed class EdiWatchFolderLightBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<EdiWatchFolderLightBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(12);

    /// <summary>Seuil FR-059J : au-delà, une session diagnostic est ouverte automatiquement.</summary>
    private const int FailureThreshold = 3;

    /// <summary>Anti-doublon : on ne réouvre pas deux diagnostics pour le même
    /// fichier dans les 6 heures. Un vrai fichier en échec répété peut faire
    /// l'objet d'un diagnostic unique résolvant les N occurrences.</summary>
    private static readonly TimeSpan AntiDoublonDiagnostic = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[P6 EDI WatchFolder] Service démarré. Premier scan dans {Delay} s.", StartupDelay.TotalSeconds);
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            int scans = 0, nouveauxDiagnostics = 0, rejetsDetectes = 0;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                var dbFactory = sp.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>();
                var ediTracking = sp.GetRequiredService<EdiTrackingService>();
                var diagSessions = sp.GetRequiredService<Diagnostic.DiagnosticSessionService>();
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);

                var composantsEdi = await db.Components.AsNoTracking()
                    .Include(c => c.Server)
                    .Include(c => c.SharedFolder)
                    .Where(c => c.SharedFolder != null && c.SharedFolder.IsConfigured)
                    .ToListAsync(stoppingToken);

                foreach (var compo in composantsEdi)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    scans++;
                    try
                    {
                        var fichiers = await ediTracking.SyncAsync(compo, stoppingToken);
                        foreach (var f in fichiers)
                        {
                            if (f.Status != EdiFileStatus.Rejete) continue;
                            if (f.ConsecutiveRejections < FailureThreshold) continue;
                            rejetsDetectes++;

                            // Anti-doublon : existe-t-il déjà une session de diagnostic récente ouverte SUR CE fichier ?
                            var sessionRecente = await db.Sessions
                                .AsNoTracking()
                                .AnyAsync(s => s.EnvironmentId == compo.EnvironmentId
                                            && s.CreatedAt >= DateTimeOffset.UtcNow.Subtract(AntiDoublonDiagnostic)
                                            && s.Title != null && s.Title.Contains(f.FileName, StringComparison.OrdinalIgnoreCase),
                                    stoppingToken);
                            if (sessionRecente) continue;

                            var result = await diagSessions.CreateFromEdiFileAsync(f, compo.Id,
                                requestedBy: $"edi-watchfolder::{compo.LogicalName}",
                                ct: stoppingToken);
                            if (result.Succeeded)
                            {
                                nouveauxDiagnostics++;
                                logger.LogInformation("[EDI WatchFolder] Diagnostic auto FR-059J ouvert pour « {File} » sur {Composant} (session #{Session}).",
                                    f.FileName, compo.LogicalName, result.SessionId.ToString("N"));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[EDI WatchFolder] Scan composant {Composant} en échec. Passage suivant.", compo.LogicalName);
                    }
                }

                chrono.Stop();
                logger.LogInformation(
                    "[EDI WatchFolder] Pass {Ms} ms — composants scannés {Scans}, rejets≥{Seuil} {Rejets}, "
                    + "nouvelles sessions diagnostics auto {Diags}.",
                    chrono.ElapsedMilliseconds, scans, FailureThreshold, rejetsDetectes, nouveauxDiagnostics);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[EDI WatchFolder] Passe complète en échec.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }

        logger.LogInformation("[EDI WatchFolder] Service arrêté proprement.");
    }
}
