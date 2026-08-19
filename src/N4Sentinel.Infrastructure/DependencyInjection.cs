using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectivity;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Referential;
using N4Sentinel.Infrastructure.Security;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Nom de la chaine de connexion, lue dans appsettings.json.
    ///
    /// A REPRENDRE AVANT LE DEPLOIEMENT EN UAT ET EN PRODUCTION : le cahier
    /// des charges demande que les secrets soient references dans un coffre et
    /// jamais integres au code (SEC-003), et que la configuration
    /// d'installation soit livree sans secret en clair. La configuration par
    /// defaut d'ASP.NET Core permet de surcharger cette valeur sans toucher au
    /// fichier, par variable d'environnement :
    ///     ConnectionStrings__N4Sentinel=...
    /// C'est le chemin a retenir sur les serveurs, ou un compte de service
    /// Windows qui supprime purement et simplement le mot de passe.
    /// </summary>
    public const string ConnectionStringName = "N4Sentinel";

    public static IServiceCollection AddN4SentinelInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Chaine de connexion '{ConnectionStringName}' introuvable dans appsettings.json.");

        // Message explicite plutot qu'un echec de connexion SQL obscur : le
        // gabarit livre contient un mot de passe a remplacer.
        if (connectionString.Contains("MOT_DE_PASSE_A_DEFINIR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Le mot de passe de la chaine de connexion est encore le gabarit livre. " +
                "Executez db/01_activer_connexion_sql.sql puis remplacez MOT_DE_PASSE_A_DEFINIR " +
                "dans appsettings.json. Tant que l'instance SQL Server est en mode " +
                "d'authentification Windows uniquement, utilisez la chaine de repli " +
                "'N4Sentinel_AuthWindows' fournie dans le meme fichier.");

        // Sans utilisateur connecte - taches de fond, migrations - l'acteur
        // est "systeme". La couche Web substitue une implementation adossee
        // au HttpContext.
        services.AddScoped<ICurrentUserContext, SystemUserContext>();
        services.AddScoped<AuditingInterceptor>();
        services.AddScoped<IAuditWriter, AuditWriter>();

        // Comptes et rôles (FR-091, SEC-002) — cf. N4SignInManager (Program.cs)
        // pour l'application effective de ApplicationUser.IsDisabled.
        services.AddScoped<Identity.UserAdministrationService>();

        // Fabrique plutot que contexte injecte directement : dans Blazor Server,
        // un composant peut declencher plusieurs operations concurrentes sur le
        // meme circuit, ce qu'un DbContext partage ne supporte pas. Chaque ecran
        // ouvre son propre contexte, le temps de sa requete.
        // Fabrique enregistree a PORTEE DE REQUETE, et non en singleton comme
        // par defaut : l'intercepteur d'audit doit connaitre l'utilisateur
        // courant, ce qu'un service singleton ne peut pas resoudre.
        services.AddDbContextFactory<N4SentinelDbContext>((sp, options) =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorNumbersToAdd: null);
                sql.CommandTimeout(60);
            });
            options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
            options.AddInterceptors(sp.GetRequiredService<AuditingInterceptor>());
        }, lifetime: ServiceLifetime.Scoped);

        // Identity attend un contexte a portee de requete : on le derive de la
        // fabrique, de sorte qu'il n'existe qu'une seule configuration.
        services.AddScoped<N4SentinelDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>().CreateDbContext());

        services.AddScoped<DatabaseSeeder>();
        services.AddScoped<DemonstrationSeeder>();
        services.AddScoped<ReferentialService>();
        services.AddScoped<ConnectivityTester>();
        services.AddScoped<NavisConfigImporter>();

        // Connecteur technique (sprint 2). Sans etat, donc partageable :
        // chaque appel ouvre et referme sa propre session.
        services.AddSingleton<IN4Connector, PowerShellConnector>();

        // Magasin de secrets et fabrique de cibles : tout acces a un serveur
        // passe par la fabrique, qui n'accepte que des serveurs declares au
        // referentiel.
        services.AddScoped<CredentialStore>();

        // Cloisonnement des environnements (SEC-004, audit SEC-A1). Le role dit
        // ce qu'on sait faire, l'habilitation dit ou on a le droit de le faire.
        services.AddScoped<Security.EnvironmentAccessService>();
        services.AddScoped<ConnectorTargetFactory>();
        services.AddScoped<ServerProbe>();
        services.AddScoped<ReadinessDiscovery>();
        services.AddScoped<CommissioningStatus>();

        // Supervision : moteur de santé, cache temps réel, service d'arrière-plan.
        // Les alertes sont dérivées du même instantané que l'état affiché, dans
        // la même passe : elles ne peuvent donc jamais le contredire.
        services.AddScoped<SupervisionService>();
        services.AddScoped<AlertService>();
        services.AddScoped<UndeclaredComponentScanner>();
        services.AddScoped<DatabaseHealthService>();
        services.AddScoped<SharedFolderHealthService>();
        services.AddScoped<Edi.EdiTrackingService>();

        // Vitalité des nœuds. Le service est à portée de requête — il dépend du
        // référentiel — mais le cache des mises à jour Windows est un singleton :
        // une interrogation de l'agent coûte de trente secondes à deux minutes,
        // il est hors de question de la refaire à chaque affichage d'écran.
        services.AddScoped<NodeVitalsService>();
        services.AddSingleton<UpdateReadingCache>();
        services.AddSingleton<SupervisionStateCache>();
        services.AddScoped<Security.AzureAdSettingsService>();
        services.AddScoped<AzureAdAuthProvider>();

        // Sentinel Intelligence Suite V2.5
        services.AddSingleton<Procedures.FlightSimulatorService>();
        services.AddSingleton<Ai.VoiceCopilotService>();

        services.AddHostedService<SupervisionBackgroundService>();

        // Orchestration (sprint 4). Le moteur est un SINGLETON : il pilote des
        // opérations qui durent bien plus longtemps qu'une requête HTTP, et doit
        // survivre à la fermeture de l'écran qui les a lancées. Il ouvre ses
        // propres portées pour tout ce qui touche la base.
        services.AddScoped<WorkflowService>();
        services.AddScoped<ExecutionService>();
        services.AddScoped<Orchestration.UseCases.PrepareExecutionUseCase>();
        services.AddScoped<Orchestration.UseCases.ApproveExecutionUseCase>();
        services.AddScoped<Orchestration.UseCases.ControlExecutionUseCase>();
        services.AddScoped<EnvironmentLockService>();
        services.AddScoped<SequenceValidator>();
        services.AddScoped<StepExecutor>();
        services.AddScoped<CenterContinuityService>();
        services.AddScoped<PreflightService>();
        services.AddScoped<AdHocOperationService>();
        services.AddScoped<ExecutionReportService>();
        services.AddScoped<ApprovalMatrixService>();
        services.AddSingleton<OrchestrationEngine>();
        services.AddHostedService<OrchestrationBackgroundService>();

        // Diagnostic (sprint 6). Le masquage des secrets est statique et sans
        // état : il n'a rien à enregistrer ici, mais tout ce qui écrit un
        // fragment de journal doit passer par lui.
        services.AddScoped<Diagnostic.SignatureCatalogue>();
        services.AddScoped<Diagnostic.LogAnalysisService>();
        services.AddScoped<Diagnostic.DiagnosticSessionService>();
        services.AddScoped<Diagnostic.DiagnosticSettingsService>();

        // Base documentaire et historique (sprint 7). La base documentaire
        // conseille et cite ; elle ne déclenche rien. Aucun de ces services
        // n'a de dépendance vers l'orchestrateur, et c'est volontaire.
        services.AddScoped<Knowledge.KnowledgeService>();
        services.AddScoped<Reporting.HistoryService>();

        // Export PDF/Word (FR-093) — sans etat, partageable.
        services.AddSingleton<Reporting.ReportDocumentService>();

        // Rapport d'incident automatique (FR-096).
        services.AddScoped<Reporting.IncidentReportService>();

        // Notifications (FR-095). INotificationSender est fourni par le projet
        // Web (SmtpEmailSender) — voir Program.cs.
        services.AddScoped<Notifications.OperationNotificationService>();

        // SOP — procédures opérationnelles standard (Phase C, Lot 2a).
        // Même principe que la base documentaire : ces services documentent et
        // tracent un geste humain, ils n'automatisent rien et ne dépendent
        // donc pas de l'orchestrateur.
        services.AddScoped<Procedures.SopService>();
        services.AddScoped<Procedures.SopExecutionService>();

        // Exploitation (sprint 8). La sauvegarde couvre la base ET le trousseau
        // de clés : la base seule redonnerait des comptes techniques dont les
        // secrets sont illisibles.
        services.AddScoped<Operations.BackupService>();

        // Métriques d'exploitation (NFR-008), au-delà des logs. Singleton :
        // les compteurs vivent en mémoire pour toute la durée du processus.
        services.AddSingleton<Observability.MetricsService>();

        // Rétention (FR-079, SEC-009). L'audit n'y est jamais soumis - voir
        // RetentionPolicy. La purge s'applique désormais automatiquement,
        // pas seulement quand un administrateur clique dessus.
        services.AddScoped<Retention.RetentionPolicyService>();
        services.AddHostedService<Retention.RetentionPurgeBackgroundService>();

        // Scan antivirus au versement de documents (SEC-007). Sans etat,
        // partageable.
        services.AddSingleton<Security.DocumentAntivirusScanner>();

        return services;
    }
}
