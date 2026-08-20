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
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<TechnicalCredential> Credentials => Set<TechnicalCredential>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();
    public DbSet<WorkflowExecution> Executions => Set<WorkflowExecution>();
    public DbSet<ExecutionStep> ExecutionSteps => Set<ExecutionStep>();
    public DbSet<EnvironmentLock> EnvironmentLocks => Set<EnvironmentLock>();
    public DbSet<DiagnosticSignature> Signatures => Set<DiagnosticSignature>();
    public DbSet<DiagnosticSession> Sessions => Set<DiagnosticSession>();
    public DbSet<LogSource> Sources => Set<LogSource>();
    public DbSet<LogFinding> Findings => Set<LogFinding>();
    public DbSet<DiagnosticHypothesis> Hypotheses => Set<DiagnosticHypothesis>();
    public DbSet<DiagnosticPhaseTransition> PhaseTransitions => Set<DiagnosticPhaseTransition>();
    public DbSet<EnvironmentGrant> EnvironmentGrants => Set<EnvironmentGrant>();
    public DbSet<KnowledgeDocument> Documents => Set<KnowledgeDocument>();
    public DbSet<DocumentSection> DocumentSections => Set<DocumentSection>();
    public DbSet<KnowledgeFeedback> KnowledgeFeedback => Set<KnowledgeFeedback>();
    public DbSet<CorrelationRule> CorrelationRules => Set<CorrelationRule>();
    public DbSet<CorrelationCondition> CorrelationConditions => Set<CorrelationCondition>();
    public DbSet<Sop> Sops => Set<Sop>();
    public DbSet<SopStep> SopSteps => Set<SopStep>();
    public DbSet<SopAssociation> SopAssociations => Set<SopAssociation>();
    public DbSet<SopExecution> SopExecutions => Set<SopExecution>();
    public DbSet<SopExecutionStep> SopExecutionSteps => Set<SopExecutionStep>();
    public DbSet<SharedFolderSnapshot> SharedFolderSnapshots => Set<SharedFolderSnapshot>();
    public DbSet<EdiFile> EdiFiles => Set<EdiFile>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<DiagnosticSettings> DiagnosticSettings => Set<DiagnosticSettings>();
    public DbSet<AzureAdSettings> AzureAdSettings => Set<AzureAdSettings>();
    public DbSet<ComponentSignal> ComponentSignals => Set<ComponentSignal>();
    public DbSet<ExternalActionDeclaration> ExternalActionDeclarations => Set<ExternalActionDeclaration>();
    public DbSet<ApprovalMatrixRule> ApprovalMatrixRules => Set<ApprovalMatrixRule>();
    public DbSet<PasswordHistoryRecord> PasswordHistoryRecords => Set<PasswordHistoryRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Les listes de motifs sont stockees en JSON : elles se lisent et
        // s'editent comme un tout, jamais unitairement. Une table dediee
        // n'apporterait qu'une jointure supplementaire.
        //
        // ATTENTION migrations futures : toute colonne ajoutee via ce
        // convertisseur (AddColumn sur une table existante) doit utiliser
        // `defaultValue: "[]"`, jamais `""`. Une chaine vide n'est pas un JSON
        // valide ; `dotnet ef migrations add` genere `""` par defaut pour un
        // nouveau `List<string>`, et il faut le corriger a la main dans le
        // fichier de migration avant de committer (vecu avec
        // Readiness_ActiveRolePatterns, cf. migration RoleActifCenter).
        var jsonListConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());

        var jsonListComparer = new ValueComparer<List<string>>(
            (a, b) => a != null && b != null && a.SequenceEqual(b),
            v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v.ToList());

        builder.Entity<PasswordHistoryRecord>(e =>
        {
            e.ToTable("PasswordHistoryRecords");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.Property(x => x.PasswordHash).HasMaxLength(1000).IsRequired();

            e.HasOne(x => x.User).WithMany()
             .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

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
            e.Property(x => x.ExpectedTimeSource).HasMaxLength(200);
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
                r.Property(p => p.ActiveRolePatterns).HasConversion(jsonListConverter).Metadata.SetValueComparer(jsonListComparer);
                r.Property(p => p.ActiveRolePatterns).HasColumnName("Readiness_ActiveRolePatterns");
                r.Property(p => p.SyncPatterns).HasConversion(jsonListConverter).Metadata.SetValueComparer(jsonListComparer);
                r.Property(p => p.SyncPatterns).HasColumnName("Readiness_SyncPatterns");
                r.Property(p => p.SyncDelayThresholdMinutes).HasColumnName("Readiness_SyncDelayThresholdMinutes");
                r.Property(p => p.ServiceRunningTimeoutSeconds).HasColumnName("Readiness_ServiceRunningTimeoutSeconds");
                r.Property(p => p.LogReadyTimeoutSeconds).HasColumnName("Readiness_LogReadyTimeoutSeconds");
                r.Property(p => p.StopTimeoutSeconds).HasColumnName("Readiness_StopTimeoutSeconds");
                r.Property(p => p.PollIntervalSeconds).HasColumnName("Readiness_PollIntervalSeconds");
                r.Property(p => p.ProgressEverySeconds).HasColumnName("Readiness_ProgressEverySeconds");
                r.Property(p => p.PostReadySettleSeconds).HasColumnName("Readiness_PostReadySettleSeconds");
                r.Ignore(p => p.IsProvable);
            });

            e.OwnsOne(x => x.SharedFolder, sf =>
            {
                sf.Property(p => p.RootPath).HasMaxLength(500).HasColumnName("SharedFolder_RootPath");
                sf.Property(p => p.Category).HasColumnName("SharedFolder_Category");
                sf.Property(p => p.PendingSubfolder).HasMaxLength(200).HasColumnName("SharedFolder_PendingSubfolder");
                sf.Property(p => p.ConsumedSubfolder).HasMaxLength(200).HasColumnName("SharedFolder_ConsumedSubfolder");
                sf.Property(p => p.BlockedSubfolder).HasMaxLength(200).HasColumnName("SharedFolder_BlockedSubfolder");
                sf.Property(p => p.ErrorSubfolder).HasMaxLength(200).HasColumnName("SharedFolder_ErrorSubfolder");
                sf.Property(p => p.MaxPendingAgeHours).HasColumnName("SharedFolder_MaxPendingAgeHours");
                sf.Property(p => p.EdiFileNamingPattern).HasMaxLength(500).HasColumnName("SharedFolder_EdiFileNamingPattern");
                sf.Property(p => p.MaxHoursSinceLastIntegration).HasColumnName("SharedFolder_MaxHoursSinceLastIntegration");
                sf.Property(p => p.MaxWriteLatencyMs).HasColumnName("SharedFolder_MaxWriteLatencyMs");
                sf.Property(p => p.MaxGrowthBytesPerHour).HasColumnName("SharedFolder_MaxGrowthBytesPerHour");
                sf.Property(p => p.LastBackupAt).HasColumnName("SharedFolder_LastBackupAt");
                sf.Property(p => p.LastBackupBy).HasMaxLength(256).HasColumnName("SharedFolder_LastBackupBy");
                sf.Property(p => p.LastBackupNote).HasMaxLength(500).HasColumnName("SharedFolder_LastBackupNote");
                sf.Ignore(p => p.IsConfigured);
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

        builder.Entity<Sop>(e =>
        {
            e.ToTable("Sops");

            // Meme regle que Workflow : code + version unique, l'ancienne
            // version restant rattachee a ses executions passees.
            e.HasIndex(x => new { x.EnvironmentId, x.Code, x.Version }).IsUnique();

            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Objective).HasMaxLength(2000);
            e.Property(x => x.Scope).HasMaxLength(2000);
            e.Property(x => x.Prerequisites).HasMaxLength(2000);
            e.Property(x => x.Risks).HasMaxLength(2000);
            e.Property(x => x.Controls).HasMaxLength(2000);
            e.Property(x => x.ExpectedOutcome).HasMaxLength(2000);
            e.Property(x => x.RollbackPlan).HasMaxLength(2000);
            e.Property(x => x.EscalationPath).HasMaxLength(2000);
            e.Property(x => x.AppliesToVersion).HasMaxLength(50);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.IsUsable);
            e.Ignore(x => x.DisplayName);

            e.HasOne(x => x.Environment).WithMany()
             .HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SopStep>(e =>
        {
            e.ToTable("SopSteps");
            e.HasIndex(x => new { x.SopId, x.Order });
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Instruction).HasMaxLength(4000).IsRequired();
            e.Property(x => x.ExpectedResult).HasMaxLength(2000);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasOne(x => x.Sop).WithMany(x => x.Steps)
             .HasForeignKey(x => x.SopId).OnDelete(DeleteBehavior.Cascade);

            // Meme regle que WorkflowStep : la suppression d'un composant
            // vise doit echouer bruyamment, jamais effacer l'etape en silence.
            e.HasOne(x => x.Component).WithMany()
             .HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<SopAssociation>(e =>
        {
            e.ToTable("SopAssociations");
            e.HasIndex(x => new { x.ComponentId, x.Kind });
            e.HasIndex(x => x.SignatureId);
            e.Property(x => x.SignatureCode).HasMaxLength(60);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasOne(x => x.Sop).WithMany(x => x.Associations)
             .HasForeignKey(x => x.SopId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SopExecution>(e =>
        {
            e.ToTable("SopExecutions");
            e.HasIndex(x => new { x.EnvironmentId, x.Status });
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.StartedAt);

            e.Property(x => x.SopCode).HasMaxLength(60);
            e.Property(x => x.SopTitle).HasMaxLength(200);
            e.Property(x => x.EnvironmentCode).HasMaxLength(20);
            e.Property(x => x.StartedBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.TicketReference).HasMaxLength(100);
            e.Property(x => x.AbandonReason).HasMaxLength(2000);
            e.Property(x => x.CorrelationId).HasMaxLength(40);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.IsFinished);
            e.Ignore(x => x.Duration);

            // Le SOP ne se supprime pas en cascade : l'historique des
            // executions doit survivre a la suppression du modele, exactement
            // comme WorkflowExecution vis-a-vis de Workflow.
            e.HasOne(x => x.Sop).WithMany()
             .HasForeignKey(x => x.SopId).OnDelete(DeleteBehavior.NoAction);

            e.HasOne(x => x.Environment).WithMany()
             .HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<SopExecutionStep>(e =>
        {
            e.ToTable("SopExecutionSteps");
            e.HasIndex(x => new { x.SopExecutionId, x.Order });

            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Instruction).HasMaxLength(4000).IsRequired();
            e.Property(x => x.ExpectedResult).HasMaxLength(2000);
            e.Property(x => x.ComponentName).HasMaxLength(150);
            e.Property(x => x.ConfirmedBy).HasMaxLength(256);
            e.Property(x => x.Evidence).HasMaxLength(2000);
            e.Property(x => x.DeviationNote).HasMaxLength(2000);
            e.Property(x => x.SkippedBy).HasMaxLength(256);
            e.Property(x => x.SkipReason).HasMaxLength(1000);
            e.Property(x => x.History).HasMaxLength(4000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.IsTerminal);
            e.Ignore(x => x.Duration);

            e.HasOne(x => x.SopExecution).WithMany(x => x.Steps)
             .HasForeignKey(x => x.SopExecutionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SharedFolderSnapshot>(e =>
        {
            e.ToTable("SharedFolderSnapshots");
            e.HasIndex(x => new { x.ComponentId, x.CapturedAt });

            e.Property(x => x.UnreachableReason).HasMaxLength(1000);
            e.Property(x => x.MissingMandatoryFiles).HasConversion(jsonListConverter).Metadata.SetValueComparer(jsonListComparer);
            e.Property(x => x.CorruptionIndicators).HasConversion(jsonListConverter).Metadata.SetValueComparer(jsonListComparer);
            e.Property(x => x.HealthWarnings).HasConversion(jsonListConverter).Metadata.SetValueComparer(jsonListComparer);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.SuspectedCorruption);
            e.Ignore(x => x.SuspicionEnAttente);

            // FR-059D : suite donnee a une suspicion de corruption. Pas de cle
            // etrangere vers SopExecutions — le releve doit survivre a la purge
            // de l'execution, sans quoi la trace de ce qui a ete fait
            // disparaitrait avec elle.
            e.Property(x => x.CorruptionConclusion).HasMaxLength(1000);
            e.HasIndex(x => x.CorruptionConfirmed);

            // Le composant ne se supprime pas en cascade : l'historique des
            // relevés doit survivre a la suppression du composant, comme pour
            // WorkflowExecution vis-a-vis de Workflow.
            e.HasOne(x => x.Component).WithMany()
             .HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<EdiFile>(e =>
        {
            e.ToTable("EdiFiles");
            e.HasIndex(x => new { x.ComponentId, x.FileName }).IsUnique();
            e.HasIndex(x => new { x.ComponentId, x.Status });

            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.MessageType).HasMaxLength(100);
            e.Property(x => x.Partner).HasMaxLength(100);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.Age);

            e.HasOne(x => x.Component).WithMany()
             .HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<RetentionPolicy>(e =>
        {
            e.ToTable("RetentionPolicies");
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        builder.Entity<DiagnosticSettings>(e =>
        {
            e.ToTable("DiagnosticSettings");
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        builder.Entity<AzureAdSettings>(e =>
        {
            e.ToTable("AzureAdSettings");
            e.Property(x => x.TenantId).HasMaxLength(200);
            e.Property(x => x.ClientId).HasMaxLength(200);
            e.Property(x => x.Authority).HasMaxLength(500);
            e.Property(x => x.PostLogoutRedirectUri).HasMaxLength(500);
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        builder.Entity<ComponentSignal>(e =>
        {
            e.ToTable("ComponentSignals");
            e.HasIndex(x => new { x.ComponentId, x.CapturedAt });
            e.Property(x => x.ComponentName).HasMaxLength(200).IsRequired();
            e.Property(x => x.SignalType).HasMaxLength(60).IsRequired();
            e.Property(x => x.Target).HasMaxLength(200).IsRequired();
            e.Property(x => x.Value).HasMaxLength(200).IsRequired();
            e.Property(x => x.Threshold).HasMaxLength(100);
            e.Property(x => x.Quality).HasMaxLength(40).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        builder.Entity<ApprovalMatrixRule>(e =>
        {
            e.ToTable("ApprovalMatrixRules");
            e.Property(x => x.Notes).HasMaxLength(500);
            e.Property(x => x.RowVersion).IsRowVersion();
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

        // -------------------------------------------------------------------
        // Diagnostic (sprint 6)
        // -------------------------------------------------------------------
        builder.Entity<DiagnosticSignature>(e =>
        {
            e.ToTable("DiagnosticSignatures");
            e.HasIndex(x => x.Code).IsUnique();

            e.Property(x => x.Code).HasMaxLength(60).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Pattern).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Meaning).HasMaxLength(2000);
            e.Property(x => x.Remediation).HasMaxLength(2000);
            e.Property(x => x.DocumentReference).HasMaxLength(300);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.EstConcluante);
        });

        builder.Entity<DiagnosticSession>(e =>
        {
            e.ToTable("DiagnosticSessions");
            e.HasIndex(x => new { x.EnvironmentId, x.CreatedAt });

            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.EnvironmentCode).HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.TicketReference).HasMaxLength(100);
            e.Property(x => x.RequestedBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.VerdictExplanation).HasMaxLength(4000);
            e.Property(x => x.EscalatedTo).HasMaxLength(200);
            e.Property(x => x.EscalatedBy).HasMaxLength(256);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.HasBeenAnalysed);
            e.Ignore(x => x.VerdictEstInconcluant);

            e.HasOne(x => x.Environment).WithMany()
             .HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);

            // Auto-reference : Restrict, pas Cascade — supprimer une session de
            // reference ne doit jamais entrainer la suppression en cascade de
            // toutes les sessions qui la citent, et SQL Server refuse de toute
            // facon une cascade auto-referencee.
            e.HasOne(x => x.ReferenceSession).WithMany()
             .HasForeignKey(x => x.ReferenceSessionId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ExternalActionDeclaration>(e =>
        {
            e.ToTable("ExternalActionDeclarations");
            e.Property(x => x.ComponentName).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            e.Property(x => x.DeclaredBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasOne(x => x.DiagnosticSession).WithMany(x => x.ExternalActions)
             .HasForeignKey(x => x.DiagnosticSessionId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.WorkflowExecution).WithMany(x => x.ExternalActions)
             .HasForeignKey(x => x.WorkflowExecutionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LogSource>(e =>
        {
            e.ToTable("LogSources");
            e.HasIndex(x => x.SessionId);

            e.Property(x => x.FileName).HasMaxLength(400).IsRequired();
            e.Property(x => x.ResolvedPath).HasMaxLength(1000);
            e.Property(x => x.ComponentName).HasMaxLength(150);
            e.Property(x => x.HostName).HasMaxLength(255);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.DetectedVersion).HasMaxLength(100);

            // FR-071 : suggestion d'origine. Aucune cle etrangere vers
            // Components — une suggestion ne doit pas empecher la suppression
            // d'un composant du referentiel, ni etre confondue avec un
            // rattachement reel.
            e.Property(x => x.SuggestedComponentName).HasMaxLength(150);
            e.Property(x => x.SuggestionEvidence).HasMaxLength(600);

            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.Succeeded);

            e.HasOne(x => x.Session).WithMany(x => x.Sources)
             .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LogFinding>(e =>
        {
            e.ToTable("LogFindings");
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.SignatureCode);

            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.SignatureCode).HasMaxLength(60);

            // SampleLine et Context sont DEJA MASQUES a l'ecriture : le service
            // d'analyse ne persiste rien qui ne soit passe par le masqueur.
            e.Property(x => x.SampleLine).HasMaxLength(1000);
            e.Property(x => x.Context).HasMaxLength(4000);
            e.Property(x => x.Meaning).HasMaxLength(2000);
            e.Property(x => x.Remediation).HasMaxLength(2000);
            e.Property(x => x.DocumentReference).HasMaxLength(300);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasOne(x => x.Session).WithMany(x => x.Findings)
             .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);

            // Pas de cascade depuis la source : la session porte deja la
            // cascade, et SQL Server refuse deux chemins de suppression.
            e.HasOne(x => x.Source).WithMany()
             .HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<DiagnosticHypothesis>(e =>
        {
            e.ToTable("DiagnosticHypotheses");
            e.HasIndex(x => new { x.SessionId, x.Rank });

            e.Property(x => x.Statement).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Evidence).HasMaxLength(2000);
            e.Property(x => x.Recommendation).HasMaxLength(2000);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasOne(x => x.Session).WithMany(x => x.Hypotheses)
             .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CorrelationRule>(e =>
        {
            e.ToTable("CorrelationRules");
            e.Property(x => x.Code).HasMaxLength(150).IsRequired();
            e.Property(x => x.Name).HasMaxLength(300).IsRequired();
            e.Property(x => x.HypothesisStatement).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Recommendation).HasMaxLength(2000);

            e.HasIndex(x => x.Code).IsUnique();
        });

        builder.Entity<CorrelationCondition>(e =>
        {
            e.ToTable("CorrelationConditions");
            e.Property(x => x.SignalSourceId).HasMaxLength(150).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500).IsRequired();

            e.HasOne(x => x.Rule)
             .WithMany(x => x.Conditions)
             .HasForeignKey(x => x.RuleId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // -------------------------------------------------------------------
        // Base documentaire (sprint 7)
        // -------------------------------------------------------------------
        builder.Entity<EnvironmentGrant>(e =>
        {
            e.ToTable("EnvironmentGrants");

            // Une seule habilitation par couple utilisateur/environnement :
            // deux lignes contradictoires laisseraient la decision au hasard
            // de l'ordre de lecture.
            e.HasIndex(x => new { x.UserId, x.EnvironmentId }).IsUnique();

            e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.UserName).HasMaxLength(256).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.IsExpired);
            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.AllowsAction);

            e.HasOne(x => x.Environment).WithMany()
             .HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<KnowledgeDocument>(e =>
        {
            e.ToTable("KnowledgeDocuments");
            e.HasIndex(x => x.Reference).IsUnique();

            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Reference).HasMaxLength(120).IsRequired();
            e.Property(x => x.DocumentVersion).HasMaxLength(60);
            e.Property(x => x.AppliesToVersion).HasMaxLength(60);
            e.Property(x => x.ValidatedBy).HasMaxLength(256);
            e.Property(x => x.SourceFileName).HasMaxLength(400);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.IsCitable);
        });

        builder.Entity<DocumentSection>(e =>
        {
            e.ToTable("DocumentSections");
            e.HasIndex(x => new { x.DocumentId, x.Ordinal });

            e.Property(x => x.Heading).HasMaxLength(300);
            e.Property(x => x.Content).HasMaxLength(8000).IsRequired();

            // Le texte normalise est aussi long que le contenu : chaque
            // caractere y est remplace, jamais supprime - c'est ce qui permet
            // de reutiliser une position trouvee dedans pour extraire le
            // passage du texte d'origine, accents compris.
            e.Property(x => x.SearchText).HasMaxLength(8000);

            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.Citation);

            e.HasOne(x => x.Document).WithMany(x => x.Sections)
             .HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<KnowledgeFeedback>(e =>
        {
            e.ToTable("KnowledgeFeedback");
            e.HasIndex(x => x.SectionId);

            e.Property(x => x.Question).HasMaxLength(500).IsRequired();
            e.Property(x => x.Comment).HasMaxLength(1000);
            e.Property(x => x.ReportedBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.ResolvedBy).HasMaxLength(256);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasOne(x => x.Section).WithMany()
             .HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(150);
            e.Property(x => x.Department).HasMaxLength(150);
        });

        AdapterLesJetonsDeConcurrencePourSqlite(builder);
    }

    // =======================================================================
    // Concurrence optimiste sur SQLite
    // =======================================================================

    /// <summary>
    /// SQLite n'a pas de type <c>rowversion</c> : les 31 appels
    /// <c>IsRowVersion()</c> ci-dessus n'y sont pas traduisibles.
    ///
    /// Plutôt que de les dupliquer par fournisseur — trente et une occasions
    /// d'en oublier un, et un oubli ne se voit pas : il supprime
    /// silencieusement la détection d'écriture concurrente sur UNE table —
    /// on les reconfigure en une passe. Le jeton reste un jeton de
    /// concurrence, mais c'est l'application qui l'incrémente, dans
    /// <see cref="EstampillerLesJetons"/>.
    ///
    /// La garantie obtenue est la même pour tout ce qui passe par le suivi de
    /// modifications : deux écritures concurrentes sur la même ligne, la
    /// seconde échoue avec <c>DbUpdateConcurrencyException</c>.
    ///
    /// CE QUI DIFFÈRE, ET QU'IL FAUT SAVOIR : sur SQL Server, le moteur
    /// incrémente le jeton même lors d'un <c>ExecuteUpdate</c>, qui contourne
    /// le suivi de modifications. Ici non. Les deux seuls appels concernés —
    /// la demande d'annulation et l'écriture de progression — sont
    /// précisément ceux qui ont été écrits pour ne PAS entrer en conflit ;
    /// l'écart est donc sans conséquence, mais il cesserait de l'être si l'on
    /// se mettait à utiliser ExecuteUpdate ailleurs.
    /// </summary>
    private void AdapterLesJetonsDeConcurrencePourSqlite(ModelBuilder builder)
    {
        if (!Database.IsSqlite()) return;

        foreach (var entite in builder.Model.GetEntityTypes())
        {
            if (entite.FindProperty(nameof(AuditableEntity.RowVersion)) is null) continue;

            builder.Entity(entite.ClrType)
                .Property(nameof(AuditableEntity.RowVersion))
                .ValueGeneratedNever()
                .IsConcurrencyToken()
                .HasColumnType("BLOB");
        }
    }

    /// <summary>
    /// Donne une nouvelle valeur au jeton de concurrence à chaque écriture,
    /// sur SQLite uniquement. Sur SQL Server, c'est le moteur qui s'en charge
    /// et y toucher fausserait la comparaison.
    /// </summary>
    private void EstampillerLesJetons()
    {
        if (!Database.IsSqlite()) return;

        foreach (var entree in ChangeTracker.Entries())
        {
            if (entree.State is not (EntityState.Added or EntityState.Modified)) continue;

            var jeton = entree.Properties.FirstOrDefault(
                p => p.Metadata.Name == nameof(AuditableEntity.RowVersion));

            if (jeton is null) continue;

            // La valeur d'ORIGINE est laissée intacte : c'est elle que le
            // fournisseur place dans la clause WHERE. La remplacer ici
            // ferait passer une écriture périmée pour valide.
            jeton.CurrentValue = Guid.NewGuid().ToByteArray();
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EstampillerLesJetons();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EstampillerLesJetons();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
