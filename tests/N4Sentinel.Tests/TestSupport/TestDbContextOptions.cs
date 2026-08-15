using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Tests;

/// <summary>
/// Program.cs fixe Stores.SchemaVersion = Version3 pour les tables Identity :
/// ce reglage influence la forme du modele EF (cles, longueurs de colonnes).
/// IdentityDbContext le lit via son IServiceProvider interne au moment de
/// OnModelCreating, pas via la chaine de connexion — chaque contexte de test
/// doit donc le reproduire pour construire EXACTEMENT le meme modele que
/// l'application.
///
/// Sans ce reglage partage, deux formes de modele coexistent pour le meme
/// type N4SentinelDbContext dans le meme processus de test (le cache de
/// modele compile d'EF Core est partage par type de contexte, pas par
/// instance), ce qui produit une erreur "model changes each time it is
/// built" des qu'un test appelle MigrateAsync aux cotes des autres suites.
/// </summary>
internal static class TestDbContextOptions
{
    private static readonly IServiceProvider IdentityServiceProvider = new ServiceCollection()
        .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
        .BuildServiceProvider();

    public static DbContextOptionsBuilder<N4SentinelDbContext> Builder(string connectionString) =>
        new DbContextOptionsBuilder<N4SentinelDbContext>()
            .UseSqlServer(connectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .UseApplicationServiceProvider(IdentityServiceProvider);
}
