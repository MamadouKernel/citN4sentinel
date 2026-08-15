using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Tests;

/// <summary>
/// FR-092 : le journal d'audit ne doit pas être modifiable par les
/// opérateurs. Jusqu'au 15/08/2026 cette garantie n'était qu'applicative
/// (AuditEntry n'expose aucune méthode d'update) alors que le commentaire du
/// domaine affirmait une contrainte "côté base" inexistante.
///
/// Ce test s'exécute contre une base migrée avec `MigrateAsync`, PAS
/// `EnsureCreatedAsync` : c'est la seule façon de faire tourner réellement le
/// trigger SQL ajouté par la migration AuditEntriesImmuables, plutôt que le
/// modèle EF Core qui l'ignore.
/// </summary>
public sealed class AuditImmutabiliteTests : IAsyncLifetime
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
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(MasterConnection);
        await master.OpenAsync();
        var cmd = master.CreateCommand();
        cmd.CommandText =
            $"IF DB_ID('{_databaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{_databaseName}]; END";
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact(DisplayName = "Une entree d'audit ne peut pas etre modifiee, meme par un acces direct a la base")]
    public async Task Une_Entree_Ne_Peut_Pas_Etre_Modifiee()
    {
        var writer = new AuditWriter(_factory);
        await writer.WriteAsync(AuditAction.Connexion, AuditOutcome.Succes, "m.konate", "10.0.0.1");

        await using var db = _factory.CreateDbContext();
        var entree = await db.AuditEntries.FirstAsync();

        await using var connexion = new SqlConnection(_connectionString);
        await connexion.OpenAsync();
        var update = connexion.CreateCommand();
        update.CommandText = "UPDATE AuditEntries SET Actor = 'quelqu''un.dautre' WHERE Id = @id";
        update.Parameters.AddWithValue("@id", entree.Id);

        var exception = await Assert.ThrowsAsync<SqlException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("immuable", exception.Message, StringComparison.OrdinalIgnoreCase);

        // La ligne n'a pas bouge : le trigger a bien annule la transaction,
        // pas seulement leve une erreur en laissant la modification passer.
        await using var relecture = _factory.CreateDbContext();
        var apres = await relecture.AuditEntries.FirstAsync(a => a.Id == entree.Id);
        Assert.Equal("m.konate", apres.Actor);
    }

    [Fact(DisplayName = "Une entree d'audit ne peut pas etre supprimee, meme par un acces direct a la base")]
    public async Task Une_Entree_Ne_Peut_Pas_Etre_Supprimee()
    {
        var writer = new AuditWriter(_factory);
        await writer.WriteAsync(AuditAction.TentativeNonAutorisee, AuditOutcome.Refuse, "inconnu", "10.0.0.2");

        await using var db = _factory.CreateDbContext();
        var entree = await db.AuditEntries.FirstAsync();

        await using var connexion = new SqlConnection(_connectionString);
        await connexion.OpenAsync();
        var suppression = connexion.CreateCommand();
        suppression.CommandText = "DELETE FROM AuditEntries WHERE Id = @id";
        suppression.Parameters.AddWithValue("@id", entree.Id);

        await Assert.ThrowsAsync<SqlException>(() => suppression.ExecuteNonQueryAsync());

        await using var relecture = _factory.CreateDbContext();
        Assert.True(await relecture.AuditEntries.AnyAsync(a => a.Id == entree.Id));
    }

    [Fact(DisplayName = "Une nouvelle entree d'audit s'insere normalement malgre le trigger")]
    public async Task Une_Nouvelle_Entree_S_Insere_Normalement()
    {
        var writer = new AuditWriter(_factory);
        await writer.WriteAsync(AuditAction.ExecutionOperation, AuditOutcome.Succes, "m.konate", null,
            entityType: nameof(WorkflowExecution), reason: "Test");

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.AuditEntries.CountAsync());
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
