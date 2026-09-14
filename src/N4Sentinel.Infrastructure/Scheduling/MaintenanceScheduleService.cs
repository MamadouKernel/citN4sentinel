using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Domain.Scheduling;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Scheduling;

/// <summary>
/// P6 — CRUD et contrôles de cohérence des planifications récurrentes N4.
///
/// Le « déclenchement » réel n'est pas ici : il est assuré par
/// <see cref="SchedulerTriggerBackgroundService"/>. Ce service garantit
/// seulement que la planification stockée est valide, unique par code sur
/// son environnement, et que les workflow cibles sont bien démarrantes.
/// </summary>
public sealed class MaintenanceScheduleService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IAuditWriter audit)
{
    /// <summary>Tolérance anti-conflit : 2 planifications du même workflow ne doivent pas être écartées de moins de 2h.</summary>
    private static readonly TimeSpan AntiConflitMinimumIntervalle = TimeSpan.FromHours(2);

    public async Task<List<ExecutionSchedule>> ListAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ExecutionSchedules
            .Include(s => s.Workflow)
            .Where(s => s.EnvironmentId == environmentId)
            .OrderBy(s => s.Code)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<ExecutionSchedule?> GetByIdAsync(Guid scheduleId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ExecutionSchedules
            .Include(s => s.Workflow)
            .FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
    }

    public async Task<(ExecutionSchedule? Schedule, string? Error)> CreateAsync(
        ExecutionSchedule schedule,
        string actor,
        CancellationToken ct = default)
    {
        if (schedule is null) return (null, "Planification : objet fourni est nul.");
        if (schedule.EnvironmentId == Guid.Empty) return (null, "Planification : EnvironnementId obligatoire.");
        if (schedule.WorkflowId == Guid.Empty) return (null, "Planification : WorkflowId obligatoire.");
        if (string.IsNullOrWhiteSpace(schedule.Code)) return (null, "Planification : Code obligatoire.");
        if (string.IsNullOrWhiteSpace(schedule.RanAs)) return (null, "Planification : RanAs (identifiant de l'exécutant automatique) est obligatoire.");
        if (schedule.TimeOfDay < TimeSpan.Zero || schedule.TimeOfDay.TotalDays >= 1)
            return (null, "Planification : TimeOfDay doit être compris entre 00:00 et 23:59:59.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var codeUniquePourEnv = await db.ExecutionSchedules
            .AnyAsync(s => s.EnvironmentId == schedule.EnvironmentId
                        && s.Code.Trim().Equals(schedule.Code.Trim(), StringComparison.OrdinalIgnoreCase), ct);
        if (codeUniquePourEnv) return (null, $"Planification : le code « {schedule.Code} » existe déjà sur cet environnement.");

        var wf = await db.Workflows.FindAsync(new object[] { schedule.WorkflowId }, ct);
        if (wf is null) return (null, "Planification : workflow cible introuvable.");
        if (!wf.IsRunnable) return (null, $"Planification : le workflow cible « {wf.Code} » n'est pas démarrable (statut={wf.Status}, étapes={wf.Steps.Count}).");

        if (schedule.Kind == ScheduleKind.HebdoJoursChoisis && schedule.WeeklyDayOfWeekMask == 0)
            return (null, "Planification Hebdo : au moins un jour doit être sélectionné (WeeklyDayOfWeekMask non nul).");
        if (schedule.Kind == ScheduleKind.MensuelJoursPrecis && (schedule.MonthlyDays is null || schedule.MonthlyDays.Count == 0))
            return (null, "Planification Mensuelle : au moins un jour numérique doit être listé.");
        if (schedule.Kind == ScheduleKind.CronExpression && string.IsNullOrWhiteSpace(schedule.CronExpression))
            return (null, "Planification Cron : CronExpression est obligatoire lorsque Kind=CronExpression.");
        if (schedule.ValidUntil.HasValue && schedule.ValidFrom.HasValue && schedule.ValidUntil.Value <= schedule.ValidFrom.Value)
            return (null, "Planification : ValidUntil doit être strictement postérieur à ValidFrom.");

        schedule.ValidFrom ??= DateTimeOffset.UtcNow;
        if (schedule.Status == ScheduleStatus.Actif && !schedule.ValidUntil.HasValue) schedule.NextPlannedAt = CalculerProchaineOccurrenceLocale(schedule, DateTimeOffset.UtcNow, null);

        schedule.CreatedAt = DateTimeOffset.UtcNow;
        schedule.CreatedBy = actor;
        schedule.ModifiedAt = schedule.CreatedAt;
        schedule.ModifiedBy = actor;

        db.ExecutionSchedules.Add(schedule);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditAction.PlanificationCreee, AuditOutcome.Succes, actor,
            entityType: nameof(ExecutionSchedule), entityId: schedule.Id.ToString("N"),
            entityLabel: schedule.Code, environmentId: schedule.EnvironmentId,
            reason: $"Création planification {schedule.Kind} TimeOfDay={schedule.TimeOfDay} AutoApprove={schedule.AutoApprove}",
            ct: ct);

        return (schedule, null);
    }

