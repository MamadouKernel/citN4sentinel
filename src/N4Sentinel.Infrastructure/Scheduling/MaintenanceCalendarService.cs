using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain.Scheduling;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Scheduling;

/// <summary>
/// P6 — Calendrier simple de jours exclus (jours fériés, pics d'activité,
/// fermetures terminales). Ajout / suppression / listing / test d'appartenance.
///
/// Aucune règle métier complexe (récurrence annuelle, etc.) : les entrées
/// sont des dates localisées uniques, explicites. L'équipe admin a
/// historiquement préféré la liste explicite à la règle récurrente
/// sujette à casse les années à 52 semaines + 1 jour.
/// </summary>
public sealed class MaintenanceCalendarService(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    public async Task<List<MaintenanceHoliday>> ListAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MaintenanceHolidays
            .Where(h => h.EnvironmentId == environmentId)
            .OrderBy(h => h.Date)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<string?> AddAsync(Guid environmentId, DateOnly date, string reason, string actor,
        CancellationToken ct = default)
    {
        if (date == default) return "Calendrier : date invalide.";
        if (string.IsNullOrWhiteSpace(reason)) return "Calendrier : motif obligatoire pour ajouter un jour exclu.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existe = await db.MaintenanceHolidays
            .AnyAsync(h => h.EnvironmentId == environmentId && h.Date == date, ct);
        if (existe) return $"Calendrier : {date} est déjà marqué exclu sur cet environnement.";

        db.MaintenanceHolidays.Add(new MaintenanceHoliday
        {
            EnvironmentId = environmentId,
            Date = date,
            Reason = reason.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = actor,
            ModifiedAt = DateTimeOffset.UtcNow,
            ModifiedBy = actor
        });
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> RemoveAsync(Guid environmentId, DateOnly date, string actor,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entry = await db.MaintenanceHolidays
            .FirstOrDefaultAsync(h => h.EnvironmentId == environmentId && h.Date == date, ct);
        if (entry is null) return $"Calendrier : aucune exclusion trouvée pour {date}.";
        db.MaintenanceHolidays.Remove(entry);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<bool> IsHolidayAsync(Guid environmentId, DateTimeOffset localTime, CancellationToken ct = default)
    {
        var d = DateOnly.FromDateTime(localTime.LocalDateTime.Date);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MaintenanceHolidays
            .AnyAsync(h => h.EnvironmentId == environmentId && h.Date == d, ct);
    }
}
