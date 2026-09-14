using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Ai;
using N4Sentinel.Infrastructure.Apr;
using N4Sentinel.Infrastructure.Capitalisation;
using N4Sentinel.Infrastructure.Connectivity;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Diagnostic;
using N4Sentinel.Infrastructure.Edi;
using N4Sentinel.Infrastructure.Identity;
using N4Sentinel.Infrastructure.Knowledge;
using N4Sentinel.Infrastructure.Notifications;
using N4Sentinel.Infrastructure.Observability;
using N4Sentinel.Infrastructure.Operations;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Orchestration.UseCases;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Procedures;
using N4Sentinel.Infrastructure.Referential;
using N4Sentinel.Infrastructure.Reporting;
using N4Sentinel.Infrastructure.Retention;
using N4Sentinel.Infrastructure.Scheduling;
using N4Sentinel.Infrastructure.Security;
using N4Sentinel.Infrastructure.Supervision;
using N4Sentinel.Infrastructure.Ticketing;
using N4Sentinel.Web.Components.Pages;
using System.Security.Claims;

namespace N4Sentinel.Tests.UI;

public class HistoryComponentsTests : BunitContext
{
    public HistoryComponentsTests()
    {
        var dbName = Guid.NewGuid().ToString();
        Services.AddDbContextFactory<N4SentinelDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        Services.AddDataProtection();

        var factory = Services.BuildServiceProvider();
        var dbFactory = factory.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>();
        var dataProtection = factory.GetRequiredService<IDataProtectionProvider>();
        var scopeFactory = factory.GetRequiredService<IServiceScopeFactory>();

        var auditWriterMock = new Mock<IAuditWriter>();
        var connectorMock = new Mock<IN4Connector>();
        var httpFactoryMock = new Mock<IHttpClientFactory>();

        Services.AddSingleton(auditWriterMock.Object);
        Services.AddSingleton(connectorMock.Object);
        Services.AddSingleton(httpFactoryMock.Object);
        var metrics = new MetricsService();
        var readingCache = new UpdateReadingCache();
        var supervisionCache = new SupervisionStateCache();
        Services.AddSingleton(metrics);
        Services.AddSingleton(readingCache);
        Services.AddSingleton(supervisionCache);

        var credentialStore = new CredentialStore(dbFactory, dataProtection, NullLogger<CredentialStore>.Instance);
        Services.AddSingleton(credentialStore);

        var connectorTargetFactory = new ConnectorTargetFactory(dbFactory, credentialStore, NullLogger<ConnectorTargetFactory>.Instance);
        Services.AddSingleton(connectorTargetFactory);

        var supervisionService = new SupervisionService(dbFactory, connectorTargetFactory, connectorMock.Object, NullLogger<SupervisionService>.Instance);
        Services.AddSingleton(supervisionService);
        Services.AddSingleton<AlertService>(_ => new AlertService(dbFactory, NullLogger<AlertService>.Instance));

        var signatureCatalogue = new SignatureCatalogue(dbFactory, NullLogger<SignatureCatalogue>.Instance);
        var logAnalysisService = new LogAnalysisService(dbFactory, connectorTargetFactory, connectorMock.Object, signatureCatalogue, metrics, NullLogger<LogAnalysisService>.Instance);
        Services.AddSingleton(signatureCatalogue);
        Services.AddSingleton(logAnalysisService);

        var diagnosticSessionService = new DiagnosticSessionService(dbFactory, logAnalysisService, auditWriterMock.Object);
        var antivirus = new DocumentAntivirusScanner(NullLogger<DocumentAntivirusScanner>.Instance);
        Services.AddSingleton(diagnosticSessionService);
        Services.AddSingleton<DiagnosticSettingsService>();
        Services.AddSingleton<UndeclaredComponentScanner>();
        Services.AddSingleton<DatabaseHealthService>();
        Services.AddSingleton<SharedFolderHealthService>();
        Services.AddSingleton(antivirus);

        var ediTrackingService = new EdiTrackingService(dbFactory, connectorTargetFactory, connectorMock.Object, NullLogger<EdiTrackingService>.Instance);
        Services.AddSingleton(ediTrackingService);

        var nodeVitals = new NodeVitalsService(dbFactory, connectorTargetFactory, connectorMock.Object, readingCache, NullLogger<NodeVitalsService>.Instance);
        Services.AddSingleton(nodeVitals);

        Services.AddSingleton<SlaService>();
        Services.AddSingleton<OperatorCredentialService>();
        var envAccess = new EnvironmentAccessService(dbFactory, new ConfigurationBuilder().Build(), NullLogger<EnvironmentAccessService>.Instance);
        Services.AddSingleton(envAccess);
        Services.AddSingleton<ServerProbe>();
        Services.AddSingleton<ReadinessDiscovery>();
        Services.AddSingleton<CommissioningStatus>();
        Services.AddSingleton(new AzureAdSettingsService(dbFactory, auditWriterMock.Object));
        Services.AddSingleton<EnvironmentDuplicationService>();
        Services.AddSingleton<ReferentialCsvImporter>();
        var merise = new MeriseBusinessPolicyService(dbFactory);
        var palier2 = new Palier2ApprovalService(dbFactory, auditWriterMock.Object);
        Services.AddSingleton(merise);
        Services.AddSingleton(palier2);
        var continuity = new CenterContinuityService(dbFactory, supervisionService);
        Services.AddSingleton(continuity);
        var seqValidator = new SequenceValidator(dbFactory, merise);
        Services.AddSingleton(seqValidator);
        var envLock = new EnvironmentLockService(dbFactory, NullLogger<EnvironmentLockService>.Instance);
        Services.AddSingleton(envLock);
        var matrixApproval = new ApprovalMatrixService(dbFactory);
        var adHoc = new AdHocOperationService(dbFactory, NullLogger<AdHocOperationService>.Instance);
        Services.AddSingleton<ExecutionReportService>();
        Services.AddSingleton(matrixApproval);
        Services.AddSingleton(adHoc);

        var engine = new OrchestrationEngine(scopeFactory, metrics, NullLogger<OrchestrationEngine>.Instance);
        Services.AddSingleton(engine);

        var preflight = new PreflightService(dbFactory, connectorTargetFactory, connectorMock.Object,
            envLock, seqValidator, supervisionService, continuity, envAccess,
            supervisionCache, merise, palier2, NullLogger<PreflightService>.Instance);
        Services.AddSingleton(preflight);
        Services.AddSingleton<WorkflowService>();

        var stepExecutor = new StepExecutor(dbFactory, connectorTargetFactory, connectorMock.Object, supervisionService, credentialStore, NullLogger<StepExecutor>.Instance);
        Services.AddSingleton(stepExecutor);

        var ticketingOptions = Microsoft.Extensions.Options.Options.Create(new TicketingExternalOptions { Enabled = false });
        var notificationsOptions = Microsoft.Extensions.Options.Options.Create(new NotificationChannelsOptions());
        var resilientNotifier = new ResilientMultiChannelNotifier(Enumerable.Empty<INotificationSender>(), httpFactoryMock.Object, notificationsOptions, NullLogger<ResilientMultiChannelNotifier>.Instance);
        Services.AddSingleton(ticketingOptions);
        Services.AddSingleton(notificationsOptions);
        Services.AddSingleton(resilientNotifier);

        var userManagerMock = new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(), null, null, null, null, null, null, null, null);
        var notificationOpService = new OperationNotificationService(resilientNotifier, userManagerMock.Object, NullLogger<OperationNotificationService>.Instance);
        Services.AddSingleton(notificationOpService);

