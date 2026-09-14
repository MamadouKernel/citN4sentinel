using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Apr;

/// <summary>
/// Service d'arrière-plan APR P2 MERISE :
///   (1) Toutes les 5 minutes — détecte les signaux faibles → Upsert APR.
///   (2) Expire les APR dont EstimatedDeadlineForAction dépassé.
///   (3) Rafraîchit statut APR EnCoursDExecution → Résolue
///       si son WorkflowExecution associée est terminale.
/// Le job est un HostedService (singleton) → il utilise IServiceScopeFactory
/// pour obtenir AprDetectionService / AprQueryService (Scoped) à chaque tick.
/// </summary>
public sealed class AprBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AprBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Service APR d'arrière-plan démarré (intervalle : {Interval} min). Premier passage dans {StartupDelay} s.",
            PollInterval.TotalMinutes, StartupDelay.TotalSeconds);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var chrono = System.Diagnostics.Stopwatch.StartNew();
                var succes = false;
                int detectees = 0, inserees = 0, misesAJour = 0, expirees = 0, finalisees = 0;
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var sp = scope.ServiceProvider;
                    var detecteur = sp.GetRequiredService<AprDetectionService>();
                    var query = sp.GetRequiredService<AprQueryService>();
                    var dbFactory = sp.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>();

                    // ---- (1) Détection + Upsert ---------------------------------
                    var candidates = await detecteur.DetectAllAsync(ct: stoppingToken);
                    detectees = candidates.Count;
                    if (detectees > 0)
                    {
                        (inserees, misesAJour) = await query.UpsertOrUpdateAsync(candidates, stoppingToken);
                    }

                    // ---- (2) Expiration APR passées leur deadline ---------------
                    expirees = await query.ExpireOverdueAsync(stoppingToken);

                    // ---- (3) Rafraîchissement : APR attachée à une exécution WF
                    finalisees = await RafraichirStatutsTerminauxAsync(dbFactory, sp, stoppingToken);

                    succes = true;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Erreur service APR (passe en échec après {Chrono} ms). "
                        + "Les prochains passages retenteront automatiquement.",
                        chrono.ElapsedMilliseconds);
                }

                chrono.Stop();
                if (succes)
                {
                    logger.LogInformation(
                        "[APR] Pass terminé en {Ms} ms — détectées {Detectees}, "
                        + "nouvelles {Insérées}, mises-à-jour {MAJ}, expirées {Expirees}, "
                        + "finalisées-après-exécution {Finalisees}.",
                        chrono.ElapsedMilliseconds,
                        detectees, inserees, misesAJour, expirees, finalisees);
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Arrêt normal = silence
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Le service APR d'arrière-plan s'est terminé de façon inattendue.");
        }
        finally
        {
            logger.LogInformation("Service APR d'arrière-plan arrêté.");
        }
    }

    /// <summary>
    /// Si une APR est en EnCoursDExecution et que son WorkflowExecution liée
    /// (ResultingExecutionId) est terminale → on passe l'APR en statut Résolue.
    /// Terminaux = TermineSucces, TermineAvecAvertissements, Echec, Annule.
    /// </summary>
    private static async Task<int> RafraichirStatutsTerminauxAsync(
        IDbContextFactory<N4SentinelDbContext> dbFactory,
        IServiceProvider sp,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var comparer = StringComparer.OrdinalIgnoreCase;
        var ids = await db.PreventiveRecommendedActions
            .AsNoTracking()
            .Where(a => a.APRStatus == AprStatus.EnCoursDExecution && a.ResultingExecutionId != null)
            .Select(a => new { a.Id, ResultingExecutionId = a.ResultingExecutionId ?? Guid.Empty })
            .ToListAsync(ct);
        if (ids.Count == 0) return 0;

        var exeIds = ids.Select(x => x.ResultingExecutionId).Distinct().ToList();

        Dictionary<Guid, ExecutionStatus> exeStatuts;
        try
        {
            exeStatuts = await db.Executions
                .AsNoTracking()
                .Where(e => exeIds.Contains(e.Id))
                .Select(e => new { e.Id, e.Status })
                .ToDictionaryAsync(e => e.Id, e => e.Status, ct);
        }
        catch (InvalidOperationException)
        {
            // Fallback si 2 exe même Id (impossible, PK)
            exeStatuts = new Dictionary<Guid, ExecutionStatus>();
            foreach (var g in await db.Executions.AsNoTracking()
                .Where(e => exeIds.Contains(e.Id))
                .Select(e => new { e.Id, e.Status })
                .GroupBy(e => e.Id)
                .ToListAsync(ct))
            {
                exeStatuts[g.Key] = g.First().Status;
            }
        }

        Capitalisation.IncidentCapitalizationService? capitalisation = null;
        try
        {
            capitalisation = sp.GetService<Capitalisation.IncidentCapitalizationService>();
        }
        catch
        {
            capitalisation = null;
        }

        int finalisees = 0;
        var maintenant = DateTimeOffset.UtcNow;
        foreach (var pair in ids)
        {
            if (!exeStatuts.TryGetValue(pair.ResultingExecutionId, out var statut)) continue;
            if (!EstTerminal(statut)) continue;

            var apr = await db.PreventiveRecommendedActions
                .FirstOrDefaultAsync(a => a.Id == pair.Id, ct);
            if (apr is null) continue;

            apr.APRStatus = AprStatus.Resolue;
            apr.ModifiedAt = maintenant;
            apr.Notes = (string.IsNullOrEmpty(apr.Notes) ? string.Empty : apr.Notes + " | ")
                + $"[FIN EXECUTION {maintenant:u}] Workflow #{pair.ResultingExecutionId:N} → {statut}.";
            finalisees++;

            // Capitalisation : une APR résolue = P1 évité, on ajoute une trace incident
            // CAPITALISÉ (gravité Faible) pour calcul des métriques P1_EVITES_APR_COUNT + MTTR
            if (capitalisation is not null)
            {
                try
                {
                    var (errC, _) = await capitalisation.CapitalizeFromExecutionAsync(pair.ResultingExecutionId, ct);
                    if (errC is not null)
                    {
                        apr.Notes += $" | [CAPT WARN] {errC}";
                    }
                }
                catch (Exception exC)
                {
                    apr.Notes += $" | [CAPT ERR] {exC.GetType().Name}: {exC.Message}";
                }
            }
        }

        if (finalisees > 0)
            await db.SaveChangesAsync(ct);

        return finalisees;
    }

    private static bool EstTerminal(ExecutionStatus s)
    {
        return s
            is ExecutionStatus.TermineSucces
            or ExecutionStatus.TermineAvecAvertissements
            or ExecutionStatus.Echec
            or ExecutionStatus.Annule;
    }
}
