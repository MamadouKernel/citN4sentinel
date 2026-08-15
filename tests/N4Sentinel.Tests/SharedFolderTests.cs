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
/// Tests de la supervision des dossiers partagés — FR-059A à FR-059D.
///
/// CE QUI EST GROUNDÉ, ET RIEN DE PLUS. Le seul fichier KahaDB attesté par le
/// corpus de support est « db.data » ; ces tests ne valident donc que ce
/// fichier-là, jamais un format ou une structure inventés. La classification
/// en attente/consommé/bloqué/erreur dépend de sous-dossiers déclarés par le
/// site : un sous-dossier non déclaré doit rester "non classifié", jamais
/// compté comme vide — c'est le comportement précisément vérifié ici.
/// </summary>
public sealed class SharedFolderTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _keyPath = string.Empty;
    private TestDbContextFactory _factory = null!;
    private SharedFolderHealthService _sante = null!;
    private FauxConnecteur _connecteur = null!;

    private Guid _envId;
    private Guid _serverId;

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

            var serveur = new N4Server { EnvironmentId = _envId, HostName = "N4FILESRV01" };
            db.Servers.Add(serveur);
            await db.SaveChangesAsync();
            _serverId = serveur.Id;
        }

        _keyPath = Path.Combine(Path.GetTempPath(), $"n4-cles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keyPath);

        var store = new CredentialStore(_factory,
            DataProtectionProvider.Create(new DirectoryInfo(_keyPath)),
            NullLogger<CredentialStore>.Instance);

        _connecteur = new FauxConnecteur();

        _sante = new SharedFolderHealthService(
            _factory,
            new ConnectorTargetFactory(_factory, store, NullLogger<ConnectorTargetFactory>.Instance),
            _connecteur,
            NullLogger<SharedFolderHealthService>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_keyPath))
            try { Directory.Delete(_keyPath, recursive: true); } catch { }

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

    // =======================================================================
    // FR-059A — Composant non configuré
    // =======================================================================
    [Fact(DisplayName = "Un composant sans chemin déclaré n'est pas traité comme un dossier partagé")]
    public async Task EvaluateAsync_Sans_Chemin_Declare_Le_Dit()
    {
        var composant = await CreerComposantAsync(sharedFolder: null);

        var snapshot = await _sante.EvaluateAsync(composant);

        Assert.Null(snapshot.Reachable);
        Assert.Contains("Aucun chemin", snapshot.UnreachableReason!);
    }

    // =======================================================================
    // FR-059B — Accessibilité et volumétrie
    // =======================================================================
    [Fact(DisplayName = "Un dossier inaccessible est signalé sans faire planter le relevé")]
    public async Task EvaluateAsync_Dossier_Inexistant_Est_Signale()
    {
        _connecteur.Reponse = new FolderSnapshot { Path = @"\\srv\partage", Exists = false };
        var composant = await CreerComposantAsync(new SharedFolderProfile { RootPath = @"\\srv\partage" });

        var snapshot = await _sante.EvaluateAsync(composant);

        Assert.False(snapshot.Reachable);
        Assert.Contains(@"\\srv\partage", snapshot.UnreachableReason!);
    }

    [Fact(DisplayName = "La volumétrie totale et les dates extrêmes sont reprises telles quelles")]
    public async Task EvaluateAsync_Calcule_La_Volumetrie()
    {
        var t1 = DateTimeOffset.UtcNow.AddDays(-5);
        var t2 = DateTimeOffset.UtcNow.AddHours(-1);

        _connecteur.Reponse = new FolderSnapshot
        {
            Path = @"\\srv\partage",
            Exists = true,
            Files =
            [
                new RemoteFileInfo { Name = "a.txt", SizeBytes = 100, LastWriteTime = t1 },
                new RemoteFileInfo { Name = "b.txt", SizeBytes = 250, LastWriteTime = t2 }
            ]
        };
        var composant = await CreerComposantAsync(new SharedFolderProfile { RootPath = @"\\srv\partage" });

        var snapshot = await _sante.EvaluateAsync(composant);

        Assert.True(snapshot.Reachable);
        Assert.Equal(2, snapshot.TotalFileCount);
        Assert.Equal(350, snapshot.TotalSizeBytes);
        Assert.Equal(t1, snapshot.OldestFileAt);
        Assert.Equal(t2, snapshot.NewestFileAt);
    }

    [Fact(DisplayName = "Un échec du test d'écriture non destructif devient un indice")]
    public async Task EvaluateAsync_Echec_Ecriture_Devient_Un_Indice()
    {
        _connecteur.Reponse = new FolderSnapshot { Path = @"\\srv\partage", Exists = true };
        _connecteur.ReponseEcriture = new WriteProbeResult { CanWrite = false, Error = "Accès refusé." };
        var composant = await CreerComposantAsync(new SharedFolderProfile { RootPath = @"\\srv\partage" });

        var snapshot = await _sante.EvaluateAsync(composant);

        Assert.False(snapshot.CanWrite);
        Assert.Contains(snapshot.CorruptionIndicators, i => i.Contains("Accès refusé"));
    }

    // =======================================================================
    // FR-059D — Fichier KahaDB obligatoire (le seul fichier grounded)
    // =======================================================================
    [Fact(DisplayName = "L'absence de db.data dans un dossier ActiveMQ/KahaDB devient un indice, pas une conclusion")]
    public async Task EvaluateAsync_DbData_Absent_Devient_Un_Indice()
    {
        _connecteur.Reponse = new FolderSnapshot { Path = @"\\srv\amq", Exists = true, Files = [] };
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\amq",
            Category = SharedFolderCategory.ActiveMqKahaDb
        });

        var snapshot = await _sante.EvaluateAsync(composant);

        Assert.False(snapshot.MandatoryFilesPresent);
        Assert.Contains("db.data", snapshot.MissingMandatoryFiles);
        Assert.Contains(snapshot.CorruptionIndicators, i => i.Contains("db.data"));
        // Reconstruit sans risque : l'indice le dit, il n'affirme pas un incident.
        Assert.Contains(snapshot.CorruptionIndicators, i => i.Contains("reconstruit sans risque"));
    }

    [Fact(DisplayName = "db.data présent et non vide ne déclenche aucun indice")]
    public async Task EvaluateAsync_DbData_Present_Ne_Declenche_Rien()
    {
        _connecteur.Reponse = new FolderSnapshot
        {
            Path = @"\\srv\amq",
            Exists = true,
            Files = [new RemoteFileInfo { Name = "db.data", SizeBytes = 1024, LastWriteTime = DateTimeOffset.UtcNow }]
        };
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\amq",
            Category = SharedFolderCategory.ActiveMqKahaDb
        });

        var snapshot = await _sante.EvaluateAsync(composant);

        Assert.True(snapshot.MandatoryFilesPresent);
        Assert.Empty(snapshot.CorruptionIndicators);
    }

    [Fact(DisplayName = "db.data présent mais de taille nulle devient un indice")]
    public async Task EvaluateAsync_DbData_Taille_Nulle_Devient_Un_Indice()
    {
        _connecteur.Reponse = new FolderSnapshot
        {
            Path = @"\\srv\amq",
            Exists = true,
            Files = [new RemoteFileInfo { Name = "db.data", SizeBytes = 0, LastWriteTime = DateTimeOffset.UtcNow }]
        };
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\amq",
            Category = SharedFolderCategory.ActiveMqKahaDb
        });

        var snapshot = await _sante.EvaluateAsync(composant);

        Assert.True(snapshot.MandatoryFilesPresent);
        Assert.Contains(snapshot.CorruptionIndicators, i => i.Contains("taille nulle"));
    }

    [Fact(DisplayName = "Une catégorie autre qu'ActiveMQ/KahaDB ne contrôle aucun fichier obligatoire")]
    public async Task EvaluateAsync_Categorie_Autre_Ne_Controle_Rien()
    {
        _connecteur.Reponse = new FolderSnapshot { Path = @"\\srv\edi", Exists = true, Files = [] };
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi",
            Category = SharedFolderCategory.EchangeEdi
        });

        var snapshot = await _sante.EvaluateAsync(composant);

        Assert.Null(snapshot.MandatoryFilesPresent);
        Assert.Empty(snapshot.MissingMandatoryFiles);
    }

    // =======================================================================
    // FR-059C — Classification par sous-dossier déclaré
    // =======================================================================
    [Fact(DisplayName = "Un sous-dossier non déclaré reste non classifié, jamais compté comme vide")]
    public async Task EvaluateAsync_Sous_Dossier_Non_Declare_Reste_Null()
    {
        _connecteur.Reponse = new FolderSnapshot { Path = @"\\srv\edi", Exists = true, Files = [] };
        var composant = await CreerComposantAsync(new SharedFolderProfile { RootPath = @"\\srv\edi" });

        var snapshot = await _sante.EvaluateAsync(composant);

        Assert.Null(snapshot.PendingCount);
        Assert.Null(snapshot.ConsumedCount);
        Assert.Null(snapshot.BlockedCount);
        Assert.Null(snapshot.ErrorCount);
    }

    [Fact(DisplayName = "Les sous-dossiers déclarés sont comptés indépendamment les uns des autres")]
    public async Task EvaluateAsync_Compte_Chaque_Sous_Dossier_Declare()
    {
        _connecteur.Reponse = new FolderSnapshot { Path = @"\\srv\edi", Exists = true, Files = [] };
        _connecteur.ReponsesParChemin[@"\\srv\edi\en-attente"] = new FolderSnapshot
        {
            Path = @"\\srv\edi\en-attente", Exists = true,
            Files = [new RemoteFileInfo { Name = "f1.edi", SizeBytes = 10, LastWriteTime = DateTimeOffset.UtcNow.AddHours(-2) }]
        };
        _connecteur.ReponsesParChemin[@"\\srv\edi\ok"] = new FolderSnapshot
        {
            Path = @"\\srv\edi\ok", Exists = true,
            Files =
            [
                new RemoteFileInfo { Name = "f2.edi", SizeBytes = 10, LastWriteTime = DateTimeOffset.UtcNow },
                new RemoteFileInfo { Name = "f3.edi", SizeBytes = 10, LastWriteTime = DateTimeOffset.UtcNow }
            ]
        };

        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi",
            PendingSubfolder = "en-attente",
            ConsumedSubfolder = "ok"
        });

        var snapshot = await _sante.EvaluateAsync(composant);

        Assert.Equal(1, snapshot.PendingCount);
        Assert.Equal(2, snapshot.ConsumedCount);
        Assert.Null(snapshot.BlockedCount);
    }

    [Fact(DisplayName = "Un fichier en attente au-delà du seuil déclaré devient un indice")]
    public async Task EvaluateAsync_Ancienneté_Anormale_Devient_Un_Indice()
    {
        _connecteur.Reponse = new FolderSnapshot { Path = @"\\srv\edi", Exists = true, Files = [] };
        _connecteur.ReponsesParChemin[@"\\srv\edi\en-attente"] = new FolderSnapshot
        {
            Path = @"\\srv\edi\en-attente", Exists = true,
            Files = [new RemoteFileInfo { Name = "vieux.edi", SizeBytes = 10, LastWriteTime = DateTimeOffset.UtcNow.AddHours(-30) }]
        };

        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi",
            PendingSubfolder = "en-attente",
            MaxPendingAgeHours = 24
        });

        var snapshot = await _sante.EvaluateAsync(composant);

        Assert.True(snapshot.OldestPendingAgeHours > 24);
        Assert.Contains(snapshot.CorruptionIndicators, i => i.Contains("au-delà du seuil"));
    }

    // =======================================================================
    // Historique (FR-059C, persistance)
    // =======================================================================
    [Fact(DisplayName = "Chaque relevé est conservé et consultable dans l'historique du composant")]
    public async Task EvaluateAsync_Persiste_Chaque_Releve()
    {
        _connecteur.Reponse = new FolderSnapshot { Path = @"\\srv\edi", Exists = true, Files = [] };
        var composant = await CreerComposantAsync(new SharedFolderProfile { RootPath = @"\\srv\edi" });

        await _sante.EvaluateAsync(composant);
        await _sante.EvaluateAsync(composant);

        var historique = await _sante.GetHistoryAsync(composant.Id);

        Assert.Equal(2, historique.Count);
        Assert.True(historique[0].CapturedAt >= historique[1].CapturedAt);
    }

    // =======================================================================
    // Aides
    // =======================================================================
    private async Task<N4Component> CreerComposantAsync(SharedFolderProfile? sharedFolder)
    {
        await using var db = _factory.CreateDbContext();

        var composant = new N4Component
        {
            EnvironmentId = _envId,
            ServerId = _serverId,
            LogicalName = $"Dossier {Guid.NewGuid():N}"[..20],
            Role = ComponentRole.DossierPartage,
            ControlMode = ControlMode.SuperviseSeulement,
            Status = LifecycleStatus.Valide,
            SharedFolder = sharedFolder ?? new SharedFolderProfile()
        };
        db.Components.Add(composant);
        await db.SaveChangesAsync();

        return (await db.Components
            .Include(c => c.Server)
            .FirstAsync(c => c.Id == composant.Id))!;
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }

    /// <summary>Connecteur qui retourne des relevés programmés, par défaut ou par chemin exact.</summary>
    private sealed class FauxConnecteur : IN4Connector
    {
        public FolderSnapshot Reponse { get; set; } = new() { Path = string.Empty, Exists = false };
        public WriteProbeResult ReponseEcriture { get; set; } = new() { CanWrite = true };
        public Dictionary<string, FolderSnapshot> ReponsesParChemin { get; } = [];

        public Task<ConnectorResult<FolderSnapshot>> ListFilesAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<FolderSnapshot>.Ok(
                ReponsesParChemin.TryGetValue(p, out var r) ? r : Reponse, TimeSpan.Zero));

        public Task<ConnectorResult<WriteProbeResult>> ProbeWriteAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<WriteProbeResult>.Ok(ReponseEcriture, TimeSpan.Zero));

        public Task<ConnectorResult<string>> PingAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<string>.Ok("ok", TimeSpan.Zero));

        public Task<ConnectorResult<ServiceSnapshot>> GetServiceAsync(ConnectorTarget t, string n, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<ServiceSnapshot>.Ok(new ServiceSnapshot { Name = n, Status = "Running" }, TimeSpan.Zero));

        public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> GetServicesAsync(ConnectorTarget t, IReadOnlyCollection<string> n, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<IReadOnlyList<ServiceSnapshot>>.Ok([], TimeSpan.Zero));

        public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> ListServicesAsync(ConnectorTarget t, IReadOnlyCollection<string> m, CancellationToken ct = default) =>
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

        public Task<ConnectorResult<ServiceSnapshot>> ControlServiceAsync(ConnectorTarget t, string n, ServiceControlAction a, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<ServiceSnapshot>.Ok(new ServiceSnapshot { Name = n, Status = "Running" }, TimeSpan.Zero));
    }
}

