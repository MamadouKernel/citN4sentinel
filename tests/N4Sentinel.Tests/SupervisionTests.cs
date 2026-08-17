using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectivity;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Orchestration;
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

        var options = TestDbContextOptions.Builder(_connectionString).Options;

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

    [Fact(DisplayName = "EvaluateComponentAsync retourne Maintenance sans relever d'echec quand le composant est declare en maintenance (FR-052)")]
    public async Task EvaluateComponentAsync_Returns_Maintenance_When_Flagged()
    {
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
                MaintenanceMode = true,
                MaintenanceNote = "Remplacement disque planifié."
            });
            await db.SaveChangesAsync();
        }

        // Le connecteur simule un service arrêté (l'échec habituel) : la
        // maintenance déclarée doit primer avant toute interrogation réelle.
        var fakeConnector = new FakeN4Connector { ServiceStatusToReturn = "Stopped" };
        var targetFactory = CreateTargetFactory(_factory);
        var service = new SupervisionService(_factory, targetFactory, fakeConnector, NullLogger<SupervisionService>.Instance);

        var snapshot = await service.EvaluateComponentAsync(compId);

        Assert.Equal(ComponentState.Maintenance, snapshot.State);
        Assert.Contains("Remplacement disque planifié.", snapshot.Verdict);
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

    [Fact(DisplayName = "Un Center dont le marqueur de role actif apparait dans le journal est signale comme role actif detenu")]
    public async Task EvaluateComponentAsync_Sets_HoldsActiveRole_True_When_Marker_Present()
    {
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
                Role = ComponentRole.CenterNode,
                WindowsServiceName = "NavisCenterService",
                ControlMode = ControlMode.Pilotable,
                Readiness = new ReadinessProfile
                {
                    LogPath = "C:\\Navis\\logs\\apex.log",
                    ReadyPatterns = ["Web tier servlet 'action' initialized"],
                    ActiveRolePatterns = ["Cluster role: ACTIVE"]
                }
            });
            await db.SaveChangesAsync();
        }

        var fakeConnector = new FakeN4Connector
        {
            ServiceStatusToReturn = "Running",
            LogTextToReturn = "Web tier servlet 'action' initialized\nCluster role: ACTIVE"
        };

        var service = new SupervisionService(_factory, CreateTargetFactory(_factory), fakeConnector, NullLogger<SupervisionService>.Instance);
        var snapshot = await service.EvaluateComponentAsync(compId);

        Assert.True(snapshot.HoldsActiveRole);
    }

    [Fact(DisplayName = "Un Standby dont le marqueur de role actif n'apparait pas est signale comme role non detenu, pas comme inconnu")]
    public async Task EvaluateComponentAsync_Sets_HoldsActiveRole_False_When_Marker_Absent()
    {
        var envId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var compId = Guid.NewGuid();

        await using (var db = _factory.CreateDbContext())
        {
            db.Environments.Add(new N4Environment { Id = envId, Code = "PROD", Name = "Production" });
            db.Servers.Add(new N4Server { Id = serverId, EnvironmentId = envId, HostName = "N4SRV02" });
            db.Components.Add(new N4Component
            {
                Id = compId,
                EnvironmentId = envId,
                ServerId = serverId,
                LogicalName = "Standby Node",
                Role = ComponentRole.StandbyCenterNode,
                WindowsServiceName = "NavisStandbyService",
                ControlMode = ControlMode.Pilotable,
                Readiness = new ReadinessProfile
                {
                    LogPath = "C:\\Navis\\logs\\apex.log",
                    ReadyPatterns = ["Web tier servlet 'action' initialized"],
                    ActiveRolePatterns = ["Cluster role: ACTIVE"]
                }
            });
            await db.SaveChangesAsync();
        }

        var fakeConnector = new FakeN4Connector
        {
            ServiceStatusToReturn = "Running",
            LogTextToReturn = "Web tier servlet 'action' initialized\nCluster role: STANDBY"
        };

        var service = new SupervisionService(_factory, CreateTargetFactory(_factory), fakeConnector, NullLogger<SupervisionService>.Instance);
        var snapshot = await service.EvaluateComponentAsync(compId);

        Assert.False(snapshot.HoldsActiveRole);
    }

    [Fact(DisplayName = "Sans marqueur de role actif configure, le role reste a confirmer plutot que suppose")]
    public async Task EvaluateComponentAsync_Leaves_HoldsActiveRole_Null_When_No_Pattern_Configured()
    {
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
                Role = ComponentRole.CenterNode,
                WindowsServiceName = "NavisCenterService",
                ControlMode = ControlMode.Pilotable,
                Readiness = new ReadinessProfile
                {
                    LogPath = "C:\\Navis\\logs\\apex.log",
                    ReadyPatterns = ["Web tier servlet 'action' initialized"]
                    // ActiveRolePatterns volontairement vide.
                }
            });
            await db.SaveChangesAsync();
        }

        var fakeConnector = new FakeN4Connector
        {
            ServiceStatusToReturn = "Running",
            LogTextToReturn = "Web tier servlet 'action' initialized"
        };

        var service = new SupervisionService(_factory, CreateTargetFactory(_factory), fakeConnector, NullLogger<SupervisionService>.Instance);
        var snapshot = await service.EvaluateComponentAsync(compId);

        Assert.Null(snapshot.HoldsActiveRole);
    }

    [Fact(DisplayName = "Un echange N4-XPS trouve dans le journal confirme la synchronisation a l'instant present")]
    public async Task EvaluateComponentAsync_Confirme_La_Synchro_Quand_Le_Marqueur_Est_Present()
    {
        var envId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var compId = Guid.NewGuid();

        await using (var db = _factory.CreateDbContext())
        {
            db.Environments.Add(new N4Environment { Id = envId, Code = "PROD", Name = "Production" });
            db.Servers.Add(new N4Server { Id = serverId, EnvironmentId = envId, HostName = "N4SRV01" });
            db.Components.Add(new N4Component
            {
                Id = compId, EnvironmentId = envId, ServerId = serverId, LogicalName = "Bridge Daemon",
                Role = ComponentRole.BridgeDaemon, WindowsServiceName = "NavisBridgeService", ControlMode = ControlMode.Pilotable,
                Readiness = new ReadinessProfile
                {
                    LogPath = "C:\\Navis\\logs\\bridge.log",
                    ReadyPatterns = ["Bridge started"],
                    SyncPatterns = ["XPS ack received"],
                    SyncDelayThresholdMinutes = 15
                }
            });
            await db.SaveChangesAsync();
        }

        var fakeConnector = new FakeN4Connector
        {
            ServiceStatusToReturn = "Running",
            LogTextToReturn = "Bridge started\nXPS ack received"
        };

        var service = new SupervisionService(_factory, CreateTargetFactory(_factory), fakeConnector, NullLogger<SupervisionService>.Instance);
        var snapshot = await service.EvaluateComponentAsync(compId);

        Assert.NotNull(snapshot.LastSyncConfirmedAt);
        Assert.True(snapshot.LastSyncConfirmedAt!.Value >= DateTimeOffset.UtcNow.AddSeconds(-30));
        Assert.False(snapshot.SyncDelayed);
    }

    [Fact(DisplayName = "Une confirmation ancienne, absente du delta courant, est reportee depuis le relevé precedent")]
    public async Task EvaluateComponentAsync_Reporte_La_Derniere_Confirmation_Quand_Absente_Du_Delta()
    {
        var envId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var compId = Guid.NewGuid();

        await using (var db = _factory.CreateDbContext())
        {
            db.Environments.Add(new N4Environment { Id = envId, Code = "PROD", Name = "Production" });
            db.Servers.Add(new N4Server { Id = serverId, EnvironmentId = envId, HostName = "N4SRV01" });
            db.Components.Add(new N4Component
            {
                Id = compId, EnvironmentId = envId, ServerId = serverId, LogicalName = "Bridge Daemon",
                Role = ComponentRole.BridgeDaemon, WindowsServiceName = "NavisBridgeService", ControlMode = ControlMode.Pilotable,
                Readiness = new ReadinessProfile
                {
                    LogPath = "C:\\Navis\\logs\\bridge.log",
                    ReadyPatterns = ["Bridge started"],
                    SyncPatterns = ["XPS ack received"],
                    SyncDelayThresholdMinutes = 15
                }
            });
            await db.SaveChangesAsync();
        }

        // Le relevé precedent a confirme un echange il y a 5 minutes.
        var precedent = new ComponentHealthSnapshot
        {
            ComponentId = compId,
            LastSyncConfirmedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        var fakeConnector = new FakeN4Connector
        {
            ServiceStatusToReturn = "Running",
            LogTextToReturn = "Bridge started" // pas de nouvel echange dans ce delta
        };

        var service = new SupervisionService(_factory, CreateTargetFactory(_factory), fakeConnector, NullLogger<SupervisionService>.Instance);
        var snapshot = await service.EvaluateComponentAsync(compId, previous: precedent);

        Assert.Equal(precedent.LastSyncConfirmedAt, snapshot.LastSyncConfirmedAt);
        Assert.False(snapshot.SyncDelayed); // 5 min < seuil de 15 min
    }

    [Fact(DisplayName = "Une confirmation trop ancienne, meme reportee, declare la synchronisation en retard")]
    public async Task EvaluateComponentAsync_Declare_Le_Retard_Quand_La_Confirmation_Est_Trop_Ancienne()
    {
        var envId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var compId = Guid.NewGuid();

        await using (var db = _factory.CreateDbContext())
        {
            db.Environments.Add(new N4Environment { Id = envId, Code = "PROD", Name = "Production" });
            db.Servers.Add(new N4Server { Id = serverId, EnvironmentId = envId, HostName = "N4SRV01" });
            db.Components.Add(new N4Component
            {
                Id = compId, EnvironmentId = envId, ServerId = serverId, LogicalName = "Bridge Daemon",
                Role = ComponentRole.BridgeDaemon, WindowsServiceName = "NavisBridgeService", ControlMode = ControlMode.Pilotable,
                Readiness = new ReadinessProfile
                {
                    LogPath = "C:\\Navis\\logs\\bridge.log",
                    ReadyPatterns = ["Bridge started"],
                    SyncPatterns = ["XPS ack received"],
                    SyncDelayThresholdMinutes = 15
                }
            });
            await db.SaveChangesAsync();
        }

        var precedent = new ComponentHealthSnapshot
        {
            ComponentId = compId,
            LastSyncConfirmedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
        };

        var fakeConnector = new FakeN4Connector
        {
            ServiceStatusToReturn = "Running",
            LogTextToReturn = "Bridge started"
        };

        var service = new SupervisionService(_factory, CreateTargetFactory(_factory), fakeConnector, NullLogger<SupervisionService>.Instance);
        var snapshot = await service.EvaluateComponentAsync(compId, previous: precedent);

        Assert.True(snapshot.SyncDelayed);
    }

    [Fact(DisplayName = "Le temps de reponse du connecteur est reporte sur l'instantane")]
    public async Task EvaluateComponentAsync_Reporte_Le_Temps_De_Reponse()
    {
        var envId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var compId = Guid.NewGuid();

        await using (var db = _factory.CreateDbContext())
        {
            db.Environments.Add(new N4Environment { Id = envId, Code = "PROD", Name = "Production" });
            db.Servers.Add(new N4Server { Id = serverId, EnvironmentId = envId, HostName = "N4SRV01" });
            db.Components.Add(new N4Component
            {
                Id = compId, EnvironmentId = envId, ServerId = serverId, LogicalName = "Cluster Node 1",
                WindowsServiceName = "NavisClusterService", ControlMode = ControlMode.Pilotable
            });
            await db.SaveChangesAsync();
        }

        var fakeConnector = new FakeN4Connector { ServiceStatusToReturn = "Running" };
        var service = new SupervisionService(_factory, CreateTargetFactory(_factory), fakeConnector, NullLogger<SupervisionService>.Instance);
        var snapshot = await service.EvaluateComponentAsync(compId);

        Assert.NotNull(snapshot.ResponseTimeMs);
        Assert.True(snapshot.ResponseTimeMs >= 0);
    }

    [Fact(DisplayName = "CenterContinuityService juge le Standby apte quand son etat consolide est Disponible")]
    public async Task CenterContinuityService_Standby_Est_Apte_Quand_Disponible()
    {
        var envId = Guid.NewGuid();
        var serverId = Guid.NewGuid();

        await using (var db = _factory.CreateDbContext())
        {
            db.Environments.Add(new N4Environment { Id = envId, Code = "PROD", Name = "Production" });
            db.Servers.Add(new N4Server { Id = serverId, EnvironmentId = envId, HostName = "N4SRV01" });
            db.Components.Add(new N4Component
            {
                EnvironmentId = envId, ServerId = serverId, LogicalName = "Center Node", Role = ComponentRole.CenterNode,
                WindowsServiceName = "NavisCenterService", ControlMode = ControlMode.Pilotable,
                Readiness = new ReadinessProfile
                {
                    LogPath = "C:\\Navis\\logs\\apex.log",
                    ReadyPatterns = ["Web tier servlet 'action' initialized"],
                    ActiveRolePatterns = ["Cluster role: ACTIVE"]
                }
            });
            db.Components.Add(new N4Component
            {
                EnvironmentId = envId, ServerId = serverId, LogicalName = "Standby Node", Role = ComponentRole.StandbyCenterNode,
                WindowsServiceName = "NavisStandbyService", ControlMode = ControlMode.Pilotable,
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
            LogTextToReturn = "Web tier servlet 'action' initialized"
        };

        var supervision = new SupervisionService(_factory, CreateTargetFactory(_factory), fakeConnector, NullLogger<SupervisionService>.Instance);
        var continuity = new CenterContinuityService(_factory, supervision);

        var assessment = await continuity.AssessAsync(envId);

        Assert.True(assessment.StandbyIsCapable);
        Assert.Null(assessment.StandbyUnavailableReason);
    }

    [Fact(DisplayName = "CenterContinuityService dit clairement l'absence de Standby, plutot que de conclure apte a tort")]
    public async Task CenterContinuityService_Sans_Standby_Declare()
    {
        var envId = Guid.NewGuid();
        var serverId = Guid.NewGuid();

        await using (var db = _factory.CreateDbContext())
        {
            db.Environments.Add(new N4Environment { Id = envId, Code = "PROD", Name = "Production" });
            db.Servers.Add(new N4Server { Id = serverId, EnvironmentId = envId, HostName = "N4SRV01" });
            db.Components.Add(new N4Component
            {
                EnvironmentId = envId, ServerId = serverId, LogicalName = "Center Node", Role = ComponentRole.CenterNode,
                WindowsServiceName = "NavisCenterService", ControlMode = ControlMode.Pilotable
            });
            await db.SaveChangesAsync();
        }

        var fakeConnector = new FakeN4Connector { ServiceStatusToReturn = "Running" };
        var supervision = new SupervisionService(_factory, CreateTargetFactory(_factory), fakeConnector, NullLogger<SupervisionService>.Instance);
        var continuity = new CenterContinuityService(_factory, supervision);

        var assessment = await continuity.AssessAsync(envId);

        Assert.Null(assessment.Standby);
        Assert.False(assessment.StandbyIsCapable);
        Assert.Contains("Aucun Standby", assessment.StandbyUnavailableReason);
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

        /// <summary>Métriques que la doublure déclare, pour les tests de vitalité.</summary>
        public LiveMetrics MetriquesARetourner { get; set; } = new() { HostName = "N4SRV01" };
        public TimeSyncSnapshot HorlogeARetourner { get; set; } = new() { HostName = "N4SRV01" };

        public Task<ConnectorResult<LiveMetrics>> GetLiveMetricsAsync(ConnectorTarget target, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<LiveMetrics>.Ok(MetriquesARetourner, TimeSpan.FromMilliseconds(10)));

        public Task<ConnectorResult<TimeSyncSnapshot>> GetTimeSyncAsync(ConnectorTarget target, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<TimeSyncSnapshot>.Ok(HorlogeARetourner, TimeSpan.FromMilliseconds(10)));

        public Task<ConnectorResult<UpdateSnapshot>> GetPendingUpdatesAsync(ConnectorTarget target, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<UpdateSnapshot>.Ok(
                new UpdateSnapshot { HostName = target.HostName, PendingCount = 3, SecurityCount = 2 },
                TimeSpan.FromSeconds(45)));

        public Task<ConnectorResult<ServiceSnapshot>> ControlServiceAsync(
            ConnectorTarget target, string serviceName, ServiceControlAction action, CancellationToken ct = default)
        {
            ServiceStatusToReturn = action == ServiceControlAction.Demarrer ? "Running" : "Stopped";
            return Task.FromResult(ConnectorResult<ServiceSnapshot>.Ok(
                new ServiceSnapshot { Name = serviceName, Status = ServiceStatusToReturn },
                TimeSpan.FromMilliseconds(10)));
        }

        /// <summary>Fichiers que la doublure déclare, pour les tests de dossiers partagés.</summary>
        public FolderSnapshot DossierARetourner { get; set; } = new() { Path = string.Empty, Exists = false };

        public Task<ConnectorResult<FolderSnapshot>> ListFilesAsync(ConnectorTarget target, string path, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<FolderSnapshot>.Ok(DossierARetourner, TimeSpan.FromMilliseconds(10)));

        public Task<ConnectorResult<WriteProbeResult>> ProbeWriteAsync(ConnectorTarget target, string path, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<WriteProbeResult>.Ok(new WriteProbeResult { CanWrite = true }, TimeSpan.FromMilliseconds(10)));
    }
}
