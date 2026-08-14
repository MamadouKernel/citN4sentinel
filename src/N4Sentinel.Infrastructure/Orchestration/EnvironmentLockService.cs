using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Verrou d'environnement : une seule opération mutative à la fois
/// (FR-015, recette AC-12).
///
/// L'unicité est garantie par un index UNIQUE en base, pas par une
/// vérification applicative. Un « lire puis écrire » laisserait passer deux
/// demandes simultanées, et deux séquences d'arrêt lancées en parallèle sur le
/// même écosystème produiraient un état que personne ne saurait plus décrire.
/// Ici, la seconde insertion échoue — la base tranche, pas le code.
///
/// Les diagnostics en lecture ne prennent pas de verrou : ils ne modifient
/// rien et ne doivent pas être empêchés pendant une opération. C'est même
/// pendant une opération qu'on en a le plus besoin.
/// </summary>
public sealed class EnvironmentLockService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<EnvironmentLockService> logger)
{
    /// <summary>
    /// Durée de vie du verrou. Prolongée par le battement du moteur tant que
    /// l'exécution vit. Sans expiration, un serveur applicatif qui meurt en
    /// tenant le verrou bloquerait l'environnement définitivement, et il
    /// faudrait intervenir en base pour le libérer.
    /// </summary>
    public static readonly TimeSpan DureeVerrou = TimeSpan.FromMinutes(10);

    public async Task<LockResult> AcquireAsync(
        Guid environmentId, Guid executionId, string heldBy, string reason,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Un verrou expire est nettoye avant toute tentative : son detenteur
        // ne bat plus, il ne travaille donc plus.
        var existant = await db.EnvironmentLocks
            .FirstOrDefaultAsync(l => l.EnvironmentId == environmentId, ct);

        if (existant is not null)
        {
            // L'execution reprend SON PROPRE verrou : ce n'est pas une operation
            // concurrente. Le cas se presente a chaque reprise apres pause ou
            // apres reconciliation, ou le verrou n'a jamais ete rendu puisque
            // l'environnement restait dans un etat intermediaire.
            if (existant.ExecutionId == executionId)
            {
                existant.HeldBy = heldBy;
                existant.Reason = reason;
                existant.ExpiresAt = DateTimeOffset.UtcNow.Add(DureeVerrou);
                await db.SaveChangesAsync(ct);
                return LockResult.Granted(existant.Id);
            }

            if (!existant.IsExpired)
            {
                var execution = await db.Executions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == existant.ExecutionId, ct);

                return LockResult.Refused(
                    $"Une opération est déjà en cours sur cet environnement : « {execution?.WorkflowName ?? "opération"} », "
                    + $"lancée par {existant.HeldBy} à {existant.AcquiredAt.ToLocalTime():HH:mm}. "
                    + "Une seule opération mutative est autorisée à la fois. "
                    + "Les diagnostics en lecture restent possibles.",
                    existant.ExecutionId);
            }

            logger.LogWarning(
                "Verrou expiré libéré sur l'environnement {Env} : il était détenu par {Detenteur} "
                + "pour l'exécution {Execution}, dont le moteur ne bat plus depuis {Expiration}.",
                environmentId, existant.HeldBy, existant.ExecutionId, existant.ExpiresAt);

            db.EnvironmentLocks.Remove(existant);
            await db.SaveChangesAsync(ct);
        }

        var verrou = new EnvironmentLock
        {
            EnvironmentId = environmentId,
            ExecutionId = executionId,
            HeldBy = heldBy,
            Reason = reason,
            ExpiresAt = DateTimeOffset.UtcNow.Add(DureeVerrou)
        };

        db.EnvironmentLocks.Add(verrou);

        try
        {
            await db.SaveChangesAsync(ct);
            return LockResult.Granted(verrou.Id);
        }
        catch (DbUpdateException)
        {
            // Course entre deux demandes simultanees : l'index unique a
            // tranche. C'est le comportement voulu - mieux vaut un refus
            // qu'une seconde operation qui demarre en croyant etre seule.
            logger.LogWarning(
                "Acquisition concurrente refusée sur l'environnement {Env} : un autre verrou a été posé "
                + "entre la vérification et l'insertion.", environmentId);

            return LockResult.Refused(
                "Une autre opération vient d'être lancée sur cet environnement. Réessayez dans un instant.",
                null);
        }
    }

    /// <summary>Prolonge le verrou. Appelé par le battement du moteur.</summary>
    public async Task RenewAsync(Guid executionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var verrou = await db.EnvironmentLocks.FirstOrDefaultAsync(l => l.ExecutionId == executionId, ct);
        if (verrou is null) return;

        verrou.ExpiresAt = DateTimeOffset.UtcNow.Add(DureeVerrou);
        await db.SaveChangesAsync(ct);
    }

    public async Task ReleaseAsync(Guid executionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var verrou = await db.EnvironmentLocks.FirstOrDefaultAsync(l => l.ExecutionId == executionId, ct);
        if (verrou is null) return;

        db.EnvironmentLocks.Remove(verrou);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Verrou libéré sur l'environnement {Env}.", verrou.EnvironmentId);
    }

    public async Task<EnvironmentLock?> GetAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.EnvironmentLocks
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.EnvironmentId == environmentId, ct);
    }
}

public sealed record LockResult
{
    public bool Succeeded { get; init; }
    public Guid? LockId { get; init; }
    public string? Error { get; init; }

    /// <summary>Exécution qui détient le verrou, pour renvoyer l'opérateur vers elle.</summary>
    public Guid? BlockedBy { get; init; }

    public static LockResult Granted(Guid lockId) => new() { Succeeded = true, LockId = lockId };

    public static LockResult Refused(string error, Guid? blockedBy) =>
        new() { Succeeded = false, Error = error, BlockedBy = blockedBy };
}
