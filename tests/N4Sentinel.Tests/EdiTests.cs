using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Edi;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests du suivi EDI — FR-059H à FR-059J.
///
/// UN FICHIER GARDE SON IDENTITÉ. Contrairement au relevé agrégé de la
/// Phase D, chaque fichier EDI reste le même enregistrement d'un relevé à
/// l'autre : c'est ce qui permet de dire QU'IL a été intégré, pas seulement
/// que le compte a changé. Ces tests protègent cette continuité, et le fait
/// que partenaire/type de message restent null sans motif déclaré.
/// </summary>
public sealed class EdiTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _keyPath = string.Empty;
    private TestDbContextFactory _factory = null!;
    private EdiTrackingService _suivi = null!;
    private AlertService _alertes = null!;
    private FauxConnecteur _connecteur = null!;

    private Guid _envId;
    private Guid _serverId;

    public async Task InitializeAsync()
    {
        TestConnectionHelper.SkipIfUnavailable();
        var cs = TestConnectionHelper.BuildDatabaseConnectionString(_databaseName);

        _factory = new TestDbContextFactory(TestDbContextOptions.Builder(cs).Options);

        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();

            var env = new N4Environment { Code = "UAT", Name = "Recette", Kind = EnvironmentKind.UAT };
            db.Environments.Add(env);
            await db.SaveChangesAsync();
            _envId = env.Id;

            var serveur = new N4Server { EnvironmentId = _envId, HostName = "N4EDISRV01" };
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

        _suivi = new EdiTrackingService(
            _factory,
            new ConnectorTargetFactory(_factory, store, NullLogger<ConnectorTargetFactory>.Instance),
            _connecteur,
            NullLogger<EdiTrackingService>.Instance);

        _alertes = new AlertService(_factory, NullLogger<AlertService>.Instance);
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
    // FR-059H — Suivi individuel des fichiers
    // =======================================================================
    [Fact(DisplayName = "Un fichier trouvé dans le sous-dossier en attente est suivi en état En attente")]
    public async Task SyncAsync_Fichier_En_Attente_Est_Suivi()
    {
        _connecteur.Chemins[@"\\srv\edi\recu"] = Dossier(("cmd001.edi", DateTimeOffset.UtcNow));

        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi", PendingSubfolder = "recu"
        });

        var fichiers = await _suivi.SyncAsync(composant);

        var f = Assert.Single(fichiers);
        Assert.Equal("cmd001.edi", f.FileName);
        Assert.Equal(EdiFileStatus.EnAttente, f.Status);
        Assert.Null(f.IntegratedAt);
    }

    [Fact(DisplayName = "Un fichier qui passe du dossier en attente au dossier consommé garde son identité")]
    public async Task SyncAsync_Transition_Vers_Integre_Garde_L_Identite()
    {
        _connecteur.Chemins[@"\\srv\edi\recu"] = Dossier(("cmd001.edi", DateTimeOffset.UtcNow));
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi", PendingSubfolder = "recu", ConsumedSubfolder = "ok"
        });

        var premierReleve = await _suivi.SyncAsync(composant);
        var idOriginal = Assert.Single(premierReleve).Id;

        // Le fichier a ete deplace : plus dans "recu", desormais dans "ok".
        _connecteur.Chemins[@"\\srv\edi\recu"] = Dossier();
        _connecteur.Chemins[@"\\srv\edi\ok"] = Dossier(("cmd001.edi", DateTimeOffset.UtcNow));

        var secondReleve = await _suivi.SyncAsync(composant);

        var f = Assert.Single(secondReleve);
        Assert.Equal(idOriginal, f.Id);
        Assert.Equal(EdiFileStatus.Integre, f.Status);
        Assert.NotNull(f.IntegratedAt);
    }

    [Fact(DisplayName = "Sans motif déclaré, partenaire et type de message restent non classifiés")]
    public async Task SyncAsync_Sans_Motif_Ne_Classifie_Rien()
    {
        _connecteur.Chemins[@"\\srv\edi\recu"] = Dossier(("PARTNERA_ORDER_001.edi", DateTimeOffset.UtcNow));
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi", PendingSubfolder = "recu"
        });

        var fichiers = await _suivi.SyncAsync(composant);

        var f = Assert.Single(fichiers);
        Assert.Null(f.Partner);
        Assert.Null(f.MessageType);
    }

    [Fact(DisplayName = "Avec un motif déclaré, partenaire et type de message sont extraits du nom de fichier")]
    public async Task SyncAsync_Avec_Motif_Extrait_Partenaire_Et_Type()
    {
        _connecteur.Chemins[@"\\srv\edi\recu"] = Dossier(("PARTNERA_ORDER_001.edi", DateTimeOffset.UtcNow));
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi",
            PendingSubfolder = "recu",
            EdiFileNamingPattern = @"^(?<partner>[A-Z]+)_(?<type>[A-Z]+)_\d+\.edi$"
        });

        var fichiers = await _suivi.SyncAsync(composant);

        var f = Assert.Single(fichiers);
        Assert.Equal("PARTNERA", f.Partner);
        Assert.Equal("ORDER", f.MessageType);
    }

    [Fact(DisplayName = "Un fichier vu plusieurs fois en rejet accumule ses rejets consécutifs")]
    public async Task SyncAsync_Rejets_Consecutifs_S_Accumulent()
    {
        _connecteur.Chemins[@"\\srv\edi\err"] = Dossier(("bad.edi", DateTimeOffset.UtcNow));
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi", ErrorSubfolder = "err"
        });

        await _suivi.SyncAsync(composant);
        var fichiers = await _suivi.SyncAsync(composant);

        var f = Assert.Single(fichiers);
        Assert.Equal(EdiFileStatus.Rejete, f.Status);
        Assert.Equal(2, f.ConsecutiveRejections);
    }

    // =======================================================================
    // FR-059I — Alertes EDI
    // =======================================================================
    [Fact(DisplayName = "Sans seuil déclaré, aucune alerte de fichier non consommé n'est levée")]
    public async Task EvaluateEdiAsync_Sans_Seuil_Ne_Leve_Rien()
    {
        var composant = await CreerComposantAsync(new SharedFolderProfile { RootPath = @"\\srv\edi" });
        var fichiers = new List<EdiFile>
        {
            new() { ComponentId = composant.Id, FileName = "vieux.edi", Status = EdiFileStatus.EnAttente,
                    FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-10) }
        };

        await _alertes.EvaluateEdiAsync(composant, fichiers);

        var ouvertes = await _alertes.GetOpenAsync(_envId);
        Assert.Empty(ouvertes);
    }

    [Fact(DisplayName = "Un fichier en attente au-delà du seuil déclaré ouvre une alerte")]
    public async Task EvaluateEdiAsync_Fichier_En_Retard_Ouvre_Une_Alerte()
    {
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi", MaxPendingAgeHours = 24
        });
        var fichiers = new List<EdiFile>
        {
            new() { ComponentId = composant.Id, FileName = "vieux.edi", Status = EdiFileStatus.EnAttente,
                    FirstSeenAt = DateTimeOffset.UtcNow.AddHours(-48) }
        };

        await _alertes.EvaluateEdiAsync(composant, fichiers);

        var ouvertes = await _alertes.GetOpenAsync(_envId);
        var alerte = Assert.Single(ouvertes);
        Assert.Equal(AlertKind.FichierEdiNonConsomme, alerte.Kind);
        Assert.Contains("vieux.edi", alerte.Detail);
    }

    [Fact(DisplayName = "Le fichier redevenu à jour résout l'alerte automatiquement")]
    public async Task EvaluateEdiAsync_Resout_Automatiquement()
    {
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi", MaxPendingAgeHours = 24
        });
        var enRetard = new List<EdiFile>
        {
            new() { ComponentId = composant.Id, FileName = "vieux.edi", Status = EdiFileStatus.EnAttente,
                    FirstSeenAt = DateTimeOffset.UtcNow.AddHours(-48) }
        };
        await _alertes.EvaluateEdiAsync(composant, enRetard);
        Assert.Single(await _alertes.GetOpenAsync(_envId));

        // Le fichier a ete integre entre-temps : plus rien en attente.
        await _alertes.EvaluateEdiAsync(composant, []);

        Assert.Empty(await _alertes.GetOpenAsync(_envId));
    }

    [Fact(DisplayName = "Aucune intégration jamais observée, avec des fichiers présents, ouvre une alerte critique")]
    public async Task EvaluateEdiAsync_Aucune_Integration_Jamais_Ouvre_Une_Alerte()
    {
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi", MaxHoursSinceLastIntegration = 12
        });
        var fichiers = new List<EdiFile>
        {
            new() { ComponentId = composant.Id, FileName = "a.edi", Status = EdiFileStatus.EnAttente }
        };

        await _alertes.EvaluateEdiAsync(composant, fichiers);

        var alerte = Assert.Single(await _alertes.GetOpenAsync(_envId));
        Assert.Equal(AlertKind.AucuneIntegrationEdiRecente, alerte.Kind);
        Assert.Equal(AlertSeverity.Critique, alerte.Severity);
    }

    [Fact(DisplayName = "Une intégration récente évite l'alerte d'absence d'intégration")]
    public async Task EvaluateEdiAsync_Integration_Recente_N_Ouvre_Rien()
    {
        var composant = await CreerComposantAsync(new SharedFolderProfile
        {
            RootPath = @"\\srv\edi", MaxHoursSinceLastIntegration = 12
        });
        var fichiers = new List<EdiFile>
        {
            new() { ComponentId = composant.Id, FileName = "a.edi", Status = EdiFileStatus.Integre,
                    IntegratedAt = DateTimeOffset.UtcNow.AddHours(-1) }
        };

        await _alertes.EvaluateEdiAsync(composant, fichiers);

        Assert.Empty(await _alertes.GetOpenAsync(_envId));
    }

    [Fact(DisplayName = "Un fichier en échec trois fois de suite ouvre une alerte, même sans dépasser l'ancienneté (FR-059I)")]
    public async Task EvaluateEdiAsync_Echecs_Repetes_Ouvre_Une_Alerte()
    {
        var composant = await CreerComposantAsync(new SharedFolderProfile { RootPath = @"\\srv\edi" });
        var fichiers = new List<EdiFile>
        {
            new() { ComponentId = composant.Id, FileName = "recurrent.edi", Status = EdiFileStatus.Rejete,
                    Partner = "TERM-B", ConsecutiveRejections = 3,
                    FirstSeenAt = DateTimeOffset.UtcNow.AddMinutes(-30) }
        };

        await _alertes.EvaluateEdiAsync(composant, fichiers);

        var alerte = Assert.Single(await _alertes.GetOpenAsync(_envId));
        Assert.Equal(AlertKind.EchecsEdiRepetes, alerte.Kind);
        Assert.Contains("recurrent.edi", alerte.Detail);
        Assert.Contains("TERM-B", alerte.Detail);
    }

    [Fact(DisplayName = "Un fichier rejeté une ou deux fois ne déclenche pas l'alerte d'échecs répétés")]
    public async Task EvaluateEdiAsync_Peu_D_Echecs_N_Ouvre_Rien()
    {
        var composant = await CreerComposantAsync(new SharedFolderProfile { RootPath = @"\\srv\edi" });
        var fichiers = new List<EdiFile>
        {
            new() { ComponentId = composant.Id, FileName = "b.edi", Status = EdiFileStatus.Rejete,
                    ConsecutiveRejections = 2, FirstSeenAt = DateTimeOffset.UtcNow.AddMinutes(-5) }
        };

        await _alertes.EvaluateEdiAsync(composant, fichiers);

        Assert.DoesNotContain(await _alertes.GetOpenAsync(_envId), a => a.Kind == AlertKind.EchecsEdiRepetes);
    }

    [Fact(DisplayName = "Un fichier qui repasse sous le seuil résout l'alerte d'échecs répétés")]
    public async Task EvaluateEdiAsync_Echecs_Repetes_Se_Resout()
    {
        var composant = await CreerComposantAsync(new SharedFolderProfile { RootPath = @"\\srv\edi" });
        var enEchec = new List<EdiFile>
        {
            new() { ComponentId = composant.Id, FileName = "recurrent.edi", Status = EdiFileStatus.Rejete,
                    ConsecutiveRejections = 4, FirstSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10) }
        };
        await _alertes.EvaluateEdiAsync(composant, enEchec);
        Assert.Single(await _alertes.GetOpenAsync(_envId));

        // Le fichier a fini par etre integre : la piste est revenue a zero.
        await _alertes.EvaluateEdiAsync(composant, []);

        Assert.Empty(await _alertes.GetOpenAsync(_envId));
    }

    // =======================================================================
    // Aides
    // =======================================================================
    private static FolderSnapshot Dossier(params (string Nom, DateTimeOffset Date)[] fichiers) => new()
    {
        Path = "test",
        Exists = true,
        Files = fichiers.Select(f => new RemoteFileInfo { Name = f.Nom, SizeBytes = 10, LastWriteTime = f.Date }).ToList()
    };

    private async Task<N4Component> CreerComposantAsync(SharedFolderProfile sharedFolder)
    {
        await using var db = _factory.CreateDbContext();

        var composant = new N4Component
        {
            EnvironmentId = _envId,
            ServerId = _serverId,
            LogicalName = $"EDI {Guid.NewGuid():N}"[..15],
            Role = ComponentRole.InterfaceEdi,
            ControlMode = ControlMode.SuperviseSeulement,
            Status = LifecycleStatus.Valide,
            SharedFolder = sharedFolder
        };
        db.Components.Add(composant);
        await db.SaveChangesAsync();

        return (await db.Components.Include(c => c.Server).FirstAsync(c => c.Id == composant.Id))!;
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }

    /// <summary>Connecteur qui retourne des relevés programmés par chemin exact.</summary>
    private sealed class FauxConnecteur : IN4Connector
    {
        public Dictionary<string, FolderSnapshot> Chemins { get; } = [];

        public Task<ConnectorResult<FolderSnapshot>> ListFilesAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<FolderSnapshot>.Ok(
                Chemins.TryGetValue(p, out var r) ? r : new FolderSnapshot { Path = p, Exists = false }, TimeSpan.Zero));

        public Task<ConnectorResult<WriteProbeResult>> ProbeWriteAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(ConnectorResult<WriteProbeResult>.Ok(new WriteProbeResult { CanWrite = true }, TimeSpan.Zero));

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