        var prepare = new PrepareExecutionUseCase(dbFactory, NullLogger<PrepareExecutionUseCase>.Instance, matrixApproval);
        var approve = new ApproveExecutionUseCase(dbFactory, merise);
        var control = new ControlExecutionUseCase(dbFactory, envLock, auditWriterMock.Object, NullLogger<ControlExecutionUseCase>.Instance,
            merise, engine, notificationOpService, matrixApproval, supervisionService, credentialStore, palier2);
        var execution = new ExecutionService(dbFactory, envLock, auditWriterMock.Object,
            NullLogger<ExecutionService>.Instance, engine, notificationOpService, stepExecutor, antivirus, diagnosticSessionService,
            matrixApproval, supervisionService, adHoc, prepare, approve, control);
        Services.AddSingleton(prepare);
        Services.AddSingleton(approve);
        Services.AddSingleton(control);
        Services.AddSingleton(execution);

        Services.AddSingleton(_ => new AprDetectionService(dbFactory, supervisionCache));
        Services.AddSingleton<AprQueryService>();
        Services.AddSingleton<AprScheduleUseCase>();
        Services.AddSingleton(_ => new AprLaunchViaWorkflowUseCase(dbFactory, prepare));
        Services.AddSingleton<AprDismissUseCase>();
        Services.AddSingleton(_ => new KnowledgeService(dbFactory, NullLogger<KnowledgeService>.Instance, auditWriterMock.Object));
        Services.AddSingleton<SopService>(sp => new SopService(dbFactory, sp.GetRequiredService<KnowledgeService>(), NullLogger<SopService>.Instance, auditWriterMock.Object));
        Services.AddSingleton<SopExecutionService>(_ => new SopExecutionService(dbFactory, auditWriterMock.Object, NullLogger<SopExecutionService>.Instance));
        Services.AddSingleton<ReferentialService>(_ => new ReferentialService(dbFactory, merise, NullLogger<ReferentialService>.Instance));
        Services.AddSingleton<HistoryService>();
        Services.AddSingleton<ReportDocumentService>();
        Services.AddSingleton<IncidentReportService>();
        Services.AddSingleton<RetentionPolicyService>(_ => new RetentionPolicyService(dbFactory, auditWriterMock.Object, NullLogger<RetentionPolicyService>.Instance));
        Services.AddSingleton<MaintenanceScheduleService>(_ => new MaintenanceScheduleService(dbFactory, auditWriterMock.Object));
        Services.AddSingleton<MaintenanceCalendarService>();
        Services.AddSingleton<DemonstrationSeeder>();
        Services.AddSingleton<NavisConfigImporter>();
        Services.AddSingleton<ConnectivityTester>();
        Services.AddSingleton<FlightSimulatorService>();
        Services.AddSingleton<BackupService>(_ => new BackupService(dbFactory, new ConfigurationBuilder().Build(), NullLogger<BackupService>.Instance));
        Services.AddSingleton<IncidentCapitalizationService>();
        Services.AddSingleton<BusinessMetricsService>();
        Services.AddSingleton(_ => new VoiceCopilotService(supervisionCache, dbFactory, NullLogger<VoiceCopilotService>.Instance));

