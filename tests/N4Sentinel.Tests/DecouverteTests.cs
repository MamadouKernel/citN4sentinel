using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests de la detection de composants non declares - SUP-06, recette AC-15.
///
/// Le cas vise est concret : quelqu'un installe un noeud Cluster
/// supplementaire un vendredi soir sans le declarer. Il tourne, il participe
/// au cluster, et l'application l'ignore - donc ne l'arrete pas pendant une
/// maintenance censee tout arreter.
/// </summary>
public sealed class DecouverteTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _keyPath = string.Empty;
    private TestDbContextFactory _factory = null!;
    private FauxConnecteur _connecteur = null!;
    private UndeclaredComponentScanner _scanner = null!;
    private Guid _envId;

    public async Task InitializeAsync()
    {
        var cs = $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
        _factory = new TestDbContextFactory(TestDbContextOptions.Builder(cs).Options);

        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();

            var env = new N4Environment { Code = "UAT", Name = "Recette" };
            db.Environments.Add(env);
            await db.SaveChangesAsync();
            _envId = env.Id;

            db.Servers.Add(new N4Server { EnvironmentId = _envId, HostName = Environment.MachineName });
            await db.SaveChangesAsync();

            // Un seul composant est declare, alors que trois services tournent.
            db.Components.Add(new N4Component
            {
                EnvironmentId = _envId,
                LogicalName = "Center Node",
                Role = ComponentRole.CenterNode,
                WindowsServiceName = "Navis N4 Center Node",
                ControlMode = ControlMode.Pilotable
            });
            await db.SaveChangesAsync();
        }

        _keyPath = Path.Combine(Path.GetTempPath(), $"n4-cles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keyPath);

        var store = new CredentialStore(_factory,
            DataProtectionProvider.Create(new DirectoryInfo(_keyPath)),
            NullLogger<CredentialStore>.Instance);

        _connecteur = new FauxConnecteur
        {
            Services =
            [
                new ServiceSnapshot { Name = "Navis N4 Center Node", DisplayName = "Navis N4 Center Node", Status = "Running" },
                new ServiceSnapshot { Name = "Navis N4 Cluster Node", DisplayName = "Navis N4 Cluster Node", Status = "Running" },
                new ServiceSnapshot { Name = "Navis XPS Bridge Daemon", DisplayName = "Navis XPS Bridge Daemon", Status = "Stopped" },
                new ServiceSnapshot { Name = "Spooler", DisplayName = "Print Spooler", Status = "Running" }
            ]
        };

        _scanner = new UndeclaredComponentScanner(
            _factory,
            new ConnectorTargetFactory(_factory, store, NullLogger<ConnectorTargetFactory>.Instance),
            _connecteur);
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

    [Fact(DisplayName = "Les services Navis non declares sont reperes, le declare est ignore")]
    public async Task Les_Non_Declares_Sont_Reperes()
    {
        var r = await _scanner.ScanAsync(_envId);

        Assert.True(r.Succeeded, r.Error);
        Assert.Equal(1, r.ServersScanned);
        Assert.True(r.IsComplete);

        // Deux non declares : Cluster et Bridge. Le Center est declare.
        Assert.Equal(2, r.Undeclared.Count);
        Assert.DoesNotContain(r.Undeclared, u => u.ServiceName == "Navis N4 Center Node");
    }

    [Fact(DisplayName = "Un service etranger a N4 n'est pas remonte")]
    public async Task Les_Services_Etrangers_Sont_Ecartes()
    {
        var r = await _scanner.ScanAsync(_envId);

        // Le spouleur d'impression n'a rien a faire dans une cartographie N4.
        Assert.DoesNotContain(r.Undeclared, u => u.ServiceName == "Spooler");
    }

    [Theory(DisplayName = "Le role est deduit du nom du service")]
    [InlineData("Navis N4 Cluster Node", ComponentRole.ClusterNode)]
    [InlineData("Navis N4 Center Node", ComponentRole.CenterNode)]
    [InlineData("Navis XPS Bridge Daemon", ComponentRole.BridgeDaemon)]
    [InlineData("Navis XPS Service", ComponentRole.Xps)]
    [InlineData("Navis ECN4 Daemon", ComponentRole.Ecn4)]
    [InlineData("Navis ECN4web", ComponentRole.Ecn4Web)]
    [InlineData("Navis Truc Inconnu", ComponentRole.Autre)]
    public async Task Le_Role_Est_Deduit(string nomService, ComponentRole attendu)
    {
        // Le jeu d'essai declare deja un Center Node : sans ce nettoyage, le
        // cas "Navis N4 Center Node" serait correctement ecarte par le scanner
        // et le test n'aurait rien a examiner. Cette theorie porte sur la
        // deduction du role, pas sur le filtrage.
        await using (var db = _factory.CreateDbContext())
        {
            db.Components.RemoveRange(await db.Components.ToListAsync());
            await db.SaveChangesAsync();
        }

        _connecteur.Services = [new ServiceSnapshot { Name = nomService, DisplayName = nomService, Status = "Running" }];

        var r = await _scanner.ScanAsync(_envId);

        Assert.Equal(attendu, r.Undeclared.Single().SuggestedRole);
    }

    [Fact(DisplayName = "Un Standby n'est pas confondu avec un Center, bien que son nom le contienne")]
    public async Task Le_Standby_N_Est_Pas_Un_Center()
    {
        _connecteur.Services =
            [new ServiceSnapshot { Name = "Navis N4 Standby Center Node", DisplayName = "Navis N4 Standby Center Node", Status = "Running" }];

        var r = await _scanner.ScanAsync(_envId);

        Assert.Equal(ComponentRole.StandbyCenterNode, r.Undeclared.Single().SuggestedRole);
    }

    [Fact(DisplayName = "Le balayage ne declare rien de lui-meme")]
    public async Task Le_Balayage_Ne_Declare_Rien()
    {
        await _scanner.ScanAsync(_envId);

        await using var db = _factory.CreateDbContext();

        // Creer la fiche a la place de l'operateur reviendrait a autoriser des
        // actions sur une machine que personne n'a validee (AC-15).
        Assert.Equal(1, await db.Components.CountAsync());
    }

    [Fact(DisplayName = "Declarer cree une fiche en Brouillon, en supervision seule")]
    public async Task Declarer_Cree_Une_Fiche_A_Valider()
    {
        var r = await _scanner.ScanAsync(_envId);
        var cluster = r.Undeclared.Single(u => u.ServiceName == "Navis N4 Cluster Node");

        var id = await _scanner.DeclareAsync(_envId, cluster, ComponentRole.ClusterNode);
        Assert.NotNull(id);

        await using var db = _factory.CreateDbContext();
        var cree = await db.Components.SingleAsync(c => c.Id == id);

        Assert.Equal(LifecycleStatus.Brouillon, cree.Status);
        Assert.Equal(ControlMode.SuperviseSeulement, cree.ControlMode);
        Assert.Equal("Navis N4 Cluster Node", cree.WindowsServiceName);
        Assert.Contains("À compléter et valider", cree.Description!);
    }

    [Fact(DisplayName = "Un serveur injoignable est distingue d'un serveur sans anomalie")]
    public async Task Un_Serveur_Injoignable_Est_Signale()
    {
        _connecteur.EchecListe = "Acces refuse par le serveur.";

        var r = await _scanner.ScanAsync(_envId);

        // Un balayage partiel ne doit pas passer pour un balayage propre.
        Assert.False(r.IsComplete);
        Assert.Single(r.Unreachable);
        Assert.Empty(r.Undeclared);
    }

    [Fact(DisplayName = "Un environnement sans serveur ne peut pas etre balaye")]
    public async Task Sans_Serveur_Le_Balayage_Echoue()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Servers.RemoveRange(await db.Servers.ToListAsync());
            db.Components.RemoveRange(await db.Components.ToListAsync());
            await db.SaveChangesAsync();
        }

        var r = await _scanner.ScanAsync(_envId);

        Assert.False(r.Succeeded);
        Assert.Contains("Aucun serveur", r.Error!);
    }

    // -----------------------------------------------------------------------
    private sealed class FauxConnecteur : IN4Connector
    {
        public List<ServiceSnapshot> Services { get; set; } = [];
        public string? EchecListe { get; set; }

        public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> ListServicesAsync(
            ConnectorTarget target, IReadOnlyCollection<string> namePatterns, CancellationToken ct = default)
        {
            if (EchecListe is not null)
                return Task.FromResult(ConnectorResult<IReadOnlyList<ServiceSnapshot>>.Fail(
                    ConnectorFailure.AccesRefuse, EchecListe, TimeSpan.Zero));

            var filtres = Services
                .Where(s => namePatterns.Any(m =>
                    s.Name.Contains(m, StringComparison.OrdinalIgnoreCase)
                    || (s.DisplayName?.Contains(m, StringComparison.OrdinalIgnoreCase) ?? false)))
                .ToList();

            return Task.FromResult(ConnectorResult<IReadOnlyList<ServiceSnapshot>>.Ok(filtres, TimeSpan.Zero));
        }

        public Task<ConnectorResult<string>> PingAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<string>.Ok("ok", TimeSpan.Zero));

        public Task<ConnectorResult<ServiceSnapshot>> GetServiceAsync(ConnectorTarget t, string n, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<ServiceSnapshot>.Ok(new ServiceSnapshot { Name = n, Status = "Running" }, TimeSpan.Zero));

        public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> GetServicesAsync(ConnectorTarget t, IReadOnlyCollection<string> n, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<IReadOnlyList<ServiceSnapshot>>.Ok([], TimeSpan.Zero));

        public Task<ConnectorResult<SystemSnapshot>> GetSystemAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<SystemSnapshot>.Ok(new SystemSnapshot { HostName = t.HostName }, TimeSpan.Zero));

        public Task<ConnectorResult<LogDelta>> ReadLogDeltaAsync(ConnectorTarget t, string p, long o, int m = 262144, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<LogDelta>.Ok(new LogDelta { ResolvedPath = p }, TimeSpan.Zero));

        public Task<ConnectorResult<LogFileInfo>> ResolveLogAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<LogFileInfo>.Ok(new LogFileInfo { Exists = false }, TimeSpan.Zero));

        public Task<ConnectorResult<LiveMetrics>> GetLiveMetricsAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<LiveMetrics>.Ok(new LiveMetrics { HostName = t.HostName }, TimeSpan.Zero));

        public Task<ConnectorResult<TimeSyncSnapshot>> GetTimeSyncAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<TimeSyncSnapshot>.Ok(new TimeSyncSnapshot { HostName = t.HostName }, TimeSpan.Zero));

        public Task<ConnectorResult<UpdateSnapshot>> GetPendingUpdatesAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<UpdateSnapshot>.Ok(new UpdateSnapshot { HostName = t.HostName }, TimeSpan.Zero));

        public Task<ConnectorResult<ServiceSnapshot>> ControlServiceAsync(
            ConnectorTarget t, string n, ServiceControlAction a, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<ServiceSnapshot>.Ok(
                new ServiceSnapshot { Name = n, Status = a == ServiceControlAction.Demarrer ? "Running" : "Stopped" },
                TimeSpan.Zero));

        public Task<ConnectorResult<FolderSnapshot>> ListFilesAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<FolderSnapshot>.Ok(new FolderSnapshot { Path = p, Exists = false }, TimeSpan.Zero));

        public Task<ConnectorResult<WriteProbeResult>> ProbeWriteAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<WriteProbeResult>.Ok(new WriteProbeResult { CanWrite = false, Error = "Non simulé." }, TimeSpan.Zero));
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
