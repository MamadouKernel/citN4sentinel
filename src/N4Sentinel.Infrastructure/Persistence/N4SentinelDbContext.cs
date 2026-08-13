using System.Text.Json;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Identity;

namespace N4Sentinel.Infrastructure.Persistence;

/// <summary>
/// Contexte unique de N4 Sentinel : referentiel technique, journal d'audit et
/// tables Identity partagent la meme base et le meme historique de migrations.
///
/// Un seul contexte est un choix delibere : deux jeux de migrations sur une
/// meme base multiplient les occasions de desynchronisation, pour un benefice
/// nul a cette echelle.
/// </summary>
public class N4SentinelDbContext(DbContextOptions<N4SentinelDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<N4Environment> Environments => Set<N4Environment>();
    public DbSet<N4Server> Servers => Set<N4Server>();
    public DbSet<N4Component> Components => Set<N4Component>();
    public DbSet<ComponentDependency> ComponentDependencies => Set<ComponentDependency>();
    public DbSet<ComponentHealthCheck> ComponentHealthChecks => Set<ComponentHealthCheck>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<TechnicalCredential> Credentials => Set<TechnicalCredential>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Les listes de motifs sont stockees en JSON : elles se lisent et
        // s'editent comme un tout, jamais unitairement. Une table dediee
        // n'apporterait qu'une jointure supplementaire.
        var jsonListConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());

        var jsonListComparer = new ValueComparer<List<string>>(
            (a, b) => a != null && b != null && a.SequenceEqual(b),
            v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v.ToList());

        builder.Entity<N4Environment>(e =>
        {
            e.ToTable("Environments");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.TechnicalOwner).HasMaxLength(150);
            e.Property(x => x.FunctionalOwner).HasMaxLength(150);
            e.Property(x => x.DefaultCredentialReference).HasMaxLength(200);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.IsProduction);
            e.Ignore(x => x.IsOperable);
        });

        builder.Entity<TechnicalCredential>(e =>
        {
            e.ToTable("Credentials");

            // La reference est unique dans un environnement, pas globalement :
            // "compte-service-n4" peut designer un compte en UAT et un autre en
            // Production, ce qui est precisement la separation attendue (SEC-004).
            e.HasIndex(x => new { x.EnvironmentId, x.Reference }).IsUnique();

            e.Property(x => x.Reference).HasMaxLength(200).IsRequired();
            e.Property(x => x.Label).HasMaxLength(200).IsRequired();
            e.Property(x => x.UserName).HasMaxLength(256);

            // Le chiffre est nettement plus long que le clair : Data Protection
            // ajoute en-tete, cle et signature.
            e.Property(x => x.ProtectedPassword).HasMaxLength(4000);

            e.Property(x => x.LastVerificationResult).HasMaxLength(400);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.Ignore(x => x.IsUsable);
            e.Ignore(x => x.SecretState);

            e.HasOne(x => x.Environment)
             .WithMany()
             .HasForeignKey(x => x.EnvironmentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<N4Server>(e =>
        {
            e.ToTable("Servers");
            e.HasIndex(x => new { x.EnvironmentId, x.HostName }).IsUnique();
            e.Property(x => x.HostName).HasMaxLength(255).IsRequired();
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.Property(x => x.DnsName).HasMaxLength(255);
            e.Property(x => x.OperatingSystem).HasMaxLength(150);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.TechnicalOwner).HasMaxLength(150);
            e.Property(x => x.CredentialReference).HasMaxLength(200);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasOne(x => x.Environment)
             .WithMany(x => x.Servers)
             .HasForeignKey(x => x.EnvironmentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<N4Component>(e =>
        {
            e.ToTable("Components");
            e.HasIndex(x => new { x.EnvironmentId, x.LogicalName }).IsUnique();
            e.Property(x => x.LogicalName).HasMaxLength(150).IsRequired();
            e.Property(x => x.WindowsServiceName).HasMaxLength(255);
            e.Property(x => x.ProcessName).HasMaxLength(255);
            e.Property(x => x.Endpoint).HasMaxLength(500);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.TechnicalOwner).HasMaxLength(150);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.CanBeControlled);

            e.HasOne(x => x.Environment)
             .WithMany(x => x.Components)
             .HasForeignKey(x => x.EnvironmentId)
             .OnDelete(DeleteBehavior.Cascade);

            // Le serveur ne se supprime pas en cascade : on refuse de supprimer
            // un serveur qui porte encore des composants declares.
            e.HasOne(x => x.Server)
             .WithMany(x => x.Components)
             .HasForeignKey(x => x.ServerId)
             .OnDelete(DeleteBehavior.Restrict);

            e.OwnsOne(x => x.Readiness, r =>
            {
                r.Property(p => p.LogPath).HasMaxLength(500).HasColumnName("Readiness_LogPath");
                r.Property(p => p.ReadyPatterns).HasConversion(jsonListConverter).Metadata.SetValueComparer(jsonListComparer);
                r.Property(p => p.ReadyPatterns).HasColumnName("Readiness_ReadyPatterns");
                r.Property(p => p.ErrorPatterns).HasConversion(jsonListConverter).Metadata.SetValueComparer(jsonListComparer);
                r.Property(p => p.ErrorPatterns).HasColumnName("Readiness_ErrorPatterns");
                r.Property(p => p.IgnorePatterns).HasConversion(jsonListConverter).Metadata.SetValueComparer(jsonListComparer);
                r.Property(p => p.IgnorePatterns).HasColumnName("Readiness_IgnorePatterns");
                r.Property(p => p.ServiceRunningTimeoutSeconds).HasColumnName("Readiness_ServiceRunningTimeoutSeconds");
                r.Property(p => p.LogReadyTimeoutSeconds).HasColumnName("Readiness_LogReadyTimeoutSeconds");
                r.Property(p => p.StopTimeoutSeconds).HasColumnName("Readiness_StopTimeoutSeconds");
                r.Property(p => p.PollIntervalSeconds).HasColumnName("Readiness_PollIntervalSeconds");
                r.Property(p => p.ProgressEverySeconds).HasColumnName("Readiness_ProgressEverySeconds");
                r.Property(p => p.PostReadySettleSeconds).HasColumnName("Readiness_PostReadySettleSeconds");
                r.Ignore(p => p.IsProvable);
            });
        });

        builder.Entity<ComponentDependency>(e =>
        {
            e.ToTable("ComponentDependencies");
            e.HasIndex(x => new { x.ComponentId, x.DependsOnComponentId }).IsUnique();
            e.Property(x => x.Notes).HasMaxLength(500);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasOne(x => x.Component)
             .WithMany(x => x.Dependencies)
             .HasForeignKey(x => x.ComponentId)
             .OnDelete(DeleteBehavior.Cascade);

            // Pas de cascade sur la cible : supprimer un composant dont un
            // autre depend doit echouer bruyamment, pas casser le graphe
            // en silence.
            e.HasOne(x => x.DependsOnComponent)
             .WithMany()
             .HasForeignKey(x => x.DependsOnComponentId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ComponentHealthCheck>(e =>
        {
            e.ToTable("ComponentHealthChecks");
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.Target).HasMaxLength(500);
            e.Property(x => x.ExpectedValue).HasMaxLength(500);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasOne(x => x.Component)
             .WithMany(x => x.HealthChecks)
             .HasForeignKey(x => x.ComponentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditEntries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(256).IsRequired();
            e.Property(x => x.ActorIpAddress).HasMaxLength(45);
            e.Property(x => x.EntityType).HasMaxLength(150).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.EntityLabel).HasMaxLength(300);
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.Property(x => x.Detail).HasMaxLength(4000);

            // Index orientes consultation : "que s'est-il passe sur cet
            // environnement, ce jour-la, et qui en est l'auteur ?"
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.EnvironmentId, x.OccurredAt });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.CorrelationId);
        });

        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(150);
            e.Property(x => x.Department).HasMaxLength(150);
        });
    }
}
