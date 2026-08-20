using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Referential;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// Duplication d'un environnement — monter UAT à partir de PROD.
///
/// LE POINT QUI COMPTE EST LE NOM D'HÔTE. Copier PROD à l'identique
/// produirait un environnement nommé « UAT » dont les fiches pointent les
/// machines de PRODUCTION. Un arrêt complet lancé dessus arrêterait la
/// production, et le bandeau à l'écran dirait UAT.
///
/// La majorité des tests ci-dessous vérifient donc ce que la copie NE
/// transporte PAS.
/// </summary>
public sealed class DuplicationEnvironnementTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private TestDbContextFactory _factory = null!;
    private EnvironmentDuplicationService _duplication = null!;

    private Guid _sourceId;
    private Guid _bridgeId;
    private Guid _centerId;

    public async Task InitializeAsync()
    {
        TestConnectionHelper.SkipIfUnavailable();
        var cs = TestConnectionHelper.BuildDatabaseConnectionString(_databaseName);
        _factory = new TestDbContextFactory(TestDbContextOptions.Builder(cs).Options);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        var prod = new N4Environment
        {
            Code = "PRD",
            Name = "Production",
            Kind = EnvironmentKind.Production,
            Status = LifecycleStatus.Actif,
            Criticality = CriticalityLevel.Critique,
            TimeZoneId = "Africa/Abidjan",
            ClockToleranceSeconds = 1
        };
        db.Environments.Add(prod);
        await db.SaveChangesAsync();
        _sourceId = prod.Id;

        var serveur = new N4Server
        {
            EnvironmentId = prod.Id,
            HostName = "CIT-N4-PROD01",
            Status = LifecycleStatus.Actif,
            CredentialReference = "compte-prod-tres-sensible"
        };
        db.Servers.Add(serveur);
        await db.SaveChangesAsync();

        var center = new N4Component
        {
            EnvironmentId = prod.Id,
            ServerId = serveur.Id,
            LogicalName = "Center",
            Role = ComponentRole.CenterNode,
            WindowsServiceName = "Navis N4 Center",
            ControlMode = ControlMode.Pilotable,
            Status = LifecycleStatus.Valide,
            Readiness = new ReadinessProfile
            {
                LogPath = @"D:\navis\logs\navis-apex.log",
                ReadyPatterns = ["Web tier servlet 'action' initialized"]
            }
        };

        var bridge = new N4Component
        {
            EnvironmentId = prod.Id,
            ServerId = serveur.Id,
            LogicalName = "Bridge",
            Role = ComponentRole.BridgeDaemon,
            WindowsServiceName = "Navis N4 Bridge",
            ControlMode = ControlMode.Pilotable,
            Status = LifecycleStatus.Valide
        };

        db.Components.AddRange(center, bridge);
        await db.SaveChangesAsync();
        _centerId = center.Id;
        _bridgeId = bridge.Id;

        db.ComponentDependencies.Add(new ComponentDependency
        {
            ComponentId = bridge.Id,
            DependsOnComponentId = center.Id,
            Kind = DependencyKind.RequisAuDemarrage
        });
        await db.SaveChangesAsync();

        _duplication = new EnvironmentDuplicationService(
            _factory, new AuditWriter(_factory),
            NullLogger<EnvironmentDuplicationService>.Instance);
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

    [SkippableFact(DisplayName = "AUCUN nom d'hôte réel ne passe dans la copie")]
    public async Task Aucun_Nom_D_Hote_Reel_N_Est_Copie()
    {
        var r = await _duplication.DupliquerAsync(
            _sourceId, "UAT", "Recette", EnvironmentKind.UAT, "m.konate");

        Assert.True(r.Succeeded, r.Erreur);

        await using var db = _factory.CreateDbContext();
        var serveurs = await db.Servers.Where(s => s.EnvironmentId == r.EnvironmentId).ToListAsync();

        Assert.Single(serveurs);

        // Le point critique : le nom d'hôte de production ne doit pas être
        // utilisable tel quel dans le nouvel environnement.
        Assert.NotEqual("CIT-N4-PROD01", serveurs[0].HostName);
        Assert.StartsWith(EnvironmentDuplicationService.PrefixeAResoudre, serveurs[0].HostName);

        // Il reste lisible dans la description, pour savoir de quoi c'est la copie.
        Assert.Contains("CIT-N4-PROD01", serveurs[0].Description!);
    }

    [SkippableFact(DisplayName = "La copie naît en brouillon, donc inexploitable telle quelle")]
    public async Task La_Copie_Nait_En_Brouillon()
    {
        var r = await _duplication.DupliquerAsync(
            _sourceId, "UAT", "Recette", EnvironmentKind.UAT, "m.konate");

        await using var db = _factory.CreateDbContext();

        var env = await db.Environments.FirstAsync(e => e.Id == r.EnvironmentId);
        Assert.Equal(LifecycleStatus.Brouillon, env.Status);

        // Un environnement en brouillon est refusé au lancement : c'est ce qui
        // garantit qu'une copie non relue ne peut rien déclencher.
        Assert.All(await db.Servers.Where(s => s.EnvironmentId == r.EnvironmentId).ToListAsync(),
            s => Assert.Equal(LifecycleStatus.Brouillon, s.Status));

        Assert.All(await db.Components.Where(c => c.EnvironmentId == r.EnvironmentId).ToListAsync(),
            c => Assert.Equal(LifecycleStatus.Brouillon, c.Status));
    }

    [SkippableFact(DisplayName = "Le compte technique n'est pas copié")]
    public async Task La_Reference_De_Compte_Technique_N_Est_Pas_Copiee()
    {
        var r = await _duplication.DupliquerAsync(
            _sourceId, "UAT", "Recette", EnvironmentKind.UAT, "m.konate");

        await using var db = _factory.CreateDbContext();
        var serveur = await db.Servers.FirstAsync(s => s.EnvironmentId == r.EnvironmentId);

        // Les comptes techniques sont propres à un environnement : transporter
        // la référence de PROD dans UAT donnerait à UAT les accès de PROD.
        Assert.Null(serveur.CredentialReference);
    }

    [SkippableFact(DisplayName = "Composants, profils de démarrage et dépendances sont repris")]
    public async Task Ce_Qui_Fait_Gagner_Du_Temps_Est_Bien_Repris()
    {
        var r = await _duplication.DupliquerAsync(
            _sourceId, "UAT", "Recette", EnvironmentKind.UAT, "m.konate");

        Assert.Equal(2, r.Composants);
        Assert.Equal(1, r.Dependances);

        await using var db = _factory.CreateDbContext();

        var center = await db.Components
            .FirstAsync(c => c.EnvironmentId == r.EnvironmentId && c.LogicalName == "Center");

        Assert.Equal("Navis N4 Center", center.WindowsServiceName);
        Assert.Equal(@"D:\navis\logs\navis-apex.log", center.Readiness!.LogPath);
        Assert.Contains("Web tier servlet 'action' initialized", center.Readiness.ReadyPatterns);

        // La dépendance doit pointer les NOUVEAUX composants, pas ceux de la
        // source : une copie qui garderait les anciens identifiants relierait
        // silencieusement les deux environnements.
        var dep = await db.ComponentDependencies
            .Include(d => d.Component)
            .FirstAsync(d => d.Component!.EnvironmentId == r.EnvironmentId);

        Assert.NotEqual(_bridgeId, dep.ComponentId);
        Assert.NotEqual(_centerId, dep.DependsOnComponentId);

        var depuis = await db.Components.FirstAsync(c => c.Id == dep.ComponentId);
        var vers = await db.Components.FirstAsync(c => c.Id == dep.DependsOnComponentId);
        Assert.Equal("Bridge", depuis.LogicalName);
        Assert.Equal("Center", vers.LogicalName);
    }

    [SkippableFact(DisplayName = "Un code déjà utilisé est refusé")]
    public async Task Un_Code_Deja_Pris_Est_Refuse()
    {
        var r = await _duplication.DupliquerAsync(
            _sourceId, "PRD", "Doublon", EnvironmentKind.UAT, "m.konate");

        Assert.False(r.Succeeded);
        Assert.Contains("PRD", r.Erreur!);
    }

    [SkippableFact(DisplayName = "Une source inexistante est refusée sans rien créer")]
    public async Task Une_Source_Inexistante_Ne_Cree_Rien()
    {
        await using var db = _factory.CreateDbContext();
        var avant = await db.Environments.CountAsync();

        var r = await _duplication.DupliquerAsync(
            Guid.NewGuid(), "UAT", "Recette", EnvironmentKind.UAT, "m.konate");

        Assert.False(r.Succeeded);
        Assert.Equal(avant, await db.Environments.CountAsync());
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
