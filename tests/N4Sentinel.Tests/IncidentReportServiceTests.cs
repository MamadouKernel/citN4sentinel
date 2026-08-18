using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Diagnostic;
using N4Sentinel.Infrastructure.Knowledge;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Procedures;
using N4Sentinel.Infrastructure.Reporting;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// FR-096 : le rapport d'incident doit reproduire CE QUI A ÉTÉ ENREGISTRÉ —
/// chronologie réelle des phases (§3.10.1), preuves, cause retenue, SOP
/// effectivement associé — jamais un contenu recomposé ou supposé.
/// </summary>
public sealed class IncidentReportServiceTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _keyPath = string.Empty;
    private TestDbContextFactory _factory = null!;
    private DiagnosticSessionService _sessions = null!;
    private SopExecutionService _sopExecutions = null!;
    private SopService _sop = null!;
    private IncidentReportService _report = null!;

    private Guid _envId;
    private Guid _composantId;

    public async Task InitializeAsync()
    {
        var cs = $"Server=localhost;Database={_databaseName};Trusted_Connection=True;"
               + "TrustServerCertificate=True;MultipleActiveResultSets=True";

        _factory = new TestDbContextFactory(TestDbContextOptions.Builder(cs).Options);

        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();

            var env = new N4Environment { Code = "UAT", Name = "Recette", Kind = EnvironmentKind.UAT };
            db.Environments.Add(env);
            await db.SaveChangesAsync();
            _envId = env.Id;

            var composant = new N4Component
            {
                EnvironmentId = _envId,
                LogicalName = "Center Node",
                Role = ComponentRole.CenterNode,
                WindowsServiceName = "Navis N4 Center Node",
                ControlMode = ControlMode.Pilotable,
                Status = LifecycleStatus.Valide
            };
            db.Components.Add(composant);
            await db.SaveChangesAsync();
            _composantId = composant.Id;
        }

        _keyPath = Path.Combine(Path.GetTempPath(), $"n4-cles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keyPath);

        var store = new CredentialStore(_factory,
            DataProtectionProvider.Create(new DirectoryInfo(_keyPath)),
            NullLogger<CredentialStore>.Instance);

        var catalogue = new SignatureCatalogue(_factory, NullLogger<SignatureCatalogue>.Instance);
        var analyse = new LogAnalysisService(
            _factory,
            new ConnectorTargetFactory(_factory, store, NullLogger<ConnectorTargetFactory>.Instance),
            new ConnecteurMuet(),
            catalogue,
            new N4Sentinel.Infrastructure.Observability.MetricsService(),
            NullLogger<LogAnalysisService>.Instance);

        _sessions = new DiagnosticSessionService(_factory, analyse, new AuditWriter(_factory));

        var knowledge = new KnowledgeService(_factory, NullLogger<KnowledgeService>.Instance, new AuditWriter(_factory));
        _sop = new SopService(_factory, knowledge, NullLogger<SopService>.Instance, new AuditWriter(_factory));
        _sopExecutions = new SopExecutionService(_factory, new AuditWriter(_factory), NullLogger<SopExecutionService>.Instance);

        _report = new IncidentReportService(_factory);
    }

    private async Task<Guid> CreerSessionAsync() =>
        await _sessions.CreateAsync(_envId, "Center Node injoignable",
            "Le Center Node ne répond plus depuis 09 h.", "INC-2026-0816", "m.konate", null, null);

    [Fact]
    public async Task BuildMarkdownAsync_Sur_Session_Inconnue_Renvoie_Null()
    {
        Assert.Null(await _report.BuildMarkdownAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task BuildMarkdownAsync_Restitue_La_Chronologie_Reelle_Des_Phases()
    {
        var sessionId = await CreerSessionAsync();

        await _sessions.AdvancePhaseAsync(sessionId, DiagnosticPhase.QualificationEtCollecte, "m.konate", null);
        await _sessions.AdvancePhaseAsync(sessionId, DiagnosticPhase.DiagnosticEtCorrelation, "m.konate",
            "Corrélation lancée sur les journaux Center/Bridge.");
        var erreurCloture = await _sessions.AdvancePhaseAsync(sessionId, DiagnosticPhase.ClotureEtCapitalisation,
            "m.konate", "Service redémarré, heartbeat confirmé stable depuis 15 min.");
        Assert.Null(erreurCloture);

        var markdown = await _report.BuildMarkdownAsync(sessionId);

        Assert.NotNull(markdown);
        Assert.Contains("Center Node injoignable", markdown);
        Assert.Contains("Incident clôturé", markdown);
        Assert.Contains("Qualification et collecte", markdown);
        Assert.Contains("Diagnostic et corrélation", markdown);
        Assert.Contains("Corrélation lancée sur les journaux", markdown);
        Assert.Contains("Durée totale", markdown);
        Assert.Contains("Service redémarré, heartbeat confirmé", markdown);
    }

    [Fact]
    public async Task BuildMarkdownAsync_Sans_Cloture_Le_Dit_Explicitement()
    {
        var sessionId = await CreerSessionAsync();
        await _sessions.AdvancePhaseAsync(sessionId, DiagnosticPhase.QualificationEtCollecte, "m.konate", null);

        var markdown = await _report.BuildMarkdownAsync(sessionId);

        Assert.NotNull(markdown);
        Assert.Contains("Non clôturé", markdown);
        Assert.DoesNotContain("Durée totale", markdown);
    }

    [Fact]
    public async Task BuildMarkdownAsync_Cite_Le_Sop_Reellement_Associe_A_La_Session()
    {
        var sessionId = await CreerSessionAsync();

        var creation = await _sop.CreateAsync(new Domain.Sop
        {
            EnvironmentId = _envId,
            Code = "REDEMARRAGE-CENTER",
            Title = "Redémarrage guidé du Center Node"
        });

        await _sop.SaveAsync(creation, "Redémarrage guidé du Center Node",
            new SopTemplateFields { Objective = "Redémarrer le Center Node en toute sécurité." },
            [new SopStep { Order = 1, Title = "Confirmer l'arrêt", Instruction = "Vérifier que le service est arrêté." }]);

        await _sop.ChangeStatusAsync(creation, LifecycleStatus.Valide, "m.konate");

        var demarrage = await _sopExecutions.StartAsync(
            creation, "m.konate", "Résolution de l'incident", "INC-2026-0816",
            callerHasElevatedRole: true, sourceDiagnosticSessionId: sessionId);

        Assert.True(demarrage.Succeeded, demarrage.Error);

        var markdown = await _report.BuildMarkdownAsync(sessionId);

        Assert.NotNull(markdown);
        Assert.Contains("SOP associé", markdown);
        Assert.Contains("Redémarrage guidé du Center Node", markdown);
        Assert.Contains("m.konate", markdown);
    }

    [Fact]
    public async Task BuildMarkdownAsync_Sans_Constat_Ni_Hypothese_Le_Dit_Honnetement()
    {
        var sessionId = await CreerSessionAsync();

        var markdown = await _report.BuildMarkdownAsync(sessionId);

        Assert.NotNull(markdown);
        Assert.Contains("Aucun constat enregistré", markdown);
        Assert.Contains("Aucun composant identifié", markdown);
    }

    /// <summary>
    /// L'écran affiche déjà "Conduite à tenir" (constat) et "À vérifier"
    /// (hypothèse) pendant l'investigation — le rapport exporté doit
    /// restituer cette même conduite à tenir, pas seulement la cause.
    /// </summary>
    [Fact]
    public async Task BuildMarkdownAsync_Restitue_Les_Recommandations_Pour_Eviter_La_Recurrence()
    {
        var sessionId = await CreerSessionAsync();

        await using (var db = _factory.CreateDbContext())
        {
            db.Signatures.Add(new DiagnosticSignature
            {
                Code = "DB-CONN-REFUSED-TEST",
                Name = "Connexion base refusée",
                Pattern = @"Connection refused",
                Domain = DiagnosticDomain.BaseDeDonnees,
                Remediation = "Vérifier que le service SQL Server est démarré et que le port 1433 est joignable."
            });
            await db.SaveChangesAsync();
        }

        var analyse = new LogAnalysisService(
            _factory,
            new ConnectorTargetFactory(_factory, new CredentialStore(_factory,
                DataProtectionProvider.Create(new DirectoryInfo(_keyPath)), NullLogger<CredentialStore>.Instance),
                NullLogger<ConnectorTargetFactory>.Instance),
            new ConnecteurMuet(),
            new SignatureCatalogue(_factory, NullLogger<SignatureCatalogue>.Instance),
            new N4Sentinel.Infrastructure.Observability.MetricsService(),
            NullLogger<LogAnalysisService>.Instance);

        var import = await analyse.ImportAsync(sessionId, "navis-apex.log",
            "2026-08-16 09:00:00 ERROR [main] Connection refused: could not establish connection to database host");
        Assert.True(import.Succeeded, import.Error);

        await analyse.ConcludeAsync(sessionId);

        var markdown = await _report.BuildMarkdownAsync(sessionId);

        Assert.NotNull(markdown);
        Assert.Contains("Recommandations pour éviter la récurrence", markdown);
        Assert.Contains("Vérifier que le service SQL Server est démarré", markdown);
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_keyPath)) try { Directory.Delete(_keyPath, true); } catch { }

        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var master = new Microsoft.Data.SqlClient.SqlConnection(MasterConnection);
        await master.OpenAsync();
        var cmd = master.CreateCommand();
        cmd.CommandText =
            $"IF DB_ID('{_databaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{_databaseName}]; END";
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }

    private sealed class ConnecteurMuet : IN4Connector
    {
        private static ConnectorResult<T> Injoignable<T>() =>
            ConnectorResult<T>.Fail(ConnectorFailure.Injoignable, "Aucun serveur dans ce test.", TimeSpan.Zero);

        public Task<ConnectorResult<string>> PingAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<string>());

        public Task<ConnectorResult<ServiceSnapshot>> GetServiceAsync(ConnectorTarget t, string n, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<ServiceSnapshot>());

        public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> GetServicesAsync(ConnectorTarget t, IReadOnlyCollection<string> n, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<IReadOnlyList<ServiceSnapshot>>());

        public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> ListServicesAsync(ConnectorTarget t, IReadOnlyCollection<string> m, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<IReadOnlyList<ServiceSnapshot>>());

        public Task<ConnectorResult<SystemSnapshot>> GetSystemAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<SystemSnapshot>());

        public Task<ConnectorResult<LogDelta>> ReadLogDeltaAsync(ConnectorTarget t, string p, long o, int m = 262144, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<LogDelta>());

        public Task<ConnectorResult<LogFileInfo>> ResolveLogAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<LogFileInfo>());

        public Task<ConnectorResult<ServiceSnapshot>> ControlServiceAsync(ConnectorTarget t, string n, ServiceControlAction a, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<ServiceSnapshot>());

        public Task<ConnectorResult<LiveMetrics>> GetLiveMetricsAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<LiveMetrics>());

        public Task<ConnectorResult<TimeSyncSnapshot>> GetTimeSyncAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<TimeSyncSnapshot>());

        public Task<ConnectorResult<UpdateSnapshot>> GetPendingUpdatesAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<UpdateSnapshot>());

        public Task<ConnectorResult<FolderSnapshot>> ListFilesAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<FolderSnapshot>());

        public Task<ConnectorResult<WriteProbeResult>> ProbeWriteAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<WriteProbeResult>());
    }
}
