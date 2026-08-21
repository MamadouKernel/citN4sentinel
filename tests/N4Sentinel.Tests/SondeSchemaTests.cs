using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Tests;

/// <summary>
/// Contrôle de santé du schéma.
///
/// Il remplace `AddDbContextCheck`, qui se contentait d'un `CanConnect`. Sur
/// SQLite, ouvrir un fichier réussit TOUJOURS : l'application répondait donc
/// « Healthy » avec une base sans la moindre table, et le script de mise en
/// service déclarait le déploiement réussi.
///
/// Ces tests vérifient les deux versants : qu'il refuse une base sans schéma,
/// et qu'il accepte une base à jour. Un contrôle qui refuserait tout serait
/// aussi inutile qu'un contrôle qui accepte tout.
/// </summary>
public sealed class SondeSchemaTests : IDisposable
{
    private readonly string _fichier =
        Path.Combine(Path.GetTempPath(), $"n4-sonde-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_fichier)) File.Delete(_fichier); } catch { /* verrou résiduel */ }
    }

    private Fabrique Factory() => new(new DbContextOptionsBuilder<N4SentinelDbContext>()
        .UseSqlite($"Data Source={_fichier}",
            s => s.MigrationsAssembly("N4Sentinel.Migrations.Sqlite"))
        .ConfigureWarnings(w => w.Ignore(
            Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
        .Options);

    private static Task<HealthCheckResult> Interroger(Fabrique f) =>
        new SchemaHealthCheck(f).CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration(
                    SchemaHealthCheck.Nom, _ => null!, HealthStatus.Unhealthy, null)
            });

    [Fact(DisplayName = "Une base sans schéma est déclarée EN MAUVAISE SANTÉ")]
    public async Task Une_Base_Sans_Schema_Est_Refusee()
    {
        var fabrique = Factory();

        // Le fichier est créé mais aucune migration n'est appliquée : c'est
        // exactement l'état qu'un CanConnect déclarait sain.
        await using (var db = fabrique.CreateDbContext())
            await db.Database.OpenConnectionAsync();

        var r = await Interroger(fabrique);

        Assert.Equal(HealthStatus.Unhealthy, r.Status);
        Assert.Contains("migration", r.Description!, StringComparison.OrdinalIgnoreCase);

        // Le motif doit dire quoi faire, pas seulement que ça ne va pas.
        Assert.Contains("redémarrez", r.Description!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Une base à jour est déclarée en bonne santé")]
    public async Task Une_Base_A_Jour_Est_Acceptee()
    {
        var fabrique = Factory();

        await using (var db = fabrique.CreateDbContext())
            await db.Database.MigrateAsync();

        var r = await Interroger(fabrique);

        Assert.Equal(HealthStatus.Healthy, r.Status);
        Assert.Contains("à jour", r.Description!);
    }

    [Fact(DisplayName = "Une base injoignable est dite injoignable, sans exception qui remonte")]
    public async Task Une_Base_Injoignable_Ne_Fait_Pas_Remonter_D_Exception()
    {
        // Chemin dans un dossier inexistant : l'ouverture échoue.
        var fabrique = new Fabrique(new DbContextOptionsBuilder<N4SentinelDbContext>()
            .UseSqlite(@"Data Source=Z:\dossier-inexistant\n4.db")
            .Options);

        var r = await Interroger(fabrique);

        // La sonde doit répondre, pas exploser : une exception non rattrapée
        // ferait remonter une erreur 500 au lieu d'un statut exploitable.
        Assert.Equal(HealthStatus.Unhealthy, r.Status);
        Assert.False(string.IsNullOrWhiteSpace(r.Description));
    }

    private sealed class Fabrique(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
