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
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();
    public DbSet<WorkflowExecution> Executions => Set<WorkflowExecution>();
    public DbSet<ExecutionStep> ExecutionSteps => Set<ExecutionStep>();
    public DbSet<EnvironmentLock> EnvironmentLocks => Set<EnvironmentLock>();

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

        builder.Entity<Alert>(e =>
        {
            e.ToTable("Alerts");
            e.HasKey(x => x.Id);

            e.Property(x => x.Signature).HasMaxLength(120).IsRequired();
            e.Property(x => x.ComponentName).HasMaxLength(150);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.Property(x => x.Recommendation).HasMaxLength(2000);
            e.Property(x => x.AcknowledgedBy).HasMaxLength(256);
            e.Property(x => x.AcknowledgementNote).HasMaxLength(1000);

            e.Ignore(x => x.IsOpen);
            e.Ignore(x => x.Duration);

            // Index de deduplication : chaque passe de collecte cherche une
            // alerte ouverte portant cette signature. Sans lui, la recherche
            // balayerait toute la table toutes les trente secondes.
            e.HasIndex(x => new { x.ComponentId, x.Signature, x.Status });

            // Index de consultation : "que se passe-t-il en ce moment sur cet
            // environnement".
            e.HasIndex(x => new { x.EnvironmentId, x.Status, x.LastOccurredAt });

            e.HasOne(x => x.Environment)
             .WithMany()
             .HasForeignKey(x => x.EnvironmentId)
             .OnDelete(DeleteBehavior.Cascade);

            // Pas de cascade depuis le composant : supprimer un composant ne
            // doit pas effacer l'historique des alertes qu'il a produites.
            e.HasOne(x => x.Component)
             .WithMany()
             .HasForeignKey(x => x.ComponentId)
             .OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<Workflow>(e =>
        {
            e.ToTable("Workflows");

            // Le couple code + version est unique : c'est ce qui permet a
            // plusieurs versions d'un meme workflow de coexister, l'ancienne
            // restant rattachee aux executions qu'elle a produites.
            e.HasIndex(x => new { x.EnvironmentId, x.Code, x.Version }).IsUnique();

            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.IsRunnable);
            e.Ignore(x => x.DisplayName);

            e.HasOne(x => x.Environment).WithMany()
             .HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WorkflowStep>(e =>
        {
            e.ToTable("WorkflowSteps");
            e.HasIndex(x => new { x.WorkflowId, x.Order });
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Instruction).HasMaxLength(2000);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasOne(x => x.Workflow).WithMany(x => x.Steps)
             .HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);

            // Supprimer un composant ne doit pas effacer en silence les etapes
            // qui le visent : l'echec doit etre bruyant.
            e.HasOne(x => x.Component).WithMany()
             .HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<WorkflowExecution>(e =>
        {
            e.ToTable("Executions");
            e.HasIndex(x => new { x.EnvironmentId, x.Status });
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.StartedAt);

            e.Property(x => x.WorkflowName).HasMaxLength(200);
            e.Property(x => x.EnvironmentCode).HasMaxLength(20);
            e.Property(x => x.RequestedBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.TicketReference).HasMaxLength(100);
            e.Property(x => x.ExpectedImpact).HasMaxLength(2000);
            e.Property(x => x.ApprovedBy).HasMaxLength(256);
            e.Property(x => x.PauseRequestedBy).HasMaxLength(256);
            e.Property(x => x.CancelRequestedBy).HasMaxLength(256);
            e.Property(x => x.Outcome).HasMaxLength(2000);
            e.Property(x => x.CorrelationId).HasMaxLength(40);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.IsFinished);
            e.Ignore(x => x.Duration);
            e.Ignore(x => x.PreflightCleared);

            // Le workflow ne se supprime pas en cascade : l'historique des
            // executions doit survivre a la suppression du modele.
            e.HasOne(x => x.Workflow).WithMany()
             .HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.NoAction);

            e.HasOne(x => x.Environment).WithMany()
             .HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<ExecutionStep>(e =>
        {
            e.ToTable("ExecutionSteps");
            e.HasIndex(x => new { x.ExecutionId, x.Order });

            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.ComponentName).HasMaxLength(150);
            e.Property(x => x.HostName).HasMaxLength(255);
            e.Property(x => x.ProgressMessage).HasMaxLength(1000);
            e.Property(x => x.Evidence).HasMaxLength(2000);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.SkippedBy).HasMaxLength(256);
            e.Property(x => x.SkipReason).HasMaxLength(1000);
            e.Property(x => x.ConfirmedBy).HasMaxLength(256);
            e.Property(x => x.OperatorNote).HasMaxLength(2000);
            e.Property(x => x.Instruction).HasMaxLength(2000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.IsTerminal);
            e.Ignore(x => x.Duration);

            e.HasOne(x => x.Execution).WithMany(x => x.Steps)
             .HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<EnvironmentLock>(e =>
        {
            e.ToTable("EnvironmentLocks");

            // UN SEUL VERROU PAR ENVIRONNEMENT, garanti par la base et non par
            // le code applicatif. Deux instances de N4 Sentinel, ou deux
            // requetes simultanees, ne peuvent pas obtenir le meme verrou : la
            // seconde insertion echoue sur la contrainte d'unicite.
            e.HasIndex(x => x.EnvironmentId).IsUnique();

            e.Property(x => x.HeldBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Ignore(x => x.IsExpired);

            e.HasOne(x => x.Environment).WithMany()
             .HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(150);
            e.Property(x => x.Department).HasMaxLength(150);
        });
    }
}
