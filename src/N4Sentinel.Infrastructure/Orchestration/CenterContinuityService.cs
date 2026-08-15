using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Évalue l'aptitude du Standby à prendre le rôle actif avant une bascule
/// Center (FR-046/047).
///
/// Ne décide RIEN à la place de l'opérateur — le choix (rester actif ou
/// basculer) est le sien, recueilli sur l'exécution. Ce service répond
/// seulement à la question qui précède ce choix : le Standby est-il, là,
/// maintenant, en état de prendre le relais ?
/// </summary>
public sealed class CenterContinuityService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    SupervisionService supervision)
{
    public async Task<ContinuityAssessment> AssessAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var center = await db.Components.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EnvironmentId == environmentId && c.Role == ComponentRole.CenterNode, ct);
        var standby = await db.Components.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EnvironmentId == environmentId && c.Role == ComponentRole.StandbyCenterNode, ct);

        var centerHealth = center is not null ? await supervision.EvaluateComponentAsync(center.Id, ct) : null;
        var standbyHealth = standby is not null ? await supervision.EvaluateComponentAsync(standby.Id, ct) : null;

        return new ContinuityAssessment(center, standby, centerHealth, standbyHealth);
    }
}

public sealed record ContinuityAssessment(
    N4Component? Center, N4Component? Standby,
    ComponentHealthSnapshot? CenterHealth, ComponentHealthSnapshot? StandbyHealth)
{
    /// <summary>Le Standby répond et son état consolidé est Disponible.</summary>
    public bool StandbyIsCapable => Standby is not null && StandbyHealth?.State == ComponentState.Disponible;

    /// <summary>FR-032/033 : les deux tiendraient le rôle actif en même temps — jamais souhaitable.</summary>
    public bool BothHoldActiveRole => CenterHealth?.HoldsActiveRole == true && StandbyHealth?.HoldsActiveRole == true;

    /// <summary>Motif lisible quand la bascule n'est pas raisonnable en l'état ; null sinon.</summary>
    public string? StandbyUnavailableReason => Standby is null
        ? "Aucun Standby n'est déclaré pour cet environnement : il n'y a rien vers quoi basculer."
        : !StandbyIsCapable
            ? $"Le Standby « {Standby.LogicalName} » n'est pas disponible : {StandbyHealth?.Verdict ?? "état non évalué"}."
            : null;
}
