using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Domain.Scheduling;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Scheduling;

/// <summary>
/// P6 — Déclencheur planifié des workflows N4.
///
/// Principe de prudence : ce service NE DÉMARRE JAMAIS de moteur, ne PILOTE
/// JAMAIS de composant. Il appelle simplement PrepareAsync et éventuellement
/// ApproveAsync — l'orchestrateur <see cref="Orchestration.OrchestrationEngine"/>
/// se charge ensuite du reste. Séparation forte (planification ≠ exécution)
/// garantit qu'une panne du planificateur n'empêche pas un démarrage manuel
/// de fonctionner normalement (contrainte NFR-001).
/// </summary>
public sealed class SchedulerTriggerBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<SchedulerTriggerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AntiDoubleDeclenchementFenetre = TimeSpan.FromHours(8);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[P6 Planification] Service déclencheur démarré. Premier tick dans {Delay} s.", StartupDelay.TotalSeconds);
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            int declenches = 0, skipsHoliday = 0, skipsWorkflowActif = 0, echecs = 0;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                var dbFactory = sp.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>();
                var executionService = sp.GetRequiredService<Orchestration.ExecutionService>();
                var calendar = sp.GetRequiredService<MaintenanceCalendarService>();
                var audit = sp.GetRequiredService<IAuditWriter>();
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);

                var envs = await db.Environments.AsNoTracking()
                    .Select(e => new { e.Id, e.TimeZoneId })
                    .ToListAsync(stoppingToken);

                foreach (var env in envs)
                {
                    // SQLite ne traduit pas de maniere fiable les comparaisons de
                    // DateTimeOffset nullable combinees en &&/||. On filtre le statut
                    // (traduisible) en base, puis la fenetre de validite cote client.
                    var maintenant = DateTimeOffset.UtcNow;
                    var schedules = (await db.ExecutionSchedules
                        .Where(s => s.EnvironmentId == env.Id && s.Status == ScheduleStatus.Actif)
                        .ToListAsync(stoppingToken))
                        .Where(s => (!s.ValidFrom.HasValue || s.ValidFrom.Value <= maintenant)
                                 && (!s.ValidUntil.HasValue || s.ValidUntil.Value >= maintenant))
                        .ToList();

                    foreach (var sched in schedules)
                    {
                        stoppingToken.ThrowIfCancellationRequested();

                        var next = MaintenanceScheduleService.CalculerProchaineOccurrenceLocale(
                            sched, DateTimeOffset.UtcNow.AddMinutes(-1), null);
                        if (next is null) continue;
                        if (next.Value > DateTimeOffset.UtcNow)
                        {
                            sched.NextPlannedAt = next.Value.UtcDateTime;
                            continue;
                        }

                        if (sched.LastTriggeredAt.HasValue
                            && DateTimeOffset.UtcNow - sched.LastTriggeredAt.Value < AntiDoubleDeclenchementFenetre)
                        {
                            continue; // évite deux lancements dans la même fenêtre
                        }

                        var maintenantLocal = MaintenanceScheduleService.ToLocal(DateTimeOffset.UtcNow, env.TimeZoneId);
                        if (await calendar.IsHolidayAsync(env.Id, maintenantLocal, stoppingToken))
                        {
                            skipsHoliday++;
                            sched.NextPlannedAt = MaintenanceScheduleService.CalculerProchaineOccurrenceLocale(sched, DateTimeOffset.UtcNow, null)?.UtcDateTime;
                            continue;
                        }

                        if (sched.SkipIfActiveAlready)
                        {
                            var dejaEnCours = await db.Executions
                                .AnyAsync(e => e.WorkflowId == sched.WorkflowId
                                            && e.EnvironmentId == env.Id
                                            && e.IsActive, stoppingToken);
                            if (dejaEnCours)
                            {
                                skipsWorkflowActif++;
                                sched.NextPlannedAt = MaintenanceScheduleService.CalculerProchaineOccurrenceLocale(sched, DateTimeOffset.UtcNow, null)?.UtcDateTime;
                                continue;
                            }
                        }

                        var requestedBy = string.IsNullOrWhiteSpace(sched.RanAs) ? $"planificateur::{sched.Code}" : sched.RanAs;
                        var reason = sched.ReasonTemplate ?? $"[Planification automatique N4] {sched.Code} - {sched.Name}.";

                        try
                        {
                            var prepare = await executionService.PrepareAsync(
                                workflowId: sched.WorkflowId,
                                requestedBy: requestedBy,
                                reason: reason,
                                ticketReference: sched.TicketTemplate,
                                isSimulation: false,
                                startWindow: null,
                                endWindow: null,
                                ct: stoppingToken);

                            if (prepare is null || prepare.Error is not null)
                            {
                                echecs++;
                                var msg = prepare?.Error ?? "PrepareAsync a retourné un résultat nul.";
                                logger.LogWarning("[Planification {Code}] PrepareAsync a échoué : {Err}", sched.Code, msg);
                                await audit.WriteAsync(AuditAction.PlanificationDeclenchee, AuditOutcome.Echec, requestedBy,
                                    entityType: nameof(ExecutionSchedule), entityId: sched.Id.ToString("N"),
                                    entityLabel: sched.Code, environmentId: env.Id, reason: msg,
                                    ct: stoppingToken);
                                continue;
                            }

                            var executionId = prepare.ExecutionId;
                            if (executionId != Guid.Empty)
                            {
                                // Renseigner TriggeredByScheduleId + TriggeredBy sur la nouvelle exécution
                                var exec = await db.Executions.FirstOrDefaultAsync(e => e.Id == executionId, stoppingToken);
                                if (exec is not null)
                                {
                                    exec.TriggeredByScheduleId = sched.Id;
                                    exec.TriggeredBy = ExecutionTriggeredBy.Planification;
                                    await db.SaveChangesAsync(stoppingToken);
                                }
                            }

                            if (sched.AutoApprove && executionId != Guid.Empty)
                            {
                                // Auto-approbation planifiée : par le demandeur configuré, sans
                                // privilège admin (le flag reste false) — comportement inchangé.
                                var err = await executionService.ApproveAsync(executionId, requestedBy, ct: stoppingToken);
                                if (err is not null)
                                {
                                    logger.LogWarning("[Planification {Code}] AutoApprove a échoué (continuera en attente) : {Err}",
                                        sched.Code, err);
                                }
                            }

                            sched.LastTriggeredAt = DateTimeOffset.UtcNow;
                            sched.NextPlannedAt = MaintenanceScheduleService.CalculerProchaineOccurrenceLocale(
                                sched, DateTimeOffset.UtcNow, null)?.UtcDateTime;

                            declenches++;
                            await audit.WriteAsync(AuditAction.PlanificationDeclenchee, AuditOutcome.Succes,
                                requestedBy,
                                entityType: nameof(ExecutionSchedule), entityId: sched.Id.ToString("N"),
                                entityLabel: sched.Code, environmentId: env.Id,
                                reason: $"Déclenchement automatique → Execution #{executionId:N}",
                                detail: $"AutoApprove={sched.AutoApprove} Kind={sched.Kind} TimeOfDay={sched.TimeOfDay}",
                                ct: stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            echecs++;
                            logger.LogError(ex, "[Planification {Code}] Erreur lors du déclenchement.", sched.Code);
                        }
                    }
                    await db.SaveChangesAsync(stoppingToken);
                }

                chrono.Stop();
                logger.LogInformation(
                    "[Planification N4] Pass {Ms} ms — déclenchés OK {Declenches}, skips jour férié {Holiday}, "
                    + "skips workflow déjà actif {Actif}, échecs {Echecs}.",
                    chrono.ElapsedMilliseconds, declenches, skipsHoliday, skipsWorkflowActif, echecs);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Planification N4] Passe complète en échec.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }

        logger.LogInformation("[Planification N4] Service arrêté proprement.");
    }
}
