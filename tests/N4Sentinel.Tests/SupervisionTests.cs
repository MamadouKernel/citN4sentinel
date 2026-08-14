using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectivity;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;
using N4Sentinel.Infrastructure.Supervision;
using Xunit;

namespace N4Sentinel.Tests;

public sealed class SupervisionTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _connectionString = string.Empty;
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _connectionString =
            $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

        var options = new DbContextOptionsBuilder<N4SentinelDbContext>()
            .UseSqlServer(_connectionString)
            .Options;

        _factory = new TestDbContextFactory(options);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
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

    [Fact(DisplayName = "SupervisionStateCache met a jour les instantanes et emet les evenements")]
    public void SupervisionStateCache_Updates_And_Emits_Events()
    {
        // Arrange
        var cache = new SupervisionStateCache();
        var eventFired = false;
        cache.OnStateChanged += () => eventFired = true;

        var compId = Guid.NewGuid();
        var snap = new ComponentHealthSnapshot
        {
            ComponentId = compId,
            LogicalName = "Center Node",
            EnvironmentCode = "PROD",
            State = ComponentState.Disponible
        };

        // Act
        cache.UpdateSnapshot(snap);

        // Assert
        Assert.True(eventFired);
        var retrieved = cache.GetSnapshot(compId);
        Assert.NotNull(retrieved);
        Assert.Equal("Center Node", retrieved!.LogicalName);
        Assert.Equal(ComponentState.Disponible, retrieved.State);

        var summary = cache.GetSummary("PROD");
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Disponible);
    }

    [Fact(DisplayName = "EvaluateComponentAsync retourne Arret quand le service est Stopped")]
    public async Task EvaluateComponentAsync_Returns_Arret_When_Service_Is_Stopped()
    {
        // Arrange
        var envId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var compId = Guid.NewGuid();

        await using (var db = _factory.CreateDbContext())
        {
            db.Environments.Add(new N4Environment { Id = envId, Code = "PROD", Name = "Production" });
            db.Servers.Add(new N4Server { Id = serverId, EnvironmentId = envId, HostName = "N4SRV01" });
            db.Components.Add(new N4Component
            {
                Id = compId,
                EnvironmentId = envId,
                ServerId = serverId,
                LogicalName = "Center Node",
                WindowsServiceName = "NavisCenterService",
                ControlMode = ControlMode.Pilotable
            });
            await db.SaveChangesAsync();
        }

        var fakeConnector = new FakeN4Connector
        {
            ServiceStatusToReturn = "Stopped"
        };

        var targetFactory = CreateTargetFactory(_factory);
        var service = new SupervisionService(_factory, targetFactory, fakeConnector, NullLogger<SupervisionService>.Instance);

        // Act
        var snapshot = await service.EvaluateComponentAsync(compId);

        // Assert
        Assert.Equal(ComponentState.Arret, snapshot.State);
        Assert.Equal("Stopped", snapshot.ServiceStatus);
    }

    [Fact(DisplayName = "EvaluateComponentAsync retourne Disponible quand le service tourne et que la preuve log est confirmee")]
    public async Task EvaluateComponentAsync_Returns_Disponible_When_Service_Running_And_Log_Proof_Confirmed()
    {
        // Arrange
        var envId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var compId = Guid.NewGuid();

        await using (var db = _factory.CreateDbContext())
        {
            db.Environments.Add(new N4Environment { Id = envId, Code = "PROD", Name = "Production" });
            db.Servers.Add(new N4Server { Id = serverId, EnvironmentId = envId, HostName = "N4SRV01" });
            db.Components.Add(new N4Component
            {
                Id = compId,
                EnvironmentId = envId,
                ServerId = serverId,
                LogicalName = "Center Node",
                WindowsServiceName = "NavisCenterService",
                ControlMode = ControlMode.Pilotable,
                Readiness = new ReadinessProfile
                {
                    LogPath = "C:\\Navis\\logs\\apex.log",
                    ReadyPatterns = ["Web tier servlet 'action' initialized"]
                }
            });
            await db.SaveChangesAsync();
        }

        var fakeConnector = new FakeN4Connector
        {
            ServiceStatusToReturn = "Running",
            LogTextToReturn = "2026-08-13 14:00:00 INFO [c.n.apex.WebTier] Web tier servlet 'action' initialized"
        };

        var targetFactory = CreateTargetFactory(_factory);
        var service = new SupervisionService(_factory, targetFactory, fakeConnector, NullLogger<SupervisionService>.Instance);

        // Act
        var snapshot = await service.EvaluateComponentAsync(compId);

        // Assert
        Assert.Equal(ComponentState.Disponible, snapshot.State);
        Assert.Equal(LogProofState.Proved, snapshot.LogProofStatus);
        Assert.Equal("Web tier servlet 'action' initialized", snapshot.MatchedPattern);
    }

    [Fact(DisplayName = "EvaluateComponentAsync retourne Demarrage quand le service tourne mais que le marqueur est en attente")]
    public async Task EvaluateComponentAsync_Returns_Demarrage_When_Service_Running_But_Log_Proof_Pending()
    {
        // Arrange
        var envId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var compId = Guid.NewGuid();

        await using (var db = _factory.CreateDbContext())
        {
            db.Environments.Add(new N4Environment { Id = envId, Code = "PROD", Name = "Production" });
            db.Servers.Add(new N4Server { Id = serverId, EnvironmentId = envId, HostName = "N4SRV01" });
            db.Components.Add(new N4Component
            {
                Id = compId,
                EnvironmentId = envId,
                ServerId = serverId,
                LogicalName = "Center Node",
                WindowsServiceName = "NavisCenterService",
                ControlMode = ControlMode.Pilotable,
                Readiness = new ReadinessProfile
                {
                    LogPath = "C:\\Navis\\logs\\apex.log",
                    ReadyPatterns = ["Web tier servlet 'action' initialized"]
                }
            });
            await db.SaveChangesAsync();
        }

        var fakeConnector = new FakeN4Connector
        {
            ServiceStatusToReturn = "Running",
            LogTextToReturn = "2026-08-13 14:00:00 INFO Loading Spring context..."
        };

        var targetFactory = CreateTargetFactory(_factory);
        var service = new SupervisionService(_factory, targetFactory, fakeConnector, NullLogger<SupervisionService>.Instance);

        // Act
        var snapshot = await service.EvaluateComponentAsync(compId);

        // Assert
        Assert.Equal(ComponentState.Demarrage, snapshot.State);
        Assert.Equal(LogProofState.WaitingForProof, snapshot.LogProofStatus);
    }

    private static ConnectorTargetFactory CreateTargetFactory(IDbContextFactory<N4SentinelDbContext> dbFactory)
    {
        var tempKeyDir = Path.Combine(Path.GetTempPath(), $"n4sentinel_dp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempKeyDir);

        var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(tempKeyDir));
        var credStore = new CredentialStore(dbFactory, dataProtection, NullLogger<CredentialStore>.Instance);
        return new ConnectorTargetFactory(dbFactory, credStore, NullLogger<ConnectorTargetFactory>.Instance);
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeN4Connector : IN4Connector
    {
        public string ServiceStatusToReturn { get; set; } = "Running";
        public string LogTextToReturn { get; set; } = string.Empty;

        public Task<ConnectorResult<string>> PingAsync(ConnectorTarget target, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<string>.Ok("N4SRV01 (PowerShell 5.1)", TimeSpan.FromMilliseconds(10)));

        public Task<ConnectorResult<ServiceSnapshot>> GetServiceAsync(ConnectorTarget target, string serviceName, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<ServiceSnapshot>.Ok(new ServiceSnapshot
            {
                Name = serviceName,
                Status = ServiceStatusToReturn,
                ProcessId = ServiceStatusToReturn == "Running" ? 1234 : null
            }, TimeSpan.FromMilliseconds(10)));

        public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> GetServicesAsync(ConnectorTarget target, IReadOnlyCollection<string> serviceNames, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<IReadOnlyList<ServiceSnapshot>>.Ok(
                serviceNames.Select(name => new ServiceSnapshot { Name = name, Status = ServiceStatusToReturn }).ToList(), TimeSpan.FromMilliseconds(10)));

        /// <summary>
        /// Services que la doublure declare presents sur la machine, pour les
        /// tests de decouverte de composants non declares.
        /// </summary>
        public List<ServiceSnapshot> ServicesPresents { get; set; } = [];

        public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> ListServicesAsync(
            ConnectorTarget target, IReadOnlyCollection<string> namePatterns, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<IReadOnlyList<ServiceSnapshot>>.Ok(
                ServicesPresents
                    .Where(s => namePatterns.Any(m =>
                        s.Name.Contains(m, StringComparison.OrdinalIgnoreCase)
                        || (s.DisplayName?.Contains(m, StringComparison.OrdinalIgnoreCase) ?? false)))
                    .ToList(),
                TimeSpan.FromMilliseconds(10)));

        public Task<ConnectorResult<SystemSnapshot>> GetSystemAsync(ConnectorTarget target, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<SystemSnapshot>.Ok(new SystemSnapshot { HostName = target.HostName, ClockSkewSeconds = 0.1 }, TimeSpan.FromMilliseconds(10)));

        public Task<ConnectorResult<LogDelta>> ReadLogDeltaAsync(ConnectorTarget target, string logPathOrPattern, long offset, int maxBytes = 262144, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<LogDelta>.Ok(new LogDelta { Exists = true, ResolvedPath = logPathOrPattern, Text = LogTextToReturn }, TimeSpan.FromMilliseconds(10)));

        public Task<ConnectorResult<LogFileInfo>> ResolveLogAsync(ConnectorTarget target, string logPathOrPattern, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<LogFileInfo>.Ok(new LogFileInfo { Exists = true, Path = logPathOrPattern }, TimeSpan.FromMilliseconds(10)));

        public Task<ConnectorResult<ServiceSnapshot>> ControlServiceAsync(
            ConnectorTarget target, string serviceName, ServiceControlAction action, CancellationToken ct = default)
        {
            ServiceStatusToReturn = action == ServiceControlAction.Demarrer ? "Running" : "Stopped";
            return Task.FromResult(ConnectorResult<ServiceSnapshot>.Ok(
                new ServiceSnapshot { Name = serviceName, Status = ServiceStatusToReturn },
                TimeSpan.FromMilliseconds(10)));
        }
    }
}