/// <summary>
/// Tests DIRECTS du connecteur PowerShell (FR-059B) : ListFilesAsync et
/// ProbeWriteAsync contre de VRAIS fichiers, sur la machine qui exécute les
/// tests, déclarée en cible locale — même principe que ServerProbeTests.
/// </summary>
public sealed class PowerShellFolderConnectorTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"n4-dossier-{Guid.NewGuid():N}");
    private readonly PowerShellConnector _connecteur = new(NullLogger<PowerShellConnector>.Instance);

    public PowerShellFolderConnectorTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { }
    }

    [Fact(DisplayName = "ListFilesAsync énumère les vrais fichiers d'un dossier, avec taille et date")]
    public async Task ListFilesAsync_Enumere_Les_Vrais_Fichiers()
    {
        await File.WriteAllTextAsync(Path.Combine(_dossier, "db.data"), "0123456789");

        var r = await _connecteur.ListFilesAsync(ConnectorTarget.Local(), _dossier);

        Assert.True(r.Succeeded, r.Error);
        Assert.True(r.Value!.Exists);
        var fichier = Assert.Single(r.Value.Files);
        Assert.Equal("db.data", fichier.Name);
        Assert.Equal(10, fichier.SizeBytes);
    }

    [Fact(DisplayName = "ListFilesAsync sur un chemin inexistant répond Exists=false, pas une erreur")]
    public async Task ListFilesAsync_Chemin_Inexistant_Repond_Sans_Erreur()
    {
        var r = await _connecteur.ListFilesAsync(ConnectorTarget.Local(), Path.Combine(_dossier, "n-existe-pas"));

        Assert.True(r.Succeeded, r.Error);
        Assert.False(r.Value!.Exists);
    }

    [Fact(DisplayName = "ProbeWriteAsync réussit sur un dossier accessible en écriture et ne laisse aucune trace")]
    public async Task ProbeWriteAsync_Reussit_Et_Ne_Laisse_Rien()
    {
        var r = await _connecteur.ProbeWriteAsync(ConnectorTarget.Local(), _dossier);

        Assert.True(r.Succeeded, r.Error);
        Assert.True(r.Value!.CanWrite, r.Value.Error);
        Assert.Empty(Directory.GetFiles(_dossier));
    }

    [Fact(DisplayName = "ProbeWriteAsync sur un dossier inexistant échoue proprement")]
    public async Task ProbeWriteAsync_Chemin_Inexistant_Echoue_Proprement()
    {
        var r = await _connecteur.ProbeWriteAsync(ConnectorTarget.Local(), Path.Combine(_dossier, "n-existe-pas"));

        Assert.True(r.Succeeded, r.Error);
        Assert.False(r.Value!.CanWrite);
    }
}
