using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Referential;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// Import CSV du référentiel.
///
/// La propriété qui compte : L'APERÇU NE DOIT PAS MENTIR. Il passe par le
/// même code que l'écriture, et ces tests vérifient qu'il annonce exactement
/// ce qui sera fait — sans rien écrire.
/// </summary>
public sealed class ImportCsvReferentielTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private TestDbContextFactory _factory = null!;
    private ReferentialCsvImporter _import = null!;
    private Guid _envId;

    public async Task InitializeAsync()
    {
        TestConnectionHelper.SkipIfUnavailable();
        var cs = TestConnectionHelper.BuildDatabaseConnectionString(_databaseName);
        _factory = new TestDbContextFactory(TestDbContextOptions.Builder(cs).Options);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        var env = new N4Environment
        {
            Code = "UAT",
            Name = "Recette",
            Kind = EnvironmentKind.UAT,
            Status = LifecycleStatus.Actif
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync();
        _envId = env.Id;

        _import = new ReferentialCsvImporter(
            _factory, new AuditWriter(_factory),
            NullLogger<ReferentialCsvImporter>.Instance);
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

    private const string CsvValide = """
        hote;composant;role;service;ordre;chemin_journal;marqueur
        SRV-01;Center;CenterNode;Navis N4 Center;1;D:\navis\logs\apex.log;servlet initialized
        SRV-01;Bridge;BridgeDaemon;Navis N4 Bridge;2;;
        SRV-02;XPS;Xps;Navis N4 XPS;3;;
        """;

    [SkippableFact(DisplayName = "L'aperçu n'écrit rien")]
    public async Task L_Apercu_N_Ecrit_Rien()
    {
        var apercu = await _import.ApercuAsync(_envId, CsvValide);

        Assert.True(apercu.Succeeded);
        Assert.Equal(3, apercu.Crees);
        Assert.False(apercu.Applique);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Components.CountAsync());
        Assert.Equal(0, await db.Servers.CountAsync());
    }

    [SkippableFact(DisplayName = "L'aperçu annonce exactement ce que l'import fait")]
    public async Task L_Apercu_Annonce_Ce_Qui_Sera_Fait()
    {
        var apercu = await _import.ApercuAsync(_envId, CsvValide);
        var reel = await _import.ImporterAsync(_envId, CsvValide, "m.konate");

        // Le point qui compte : un aperçu calculé par un autre chemin finirait
        // par diverger de l'écriture, et fabriquerait un faux sentiment de
        // contrôle.
        Assert.Equal(apercu.Crees, reel.Crees);
        Assert.Equal(apercu.Refuses, reel.Refuses);
        Assert.Equal(apercu.Ignores, reel.Ignores);
    }

    [SkippableFact(DisplayName = "L'import crée composants et serveurs manquants, en brouillon")]
    public async Task L_Import_Cree_En_Brouillon()
    {
        var r = await _import.ImporterAsync(_envId, CsvValide, "m.konate");

        Assert.Equal(3, r.Crees);
        Assert.Equal(2, r.ServeursCrees);

        await using var db = _factory.CreateDbContext();

        Assert.All(await db.Components.ToListAsync(),
            c => Assert.Equal(LifecycleStatus.Brouillon, c.Status));
        Assert.All(await db.Servers.ToListAsync(),
            s => Assert.Equal(LifecycleStatus.Brouillon, s.Status));

        var center = await db.Components.FirstAsync(c => c.LogicalName == "Center");
        Assert.Equal(@"D:\navis\logs\apex.log", center.Readiness!.LogPath);
        Assert.Contains("servlet initialized", center.Readiness.ReadyPatterns);

        // Aucun profil inventé quand le fichier n'apporte rien.
        var bridge = await db.Components.FirstAsync(c => c.LogicalName == "Bridge");
        Assert.True(bridge.Readiness is null || bridge.Readiness.LogPath is null);
    }

    [SkippableFact(DisplayName = "Un rôle inconnu est refusé en disant lesquels existent")]
    public async Task Un_Role_Inconnu_Dit_Lesquels_Existent()
    {
        var csv = "hote;composant;role\nSRV-01;Truc;RoleQuiNExistePas";

        var r = await _import.ApercuAsync(_envId, csv);

        Assert.Equal(1, r.Refuses);
        var ligne = r.Lignes.Single();
        Assert.Contains("RoleQuiNExistePas", ligne.Explication);
        Assert.Contains("CenterNode", ligne.Explication);
    }

    [SkippableFact(DisplayName = "Un composant déjà présent est ignoré, pas refusé")]
    public async Task Un_Doublon_Est_Ignore()
    {
        await _import.ImporterAsync(_envId, CsvValide, "m.konate");

        // Réimporter le même fichier après correction d'une ligne ne doit pas
        // être traité comme une faute.
        var r = await _import.ImporterAsync(_envId, CsvValide, "m.konate");

        Assert.Equal(0, r.Crees);
        Assert.Equal(3, r.Ignores);
        Assert.Equal(0, r.Refuses);
        Assert.All(r.Lignes, l => Assert.Contains("existe déjà", l.Explication));
    }

    [SkippableFact(DisplayName = "Les virgules sont acceptées comme les points-virgules")]
    public async Task Les_Deux_Separateurs_Sont_Acceptes()
    {
        var csv = "hote,composant,role\nSRV-09,Center,CenterNode";

        var r = await _import.ApercuAsync(_envId, csv);

        Assert.True(r.Succeeded, r.ErreurGlobale);
        Assert.Equal(1, r.Crees);
    }

    [SkippableFact(DisplayName = "Une colonne obligatoire absente est dite avant tout traitement")]
    public async Task Une_Colonne_Manquante_Est_Dite()
    {
        var csv = "hote;service\nSRV-01;Navis";

        var r = await _import.ApercuAsync(_envId, csv);

        Assert.False(r.Succeeded);
        Assert.Contains("composant", r.ErreurGlobale!);
        Assert.Empty(r.Lignes);
    }

    [SkippableFact(DisplayName = "Une ligne refusée n'empêche pas les autres de passer")]
    public async Task Une_Ligne_Refusee_N_Arrete_Pas_L_Import()
    {
        var csv = """
            hote;composant;role
            SRV-01;Center;CenterNode
            SRV-01;Cassé;RoleInvalide
            SRV-01;Bridge;BridgeDaemon
            """;

        var r = await _import.ImporterAsync(_envId, csv, "m.konate");

        Assert.Equal(2, r.Crees);
        Assert.Equal(1, r.Refuses);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.Components.CountAsync());
    }

    [SkippableFact(DisplayName = "Un ordre non numérique est refusé avec sa valeur")]
    public async Task Un_Ordre_Non_Numerique_Est_Refuse()
    {
        var csv = "hote;composant;role;ordre\nSRV-01;Center;CenterNode;premier";

        var r = await _import.ApercuAsync(_envId, csv);

        Assert.Equal(1, r.Refuses);
        Assert.Contains("premier", r.Lignes.Single().Explication);
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
