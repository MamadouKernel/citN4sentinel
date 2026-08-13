using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectivity;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests du test unitaire de serveur - REF-05 et REF-07.
///
/// Ils interrogent la machine qui execute les tests, declaree au referentiel
/// comme n'importe quel serveur N4. Le chemin traverse est complet : fabrique
/// de cibles, resolution du compte, connecteur, interrogation des services.
/// Seul le transport differe, la machine locale n'ayant pas besoin de WinRM.
/// </summary>
public sealed class ServerProbeTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _connectionString = string.Empty;
    private string _keyPath = string.Empty;
    private TestDbContextFactory _factory = null!;
    private ServerProbe _probe = null!;
    private Guid _environmentId;
    private Guid _serverId;

    public async Task InitializeAsync()
    {
        _connectionString =
            $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

        var options = new DbContextOptionsBuilder<N4SentinelDbContext>()
            .UseSqlServer(_connectionString)
            .Options;

        _factory = new TestDbContextFactory(options);

        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();

            var env = new N4Environment { Code = "SIM", Name = "Environnement simule" };
            db.Environments.Add(env);
            await db.SaveChangesAsync();
            _environmentId = env.Id;

            // La machine courante, declaree comme n'importe quel serveur N4.
            var serveur = new N4Server
            {
                EnvironmentId = _environmentId,
                HostName = Environment.MachineName
            };
            db.Servers.Add(serveur);
            await db.SaveChangesAsync();
            _serverId = serveur.Id;

            // Un composant dont le service existe reellement, et un autre dont
            // le nom est faux - c'est ce second cas qui justifie le controle.
            db.Components.AddRange(
                new N4Component
                {
                    EnvironmentId = _environmentId, ServerId = _serverId,
                    LogicalName = "Composant valide", Role = ComponentRole.ClusterNode,
                    WindowsServiceName = "Winmgmt", ControlMode = ControlMode.Pilotable, StartOrder = 1
                },
                new N4Component
                {
                    EnvironmentId = _environmentId, ServerId = _serverId,
                    LogicalName = "Composant mal saisi", Role = ComponentRole.BridgeDaemon,
                    WindowsServiceName = "Navis N4 Service Qui N Existe Pas",
                    ControlMode = ControlMode.Pilotable, StartOrder = 2
                });
            await db.SaveChangesAsync();
        }

        _keyPath = Path.Combine(Path.GetTempPath(), $"n4-cles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keyPath);

        var store = new CredentialStore(
            _factory,
            DataProtectionProvider.Create(new DirectoryInfo(_keyPath)),
            NullLogger<CredentialStore>.Instance);

        _probe = new ServerProbe(
            _factory,
            new ConnectorTargetFactory(_factory, store, NullLogger<ConnectorTargetFactory>.Instance),
            new PowerShellConnector(NullLogger<PowerShellConnector>.Instance));
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

    [Fact(DisplayName = "Le serveur repond et annonce sous quelle identite la connexion s'est faite")]
    public async Task Le_Serveur_Repond()
    {
        var r = await _probe.ProbeAsync(_serverId);

        Assert.True(r.Reachable, r.Error);
        Assert.True(r.IsLocal, "La machine courante doit etre traitee en local, sans WinRM.");
        Assert.Equal("identite du processus", r.IdentityDescription);
        Assert.Contains(Environment.MachineName, r.RemoteIdentity!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "L'etat systeme remonte ressources, disques et ecart d'horloge")]
    public async Task L_Etat_Systeme_Est_Collecte()
    {
        var r = await _probe.ProbeAsync(_serverId);

        Assert.NotNull(r.System);
        Assert.NotNull(r.System!.OperatingSystem);
        Assert.NotEmpty(r.System.Disks);
        Assert.True(r.System.TotalMemoryBytes > 0);

        // Contre soi-meme, l'ecart doit etre negligeable. C'est ce controle qui,
        // sur un serveur distant, revele la cause n.1 de statuts DISCONNECTED
        // trompeurs sur N4.
        Assert.NotNull(r.System.ClockSkewSeconds);
        Assert.True(Math.Abs(r.System.ClockSkewSeconds!.Value) < 5);
    }

    [Fact(DisplayName = "Un nom de service errone est detecte a la saisie, pas a l'exploitation")]
    public async Task Le_Nom_De_Service_Errone_Est_Detecte()
    {
        var r = await _probe.ProbeAsync(_serverId);

        Assert.Equal(2, r.Services.Count);
        Assert.Equal(1, r.ServicesFound);
        Assert.Equal(1, r.ServicesMissing);

        var valide = r.Services.Single(s => s.ComponentName == "Composant valide");
        Assert.True(valide.Exists);
        Assert.Equal("Running", valide.Status);

        var errone = r.Services.Single(s => s.ComponentName == "Composant mal saisi");
        Assert.False(errone.Exists);
        Assert.Equal("Introuvable", errone.Status);

        // Le composant fautif est declare pilotable : le probleme est bloquant,
        // parce qu'il ne se manifesterait sinon qu'au premier arret demande.
        Assert.True(r.HasBlockingIssue);
    }

    [Fact(DisplayName = "Un serveur absent du referentiel ne peut pas etre interroge")]
    public async Task Serveur_Hors_Referentiel_Est_Refuse()
    {
        var r = await _probe.ProbeAsync(Guid.NewGuid());

        Assert.False(r.Reachable);
        Assert.Contains("introuvable", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Un compte technique reference mais inexistant fait echouer l'interrogation")]
    public async Task Compte_Manquant_Fait_Echouer()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var s = await db.Servers.SingleAsync(x => x.Id == _serverId);
            s.CredentialReference = "compte-jamais-cree";
            await db.SaveChangesAsync();
        }

        var r = await _probe.ProbeAsync(_serverId);

        // Se rabattre en silence sur l'identite du processus pourrait faire
        // reussir la connexion avec de mauvais droits.
        Assert.False(r.Reachable);
        Assert.Contains("compte-jamais-cree", r.Error!);
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
