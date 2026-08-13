using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests du jeu de démonstration - SIM-02.
///
/// Ce jeu sert à découvrir l'application sans rien saisir. Il doit donc être
/// exact : un jeu de démonstration qui déclarerait de mauvaises dépendances
/// enseignerait une séquence fausse à qui l'utilise pour comprendre le produit.
/// </summary>
public sealed class DemonstrationTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private TestDbContextFactory _factory = null!;
    private DemonstrationSeeder _seeder = null!;

    public async Task InitializeAsync()
    {
        var cs = $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<N4SentinelDbContext>().UseSqlServer(cs).Options);
        _seeder = new DemonstrationSeeder(_factory, NullLogger<DemonstrationSeeder>.Instance);

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

    [Fact(DisplayName = "Le jeu cree huit composants et six dependances")]
    public async Task Le_Jeu_Est_Complet()
    {
        var id = await _seeder.SeedAsync();
        Assert.NotNull(id);

        await using var db = _factory.CreateDbContext();

        // Huit : deux noeuds Cluster, Center, Standby, Bridge, XPS, ECN4, ECN4Web.
        Assert.Equal(8, await db.Components.CountAsync());
        Assert.Equal(6, await db.ComponentDependencies.CountAsync());
        Assert.Equal(1, await db.Servers.CountAsync());
    }

    [Fact(DisplayName = "L'environnement nait en Brouillon et de type Test")]
    public async Task L_Environnement_Ne_Peut_Rien_Piloter()
    {
        await _seeder.SeedAsync();

        await using var db = _factory.CreateDbContext();
        var env = await db.Environments.SingleAsync();

        // Aucune operation ne doit etre possible sans passer par le cycle de
        // validation, jeu de demonstration compris.
        Assert.Equal(LifecycleStatus.Brouillon, env.Status);
        Assert.False(env.IsOperable);

        // De type Test : il ne peut pas etre confondu avec une Production.
        Assert.Equal(EnvironmentKind.Test, env.Kind);
        Assert.False(env.IsProduction);
    }

    [Fact(DisplayName = "Chaque composant pilotable porte un marqueur exploitable")]
    public async Task Tous_Les_Composants_Sont_Prouvables()
    {
        await _seeder.SeedAsync();

        await using var db = _factory.CreateDbContext();
        var composants = await db.Components.ToListAsync();

        Assert.All(composants, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.WindowsServiceName));
            Assert.True(c.Readiness.IsProvable,
                $"{c.LogicalName} n'a pas de preuve de démarrage exploitable.");
        });
    }

    [Fact(DisplayName = "L'ordre de demarrage respecte la contrainte N4")]
    public async Task L_Ordre_Respecte_La_Sequence_N4()
    {
        await _seeder.SeedAsync();

        await using var db = _factory.CreateDbContext();
        var parNom = await db.Components.ToDictionaryAsync(c => c.LogicalName, c => c.StartOrder);

        // Les noeuds Cluster avant le Center, le Bridge avant XPS, XPS avant ECN4.
        Assert.True(parNom["Cluster Node 1"] < parNom["Cluster Node 2"]);
        Assert.True(parNom["Cluster Node 2"] < parNom["Center Node"]);
        Assert.True(parNom["Center Node"] < parNom["XPS Bridge Daemon"]);
        Assert.True(parNom["XPS Bridge Daemon"] < parNom["Service XPS"]);
        Assert.True(parNom["Service XPS"] < parNom["ECN4 Daemon"]);
        Assert.True(parNom["ECN4 Daemon"] < parNom["ECN4Web"]);
    }

    [Fact(DisplayName = "XPS depend du Bridge : la dependance la plus importante de l'ecosysteme")]
    public async Task XPS_Depend_Du_Bridge()
    {
        await _seeder.SeedAsync();

        await using var db = _factory.CreateDbContext();
        var xps = await db.Components.SingleAsync(c => c.LogicalName == "Service XPS");
        var bridge = await db.Components.SingleAsync(c => c.LogicalName == "XPS Bridge Daemon");

        Assert.True(await db.ComponentDependencies.AnyAsync(
            d => d.ComponentId == xps.Id && d.DependsOnComponentId == bridge.Id));
    }

    [Fact(DisplayName = "Le Standby n'attend pas le marqueur du tier web, contrairement au Center")]
    public async Task Le_Standby_A_Son_Propre_Marqueur()
    {
        await _seeder.SeedAsync();

        await using var db = _factory.CreateDbContext();
        var standby = await db.Components.SingleAsync(c => c.LogicalName == "Standby Center Node");
        var center = await db.Components.SingleAsync(c => c.LogicalName == "Center Node");

        // Une instance en veille n'ecrit PAS le marqueur du tier web : c'est le
        // comportement normal, pas un defaut. Leur donner le meme marqueur
        // ferait attendre indefiniment le Standby.
        Assert.NotEqual(center.Readiness.ReadyPatterns[0], standby.Readiness.ReadyPatterns[0]);
        Assert.Contains("standby", standby.Readiness.ReadyPatterns[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Le journal XPS est declare avec un caractere generique")]
    public async Task Le_Journal_XPS_Est_Generique()
    {
        await _seeder.SeedAsync();

        await using var db = _factory.CreateDbContext();
        var xps = await db.Components.SingleAsync(c => c.LogicalName == "Service XPS");

        // Le journal XPS est horodate dans son nom et recree a chaque
        // demarrage : sans generique, le chemin serait perime des le premier
        // redemarrage.
        Assert.Contains("*", xps.Readiness.LogPath!);
    }

    [Fact(DisplayName = "Un second appel ne duplique rien")]
    public async Task Le_Jeu_N_Est_Pas_Duplique()
    {
        await _seeder.SeedAsync();
        var second = await _seeder.SeedAsync();

        Assert.Null(second);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Environments.CountAsync());
        Assert.Equal(8, await db.Components.CountAsync());
    }

    [Fact(DisplayName = "La suppression retire tout, dependances comprises")]
    public async Task La_Suppression_Est_Complete()
    {
        await _seeder.SeedAsync();
        Assert.True(await _seeder.RemoveAsync());

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Environments.CountAsync());
        Assert.Equal(0, await db.Components.CountAsync());
        Assert.Equal(0, await db.ComponentDependencies.CountAsync());
        Assert.Equal(0, await db.Servers.CountAsync());
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
