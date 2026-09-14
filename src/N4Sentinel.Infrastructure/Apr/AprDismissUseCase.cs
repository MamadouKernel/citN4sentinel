using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Apr;

/// <summary>
/// Transitions une APR ouverte → Ignorée (rejet EXPLICITE, pas juste laisser
/// disparaître dans la nature). RG-P2 : DismissalReason obligatoire (pas vide)
/// pour audit post-incident (pourquoi une mesure préventive recommandée n'a pas
/// été prise — corrélation avec un futur P1).
/// Pattern : scellé Scoped, retour string? null = succès.
/// </summary>
public sealed class AprDismissUseCase(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    private static readonly AprStatus[] StatutsAutorises = new[]
    {
        AprStatus.Detectee,
        AprStatus.Proposee,
        AprStatus.Programmee,
    };

    public async Task<string?> ExecuteAsync(
        Guid aprId,
        string dismissalReason,
        string actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
            return "Ignorer APR : l'identifiant de l'acteur est obligatoire.";

        if (string.IsNullOrWhiteSpace(dismissalReason))
            return "RG-P2 · Ignorer APR : le motif d'ignorance est OBLIGATOIRE. "
                 + "En cas d'incident P1 ultérieur, on doit pouvoir expliquer pourquoi "
                 + "l'APR préventive n'a pas été appliquée (audit SEC-007).";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var apr = await db.PreventiveRecommendedActions
            .FirstOrDefaultAsync(a => a.Id == aprId, ct);

        if (apr is null) return "APR introuvable.";
        if (!StatutsAutorises.Contains(apr.APRStatus))
            return $"Impossible d'ignorer une APR en statut {apr.APRStatus}. "
                 + $"Statuts autorisés : {string.Join(", ", StatutsAutorises)}.";

        var maintenant = DateTimeOffset.UtcNow;
        apr.APRStatus = AprStatus.Ignoree;
        apr.DismissedBy = actor;
        apr.DismissedAt = maintenant;
        apr.DismissalReason = dismissalReason.Trim();
        apr.ModifiedAt = maintenant;

        await db.SaveChangesAsync(ct);
        return null;
    }
}
