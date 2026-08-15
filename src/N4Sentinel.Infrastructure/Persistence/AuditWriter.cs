using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;

namespace N4Sentinel.Infrastructure.Persistence;

/// <summary>
/// Point d'ecriture explicite du journal d'audit pour tout ce qui n'est PAS
/// un changement d'entite du referentiel (donc hors de portee d'
/// <see cref="AuditingInterceptor"/>) : connexions, deconnexions, echecs
/// d'authentification, tentatives non autorisees, contournements et
/// executions d'operation (SEC-008, FR-091).
///
/// Une connexion echouee n'a pas d'utilisateur authentifie a interroger via
/// <see cref="ICurrentUserContext"/> - c'est justement ce qu'on trace. L'acteur
/// se passe donc explicitement, plutot que d'etre devine du contexte HTTP.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(
        AuditAction action,
        AuditOutcome outcome,
        string actor,
        string? ipAddress = null,
        string? entityType = null,
        string? entityId = null,
        string? entityLabel = null,
        Guid? environmentId = null,
        string? reason = null,
        string? detail = null,
        string? correlationId = null,
        CancellationToken ct = default);
}

public sealed class AuditWriter(IDbContextFactory<N4SentinelDbContext> dbFactory) : IAuditWriter
{
    public async Task WriteAsync(
        AuditAction action, AuditOutcome outcome, string actor, string? ipAddress = null,
        string? entityType = null, string? entityId = null, string? entityLabel = null,
        Guid? environmentId = null, string? reason = null, string? detail = null,
        string? correlationId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Actor = actor,
            ActorIpAddress = ipAddress,
            Action = action,
            Outcome = outcome,
            EntityType = entityType ?? string.Empty,
            EntityId = entityId,
            EntityLabel = entityLabel,
            EnvironmentId = environmentId,
            Reason = reason,
            Detail = detail,
            CorrelationId = correlationId
        });

        // Ecriture directe : ces entrees ne dependent d'aucune autre
        // modification en cours dans le meme circuit Blazor, inutile
        // d'attendre un SaveChanges ailleurs.
        await db.SaveChangesAsync(ct);
    }
}
