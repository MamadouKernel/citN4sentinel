using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Apr;

/// <summary>
/// Transitions une APR Proposée/Détectée → Programmée, à une date ScheduledAt future.
/// Pattern : scellé Scoped, retour string? erreur, null = succès.
/// </summary>
public sealed class AprScheduleUseCase(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    private static readonly AprStatus[] StatutsAutorises = new[]
    {
        AprStatus.Detectee,
        AprStatus.Proposee,
        AprStatus.Programmee, // Autorise le déplacement de date
    };

    public async Task<string?> ExecuteAsync(
        Guid aprId,
        DateTimeOffset scheduledAt,
        string scheduledBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scheduledBy))
            return "Programmation APR : l'identifiant du planificateur est obligatoire.";

        if (scheduledAt <= DateTimeOffset.UtcNow.AddMinutes(-1))
            return "Programmation APR : la date de lancement programmée doit être future.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var apr = await db.PreventiveRecommendedActions
            .FirstOrDefaultAsync(a => a.Id == aprId, ct);

        if (apr is null) return "APR introuvable.";
        if (!StatutsAutorises.Contains(apr.APRStatus))
            return $"Impossible de programmer une APR en statut {apr.APRStatus}. "
                 + $"Statuts attendus : {string.Join(", ", StatutsAutorises)}.";

        var maintenant = DateTimeOffset.UtcNow;
        apr.APRStatus = AprStatus.Programmee;
        apr.ScheduledAt = scheduledAt;
        apr.ScheduledBy = scheduledBy;
        apr.ModifiedAt = maintenant;

        await db.SaveChangesAsync(ct);
        return null;
    }
}