        Services.AddSingleton(_ => new TicketingService(dbFactory, auditWriterMock.Object));
        Services.AddSingleton(_ => new TicketingSyncService(dbFactory, httpFactoryMock.Object, auditWriterMock.Object, ticketingOptions, NullLogger<TicketingSyncService>.Instance));
        Services.AddSingleton<Domain.Interfaces.IJmxConnector>(_ => new JolokiaJmxConnector(dbFactory, credentialStore, httpFactoryMock.Object, auditWriterMock.Object, NullLogger<JolokiaJmxConnector>.Instance));

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "TestUser"),
            new Claim(ClaimTypes.Role, "Consultant")
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        var authMock = new Mock<AuthenticationStateProvider>();
        authMock.Setup(p => p.GetAuthenticationStateAsync()).ReturnsAsync(authState);
        Services.AddSingleton(authMock.Object);

        Services.AddAuthorizationCore(options =>
        {
            options.AddPolicy(N4Policies.PeutConsulter, p => p.RequireAssertion(_ => true));
            options.AddPolicy(N4Policies.PeutDiagnostiquer, p => p.RequireAssertion(_ => true));
            options.AddPolicy(N4Policies.PeutExecuter, p => p.RequireAssertion(_ => true));
            options.AddPolicy(N4Policies.PeutExecuterActionsSensibles, p => p.RequireAssertion(_ => true));
            options.AddPolicy(N4Policies.PeutExecuterActionUnitaire, p => p.RequireAssertion(_ => true));
            options.AddPolicy(N4Policies.PeutApprouver, p => p.RequireAssertion(_ => true));
            options.AddPolicy(N4Policies.PeutAdministrerReferentiel, p => p.RequireAssertion(_ => true));
            options.AddPolicy(N4Policies.PeutAdministrerConnecteurs, p => p.RequireAssertion(_ => true));
            options.AddPolicy(N4Policies.PeutAuditer, p => p.RequireAssertion(_ => true));
        });

        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void L_Historique_S_Affiche_Et_Dit_Que_L_Absence_D_Evenement_Ne_Prouve_Rien()
    {
        var cut = Render<Historique>();
        Assert.Contains("Historique", cut.Markup);
        Assert.Contains("Aucun événement enregistré", cut.Markup);
        Assert.Contains("hors de", cut.Markup);
    }
}
