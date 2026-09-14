using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Apr;

/// <summary>
/// Requêtes & écritures APR persistantes : Top-10 Dashboard, Upsert
/// anti-doublon sur Code+SourceComponentId+Status, ExpireOverdueAsync.
/// Pattern : scellé Scoped, retour string? / List vide = succès.
/// </summary>
public sealed class AprQueryService(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    private static readonly AprStatus[] StatutsOuvertsPourAntiDoublon = new[]
    {
        AprStatus.Detectee,
        AprStatus.Proposee,
        AprStatus.Programmee,
    };

    /// <summary>
    /// Retourne les 10 APR les plus prioritaires (tri PriorityScore DESC).
    /// StatutsOuverts par défaut : Détectée, Proposée, Programmée, EnCoursDExecution.
    /// </summary>
    public async Task<List<PreventiveRecommendedAction>> GetTop10PriorityDashboardAsync(
        Guid? envId = null,
        AprStatus[]? statusFilter = null,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (limit <= 0) limit = 10;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        IQueryable<PreventiveRecommendedAction> query = db.PreventiveRecommendedActions.AsNoTracking()
            .Include(a => a.Environment)
            .Include(a => a.SourceComponent);

        if (envId.HasValue)
            query = query.Where(a => a.EnvironmentId == envId.Value);

        if (statusFilter is { Length: > 0 })
            query = query.Where(a => statusFilter.Contains(a.APRStatus));

        return await query
            .OrderByDescending(a => a.PriorityScore)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Upsert : si une APR ouverte existe déjà avec le même
    /// (Code, SourceComponentId) → on met à jour (métriques, lastSeen,
    /// RawSignalSnapshot) au lieu de dupliquer. Si pas d'existant → insert.
    /// Retour (insertedCount, updatedCount).
    /// </summary>
    public async Task<(int Inserted, int Updated)> UpsertOrUpdateAsync(
        List<PreventiveRecommendedAction> candidates,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) return (0, 0);

        int inserted = 0, updated = 0;
        var maintenant = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        foreach (var apr in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var existant = await TrouverOuverteCorrespondanteAsync(db, apr, ct);

            if (existant is null)
            {
                apr.APRStatus = AprStatus.Proposee;
                apr.CreatedAt = maintenant;
                db.PreventiveRecommendedActions.Add(apr);
                inserted++;
            }
            else
            {
                existant.EstimatedP1RiskPct = apr.EstimatedP1RiskPct;
                existant.EstimatedDurationMinutes = apr.EstimatedDurationMinutes;
                existant.PriorityScore = apr.PriorityScore;
                existant.RawSignalSnapshot = apr.RawSignalSnapshot;
                existant.EvidenceSummary = apr.EvidenceSummary;
                existant.ModifiedAt = maintenant;
                if (!existant.FirstWeakSignalAt.HasValue && apr.FirstWeakSignalAt.HasValue)
                    existant.FirstWeakSignalAt = apr.FirstWeakSignalAt;
                updated++;
            }
        }

        await db.SaveChangesAsync(ct);
        return (inserted, updated);
    }

    /// <summary>
    /// Passe toutes les APR ouvertes ayant dépassé EstimatedDeadlineForAction
    /// en statut Expirée. Retourne le nombre d'éléments marqués.
    /// </summary>
    public async Task<int> ExpireOverdueAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var maintenant = DateTimeOffset.UtcNow;

        var concernees = await db.PreventiveRecommendedActions
            .Where(a =>
                StatutsOuvertsPourAntiDoublon.Contains(a.APRStatus)
                && a.EstimatedDeadlineForAction.HasValue
                && a.EstimatedDeadlineForAction.Value <= maintenant)
            .ToListAsync(ct);

        if (concernees.Count == 0) return 0;

        foreach (var a in concernees)
        {
            a.APRStatus = AprStatus.Expiree;
            a.ModifiedAt = maintenant;
            a.Notes = (string.IsNullOrEmpty(a.Notes) ? string.Empty : a.Notes + " | ")
                + $"[EXPIRATION AUTO {maintenant:u}] Délai d'action dépassé, "
                + $"deadline = {a.EstimatedDeadlineForAction:u}. Relance détecteur au prochain passage.";
        }

        await db.SaveChangesAsync(ct);
        return concernees.Count;
    }

    private static async Task<PreventiveRecommendedAction?> TrouverOuverteCorrespondanteAsync(
        N4SentinelDbContext db,
        PreventiveRecommendedAction candidate,
        CancellationToken ct)
    {
        IQueryable<PreventiveRecommendedAction> requete = db.PreventiveRecommendedActions
            .Where(a =>
                a.EnvironmentId == candidate.EnvironmentId
                && string.Equals(a.Code, candidate.Code, StringComparison.OrdinalIgnoreCase)
                && StatutsOuvertsPourAntiDoublon.Contains(a.APRStatus));

        if (candidate.SourceComponentId.HasValue)
            requete = requete.Where(a => a.SourceComponentId == candidate.SourceComponentId.Value);
        else
            requete = requete.Where(a => a.SourceComponentId == null);

        return await requete
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }
}
