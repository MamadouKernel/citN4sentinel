using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using N4Sentinel.Domain;

namespace N4Sentinel.Infrastructure.Persistence;

/// <summary>
/// Horodate les entites du referentiel et alimente le journal d'audit a
/// chaque enregistrement.
///
/// Pourquoi un intercepteur plutot qu'un appel explicite dans chaque service :
/// le cahier des charges exige que TOUTE modification de configuration soit
/// tracee. Une trace qu'il faut penser a ecrire finit par etre oubliee ; une
/// trace posee au point de passage obligatoire ne peut pas l'etre.
///
/// Les entrees d'audit elles-memes ne sont jamais auditees (pas de recursion),
/// et ne sont jamais modifiees : seules des insertions sont produites ici.
/// </summary>
public sealed class AuditingInterceptor(ICurrentUserContext currentUser) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Proprietes jamais recopiees dans le journal : elles n'apportent rien a
    /// la lecture d'un audit et alourdiraient chaque entree.
    /// </summary>
    private static readonly HashSet<string> IgnoredProperties =
    [
        nameof(AuditableEntity.RowVersion),
        nameof(AuditableEntity.CreatedAt),
        nameof(AuditableEntity.CreatedBy),
        nameof(AuditableEntity.ModifiedAt),
        nameof(AuditableEntity.ModifiedBy)
    ];

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null) return;

        var now = DateTimeOffset.UtcNow;
        var actor = currentUser.Actor;
        var entries = new List<AuditEntry>();

        foreach (var entry in context.ChangeTracker.Entries<AuditableEntity>().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = actor;
                    break;
                case EntityState.Modified:
                    entry.Entity.ModifiedAt = now;
                    entry.Entity.ModifiedBy = actor;
                    // La date et l'auteur de creation ne se reecrivent jamais.
                    entry.Property(nameof(AuditableEntity.CreatedAt)).IsModified = false;
                    entry.Property(nameof(AuditableEntity.CreatedBy)).IsModified = false;
                    break;
            }

            entries.Add(BuildAuditEntry(entry, now, actor));
        }

        if (entries.Count > 0)
            context.Set<AuditEntry>().AddRange(entries);
    }

    private AuditEntry BuildAuditEntry(EntityEntry<AuditableEntity> entry, DateTimeOffset now, string actor)
    {
        var action = entry.State switch
        {
            EntityState.Added => AuditAction.Creation,
            EntityState.Deleted => AuditAction.Suppression,
            _ => AuditAction.Modification
        };

        // Sur une modification, on ne conserve que ce qui a REELLEMENT change :
        // un journal ou chaque ligne repete l'objet entier est illisible.
        string? before = null;
        string? after = null;

        if (entry.State == EntityState.Modified)
        {
            var changed = entry.Properties
                .Where(p => p.IsModified && !IgnoredProperties.Contains(p.Metadata.Name))
                .ToList();

            if (changed.Count > 0)
            {
                before = JsonSerializer.Serialize(
                    changed.ToDictionary(p => p.Metadata.Name, p => p.OriginalValue), JsonOptions);
                after = JsonSerializer.Serialize(
                    changed.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue), JsonOptions);
            }
        }
        else if (entry.State == EntityState.Added)
        {
            after = JsonSerializer.Serialize(Snapshot(entry, original: false), JsonOptions);
        }
        else
        {
            before = JsonSerializer.Serialize(Snapshot(entry, original: true), JsonOptions);
        }

        return new AuditEntry
        {
            OccurredAt = now,
            Actor = actor,
            ActorIpAddress = currentUser.IpAddress,
            CorrelationId = currentUser.CorrelationId,
            Action = action,
            Outcome = AuditOutcome.Succes,
            EntityType = entry.Entity.GetType().Name,
            EntityId = entry.Entity.Id.ToString(),
            EntityLabel = DescribeEntity(entry.Entity),
            EnvironmentId = ResolveEnvironmentId(entry.Entity),
            BeforeJson = before,
            AfterJson = after
        };
    }

    private static Dictionary<string, object?> Snapshot(EntityEntry<AuditableEntity> entry, bool original) =>
        entry.Properties
            .Where(p => !IgnoredProperties.Contains(p.Metadata.Name))
            .ToDictionary(p => p.Metadata.Name, p => original ? p.OriginalValue : p.CurrentValue);

    /// <summary>
    /// Libelle lisible conserve dans l'audit. Il reste juste meme si l'objet
    /// est renomme ou supprime plus tard - c'est tout l'interet de le figer.
    /// </summary>
    private static string? DescribeEntity(AuditableEntity entity) => entity switch
    {
        N4Environment e => $"{e.Code} - {e.Name}",
        N4Server s => s.HostName,
        N4Component c => c.LogicalName,
        ComponentHealthCheck h => h.Name,
        ComponentDependency => "dependance",
        _ => null
    };

    private static Guid? ResolveEnvironmentId(AuditableEntity entity) => entity switch
    {
        N4Environment e => e.Id,
        N4Server s => s.EnvironmentId,
        N4Component c => c.EnvironmentId,
        _ => null
    };
}