    public async Task<(ExecutionSchedule? Schedule, string? Warning, string? Error)> UpdateAsync(
        Guid scheduleId,
        Action<ExecutionSchedule> mutate,
        string actor,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sched = await db.ExecutionSchedules.FindAsync(new object[] { scheduleId }, ct);
        if (sched is null) return (null, null, "Planification introuvable.");

        mutate(sched);
        sched.ModifiedAt = DateTimeOffset.UtcNow;
        sched.ModifiedBy = actor;
        if (sched.Status == ScheduleStatus.Actif) sched.NextPlannedAt = CalculerProchaineOccurrenceLocale(sched, DateTimeOffset.UtcNow, null);

        string? warning = null;
        var conflit = await DetecterConflitProcheAsync(db, sched, ct);
        if (conflit is not null) warning = $"Conflit probable : la planification « {conflit.Code} » du même workflow est à {AntiConflitMinimumIntervalle} près.";

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditAction.PlanificationModifiee, AuditOutcome.Succes, actor,
            entityType: nameof(ExecutionSchedule), entityId: sched.Id.ToString("N"),
            entityLabel: sched.Code, environmentId: sched.EnvironmentId, reason: warning, ct: ct);

        return (sched, warning, null);
    }

    public async Task<string?> EnableAsync(Guid scheduleId, string actor, CancellationToken ct = default)
        => (await UpdateAsync(scheduleId, s => s.Status = ScheduleStatus.Actif, actor, ct)).Error;

    public async Task<string?> DisableAsync(Guid scheduleId, string actor, CancellationToken ct = default)
    {
        var r = await UpdateAsync(scheduleId, s => { s.Status = ScheduleStatus.Suspendu; s.NextPlannedAt = null; }, actor, ct);
        if (r.Error is not null) return r.Error;
        await audit.WriteAsync(AuditAction.PlanificationSuspendue, AuditOutcome.Succes, actor,
            entityType: nameof(ExecutionSchedule), entityId: scheduleId.ToString("N"),
            entityLabel: r.Schedule?.Code, environmentId: r.Schedule?.EnvironmentId,
            reason: "Suspension manuelle de la planification récurrente.", ct: ct);
        return null;
    }

    public async Task<string?> DeleteAsync(Guid scheduleId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sched = await db.ExecutionSchedules.FindAsync(new object[] { scheduleId }, ct);
        if (sched is null) return "Planification introuvable.";
        if (sched.LastTriggeredAt.HasValue)
            return "Planification déjà utilisée : la suspension (Disable) est préférée à la suppression pour préserver l'audit.";
        db.ExecutionSchedules.Remove(sched);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AuditAction.Suppression, AuditOutcome.Succes, actor,
            entityType: nameof(ExecutionSchedule), entityId: scheduleId.ToString("N"),
            entityLabel: sched.Code, environmentId: sched.EnvironmentId,
            reason: "Suppression planification jamais déclenchée.", ct: ct);
        return null;
    }

    public async Task<List<DateTimeOffset>> GetNextOccurrencesAsync(Guid scheduleId, int n = 10,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sched = await db.ExecutionSchedules.FindAsync(new object[] { scheduleId }, ct);
        if (sched is null || n <= 0) return [];

        var result = new List<DateTimeOffset>(n);
        var cursor = DateTimeOffset.UtcNow;
        var holidays = await db.MaintenanceHolidays
            .Where(h => h.EnvironmentId == sched.EnvironmentId)
            .Select(h => h.Date)
            .Distinct()
            .ToListAsync(ct);
        HashSet<DateOnly> holidaySet = new(holidays);

        for (int i = 0; i < n * 20 && result.Count < n; i++)
        {
            cursor = CalculerProchaineOccurrenceLocale(sched, cursor, null) ?? cursor.AddDays(1);
            if (cursor > sched.ValidUntil) break;
            var local = ToLocal(cursor, null);
            if (!holidaySet.Contains(DateOnly.FromDateTime(local.Date))) result.Add(cursor);
        }
        return result;
    }

    /// <summary>
    /// Calcule la prochaine occurrence en temps LOCAL de l'environnement,
    /// avec jours fériés exclus (optionnel). Retourne null s'il n'y en a plus.
    /// </summary>
    public static DateTimeOffset? CalculerProchaineOccurrenceLocale(ExecutionSchedule schedule,
        DateTimeOffset maintenantUtc, HashSet<DateOnly>? holidays)
    {
        if (schedule.Status != ScheduleStatus.Actif) return null;
        if (schedule.ValidUntil.HasValue && maintenantUtc >= schedule.ValidUntil) return null;

        var debut = schedule.ValidFrom.HasValue && maintenantUtc < schedule.ValidFrom.Value
            ? schedule.ValidFrom.Value : maintenantUtc;

        for (int jour = 0; jour < 732; jour++) // sécurité max 2 ans
        {
            var candidatLocal = debut.AddDays(jour);
            if (!JourConvient(schedule, candidatLocal)) continue;
            if (holidays is not null)
            {
                if (holidays.Contains(DateOnly.FromDateTime(candidatLocal.LocalDateTime.Date))) continue;
            }
            var heureCandidatLocal = candidatLocal.Date.Add(schedule.TimeOfDay);
            var resultat = new DateTimeOffset(heureCandidatLocal, candidatLocal.Offset);
            if (resultat > maintenantUtc && (!schedule.ValidUntil.HasValue || resultat <= schedule.ValidUntil))
                return resultat.UtcDateTime;
        }
        return schedule.ValidUntil;
    }

    private static bool JourConvient(ExecutionSchedule schedule, DateTimeOffset t)
    {
        var dow = t.LocalDateTime.DayOfWeek;
        return schedule.Kind switch
        {
            ScheduleKind.Quotidien => true,
            ScheduleKind.JoursOuvres => dow is not DayOfWeek.Saturday and not DayOfWeek.Sunday,
            ScheduleKind.HebdoJoursChoisis => MaskContientJour(schedule.WeeklyDayOfWeekMask, dow),
            ScheduleKind.MensuelJoursPrecis => schedule.MonthlyDays is not null && schedule.MonthlyDays.Contains(t.LocalDateTime.Day),
            ScheduleKind.CronExpression => true, // cron interprété par le moteur dédié si disponible, ici on avance chaque jour
            _ => false
        };
    }

    public static bool MaskContientJour(ushort mask, DayOfWeek dow)
    {
        int bit = dow switch
        {
            DayOfWeek.Monday => 0,
            DayOfWeek.Tuesday => 1,
            DayOfWeek.Wednesday => 2,
            DayOfWeek.Thursday => 3,
            DayOfWeek.Friday => 4,
            DayOfWeek.Saturday => 5,
            DayOfWeek.Sunday => 6,
            _ => -1
        };
        if (bit < 0) return false;
        return (mask & (1 << bit)) != 0;
    }

    private static async Task<ExecutionSchedule?> DetecterConflitProcheAsync(N4SentinelDbContext db, ExecutionSchedule sched, CancellationToken ct)
    {
        var autres = await db.ExecutionSchedules
            .Where(s => s.EnvironmentId == sched.EnvironmentId
                && s.WorkflowId == sched.WorkflowId
                && s.Id != sched.Id
                && s.Status == ScheduleStatus.Actif)
            .ToListAsync(ct);
        foreach (var a in autres)
        {
            if (Math.Abs(a.TimeOfDay.TotalMinutes - sched.TimeOfDay.TotalMinutes) < AntiConflitMinimumIntervalle.TotalMinutes)
                return a;
        }
        return null;
    }

    public static DateTimeOffset ToLocal(DateTimeOffset utc, string? tzId)
    {
        if (string.IsNullOrEmpty(tzId)) return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Utc);
        try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(tzId)); }
        catch { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Utc); }
    }
}
