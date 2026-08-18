using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Operations;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests de la sauvegarde — DEP-05, NFR-010.
///
/// Ce que ces tests protègent n'est pas la production d'un fichier : c'est la
/// capacité à REPRENDRE après un sinistre.
///
/// Le point central est que les mots de passe des comptes techniques sont
/// chiffrés par Data Protection, dont les clés vivent hors de la base.
/// Sauvegarder la base seule redonne des comptes dont les secrets sont
/// illisibles — et le constat se fait au pire moment. Le service refuse donc
/// de produire une sauvegarde amputée du trousseau plutôt que d'en produire
/// une qui semble complète.
/// </summary>
public sealed class SauvegardeTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _keyPath = string.Empty;
    private string _destination = string.Empty;
    private TestDbContextFactory _factory = null!;
    private BackupService _sauvegarde = null!;

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

            db.Credentials.Add(new TechnicalCredential
            {
                EnvironmentId = env.Id,
                Reference = "compte-service-n4",
                Label = "Compte de service N4",
                Mode = CredentialMode.CompteExplicite,
                UserName = "CIT\\svc-n4",
                ProtectedPassword = "CfDJ8FauxChiffreDeTest"
            });
            await db.SaveChangesAsync();
        }

        _keyPath = Path.Combine(Path.GetTempPath(), $"n4-cles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keyPath);

        // LA DESTINATION APPARTIENT A SQL SERVER, PAS A NOUS. Le fichier .bak
        // est ecrit par le moteur SQL, sous SON compte de service : un dossier
        // du profil de l'utilisateur courant lui est refuse. On demande donc au
        // serveur son propre dossier de sauvegarde, ou il sait ecrire.
        _destination = await DossierDeSauvegardeSqlAsync();

        // Deux fichiers de cle, comme en produirait Data Protection.
        await File.WriteAllTextAsync(Path.Combine(_keyPath, "key-1.xml"), "<key id=\"1\" />");
        await File.WriteAllTextAsync(Path.Combine(_keyPath, "key-2.xml"), "<key id=\"2\" />");

        _sauvegarde = Construire(_keyPath);
    }

    /// <summary>
    /// Dossier accessible en écriture aux DEUX parties : à nous pour le créer
    /// et le lire, au compte de service SQL Server pour y déposer le fichier de
    /// sauvegarde.
    ///
    /// Le dossier de sauvegarde par défaut de l'instance ne convient pas : il
    /// vit sous « Program Files », où nous ne pouvons pas créer de sous-dossier
    /// sans élévation. ProgramData satisfait les deux contraintes.
    ///
    /// Retourne une chaîne vide si rien n'est possible — les tests qui écrivent
    /// réellement sont alors ignorés plutôt que déclarés en échec.
    /// </summary>
    private static Task<string> DossierDeSauvegardeSqlAsync()
    {
        try
        {
            var racine = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "N4Sentinel-Tests");

            var dossier = Path.Combine(racine, $"sauv-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dossier);

            return Task.FromResult(dossier);
        }
        catch (Exception)
        {
            return Task.FromResult(string.Empty);
        }
    }

    private BackupService Construire(string cheminCles)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["N4Sentinel:DataProtection:KeyPath"] = cheminCles
            })
            .Build();

        return new BackupService(_factory, configuration, NullLogger<BackupService>.Instance);
    }

    /// <summary>
    /// Produit une sauvegarde réelle, ou ignore le test si SQL Server ne peut
    /// pas écrire. Le cas est légitime — instance distante, droits restreints —
    /// et n'a rien à voir avec ce que ces tests vérifient.
    /// </summary>
    private async Task<BackupResult> SauvegarderOuIgnorerAsync(BackupService? service = null)
    {
        Skip.If(string.IsNullOrEmpty(_destination),
            "L'instance SQL Server ne déclare pas de dossier de sauvegarde accessible depuis ce poste.");

        var r = await (service ?? _sauvegarde).CreateAsync(_destination, "m.konate");

        Skip.If(!r.Succeeded && (r.Error?.Contains("Accès refusé") == true
                                 || r.Error?.Contains("Access is denied") == true
                                 || r.Error?.Contains("Operating system error 5") == true),
            "Le compte de service SQL Server n'a pas le droit d'écrire dans son propre dossier de sauvegarde.");

        Assert.True(r.Succeeded, r.Error);
        return r;
    }

    public async Task DisposeAsync()
    {
        foreach (var dossier in new[] { _keyPath, _destination })
            if (!string.IsNullOrEmpty(dossier) && Directory.Exists(dossier))
                try { Directory.Delete(dossier, true); } catch { }

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
    // État
    // =======================================================================
    [SkippableFact]
    public async Task L_Etat_Denombre_Ce_Qu_Une_Sauvegarde_Doit_Preserver()
    {
        var etat = await _sauvegarde.GetStatusAsync();

        Assert.Equal(_databaseName, etat.DatabaseName);
        Assert.Equal(2, etat.KeyFileCount);
        Assert.Equal(1, etat.CredentialCount);
        Assert.Equal(1, etat.CredentialsWithSecretCount);
        Assert.False(etat.KeyRingMissing);
    }

    [SkippableFact]
    public async Task Un_Trousseau_Absent_Avec_Des_Secrets_Est_Un_Etat_Signale()
    {
        // Situation grave et silencieuse : rien ne la signale au quotidien,
        // jusqu'au jour ou l'on tente une reprise.
        var vide = Path.Combine(Path.GetTempPath(), $"n4-vide-{Guid.NewGuid():N}");
        Directory.CreateDirectory(vide);

        try
        {
            var etat = await Construire(vide).GetStatusAsync();

            Assert.Equal(0, etat.KeyFileCount);
            Assert.Equal(1, etat.CredentialsWithSecretCount);
            Assert.True(etat.KeyRingMissing);
        }
        finally
        {
            Directory.Delete(vide, true);
        }
    }

    // =======================================================================
    // Sauvegarde
    // =======================================================================
    [SkippableFact]
    public async Task Une_Sauvegarde_Contient_La_Base_Le_Trousseau_Et_Le_Manifeste()
    {
        var r = await SauvegarderOuIgnorerAsync();

        Assert.NotNull(r.Folder);
        Assert.True(r.SecretsPreserved);

        Assert.True(File.Exists(Path.Combine(r.Folder!, $"{_databaseName}.bak")));
        Assert.True(File.Exists(Path.Combine(r.Folder!, "manifeste.json")));
        Assert.True(File.Exists(Path.Combine(r.Folder!, "LISEZ-MOI.txt")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(r.Folder!, "cles-protection"), "*.xml").Length);

        Assert.Equal(2, r.Manifest!.KeyFileCount);
        Assert.Equal("m.konate", r.Manifest.CreatedBy);
    }

    [SkippableFact]
    public async Task La_Sauvegarde_Est_Refusee_Si_Le_Trousseau_Est_Introuvable()
    {
        // Mieux vaut ne rien produire qu'une sauvegarde qui semble complete.
        var inexistant = Path.Combine(Path.GetTempPath(), $"n4-absent-{Guid.NewGuid():N}");

        var r = await Construire(inexistant).CreateAsync(_destination, "m.konate");

        Assert.False(r.Succeeded);
        Assert.Contains("irrécupérables", r.Error!);
        Assert.Contains("interrompue plutôt que produite incomplète", r.Error!);
    }

    [SkippableFact]
    public async Task La_Sauvegarde_Est_Refusee_Si_Le_Trousseau_Est_Vide_Alors_Que_Des_Secrets_Existent()
    {
        var vide = Path.Combine(Path.GetTempPath(), $"n4-vide-{Guid.NewGuid():N}");
        Directory.CreateDirectory(vide);

        try
        {
            var r = await Construire(vide).CreateAsync(_destination, "m.konate");

            Assert.False(r.Succeeded);
            Assert.Contains("Aucun fichier de clé", r.Error!);
        }
        finally
        {
            Directory.Delete(vide, true);
        }
    }

    [SkippableFact]
    public async Task La_Notice_Dit_Que_La_Base_Seule_Ne_Suffit_Pas()
    {
        var r = await SauvegarderOuIgnorerAsync();
        var notice = await File.ReadAllTextAsync(Path.Combine(r.Folder!, "LISEZ-MOI.txt"));

        Assert.Contains("LA BASE SEULE NE SUFFIT PAS", notice);
        Assert.Contains("LIE A LA MACHINE", notice);
        Assert.Contains("PROCEDURE DE RESTAURATION", notice);
        Assert.Contains("RESTORE DATABASE", notice);
    }

    // =======================================================================
    // Vérification
    // =======================================================================
    [SkippableFact]
    public async Task Une_Sauvegarde_Fraiche_Est_Declaree_Restaurable()
    {
        var r = await SauvegarderOuIgnorerAsync();

        var v = await _sauvegarde.VerifyAsync(r.Folder!);

        Assert.True(v.IsRestorable, string.Join(" | ",
            v.Checks.Where(c => c.Outcome == BackupOutcome.Echec).Select(c => c.Detail)));

        Assert.Equal(0, v.Failures);
        Assert.Contains(v.Checks, c => c.Name == "Sauvegarde de la base" && c.Outcome == BackupOutcome.Reussi);
        Assert.Contains(v.Checks, c => c.Name == "Trousseau de clés" && c.Outcome == BackupOutcome.Reussi);
    }

    [SkippableFact]
    public async Task Un_Dossier_Sans_Manifeste_N_Est_Pas_Une_Sauvegarde()
    {
        var quelconque = Path.Combine(Path.GetTempPath(), $"n4-quelconque-{Guid.NewGuid():N}");
        Directory.CreateDirectory(quelconque);

        try
        {
            var v = await _sauvegarde.VerifyAsync(quelconque);

            Assert.False(v.IsRestorable);
            Assert.Contains(v.Checks, c => c.Detail.Contains("ne s'agit pas d'une sauvegarde"));
        }
        finally
        {
            Directory.Delete(quelconque, true);
        }
    }

    [SkippableFact]
    public async Task Un_Fichier_De_Base_Disparu_Est_Detecte()
    {
        // Le seul moment ou l'on decouvre qu'un fichier manque ne doit pas
        // etre celui ou l'on en a besoin.
        //
        // Le fichier .bak appartient au compte de service SQL Server : nous ne
        // pouvons pas le supprimer. On modifie donc le manifeste pour qu'il
        // designe un fichier absent, ce qui eprouve exactement le meme controle.
        var r = await SauvegarderOuIgnorerAsync();

        var chemin = Path.Combine(r.Folder!, "manifeste.json");
        var json = await File.ReadAllTextAsync(chemin);
        await File.WriteAllTextAsync(chemin,
            json.Replace($"{_databaseName}.bak", "fichier-disparu.bak"));

        var v = await _sauvegarde.VerifyAsync(r.Folder!);

        Assert.False(v.IsRestorable);
        Assert.Contains(v.Checks, c => c.Name == "Sauvegarde de la base" && c.Outcome == BackupOutcome.Echec);
    }

    [SkippableFact]
    public async Task Un_Trousseau_Disparu_Fait_Echouer_La_Verification()
    {
        var r = await SauvegarderOuIgnorerAsync();
        Directory.Delete(Path.Combine(r.Folder!, "cles-protection"), true);

        var v = await _sauvegarde.VerifyAsync(r.Folder!);

        Assert.False(v.IsRestorable);

        var controle = v.Checks.First(c => c.Name == "Trousseau de clés");
        Assert.Equal(BackupOutcome.Echec, controle.Outcome);
        Assert.Contains("irrécupérables", controle.Detail);
    }

    [SkippableFact]
    public async Task Un_Trousseau_Incomplet_Produit_Un_Avertissement()
    {
        var r = await SauvegarderOuIgnorerAsync();
        File.Delete(Path.Combine(r.Folder!, "cles-protection", "key-2.xml"));

        var v = await _sauvegarde.VerifyAsync(r.Folder!);

        var controle = v.Checks.First(c => c.Name == "Trousseau de clés");
        Assert.Equal(BackupOutcome.Avertissement, controle.Outcome);
        Assert.Contains("annoncé", controle.Detail);
    }

    [SkippableFact]
    public async Task Une_Sauvegarde_D_Une_Autre_Machine_Est_Signalee()
    {
        // LE POINT QUI COUTE UNE NUIT S'IL N'EST PAS DIT : le trousseau est
        // chiffre par DPAPI a l'echelle de la machine.
        var r = await SauvegarderOuIgnorerAsync();

        var chemin = Path.Combine(r.Folder!, "manifeste.json");
        var json = await File.ReadAllTextAsync(chemin);
        await File.WriteAllTextAsync(chemin,
            json.Replace($"\"{Environment.MachineName}\"", "\"UN-AUTRE-SERVEUR\""));

        var v = await _sauvegarde.VerifyAsync(r.Folder!);

        var controle = v.Checks.First(c => c.Name == "Machine d'origine");
        Assert.Equal(BackupOutcome.Avertissement, controle.Outcome);
        Assert.Contains("DPAPI", controle.Detail);
        Assert.Contains("ressaisis", controle.Detail);

        // La base reste restaurable : seule la reprise des secrets est en cause.
        Assert.True(v.IsRestorable);
    }

    [SkippableFact]
    public async Task Un_Dossier_Inexistant_Est_Refuse_Sans_Exception()
    {
        var v = await _sauvegarde.VerifyAsync(Path.Combine(_destination, "n-existe-pas"));

        Assert.False(v.IsRestorable);
        Assert.NotNull(v.Error);
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
