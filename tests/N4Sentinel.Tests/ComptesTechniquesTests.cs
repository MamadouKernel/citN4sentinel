using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests du magasin de comptes techniques et de la fabrique de cibles.
///
/// Executes contre une base SQL Server reelle et jetable : le point qu'on veut
/// verifier - qu'aucun mot de passe en clair n'atteint le disque - n'a de sens
/// que contre un vrai stockage.
/// </summary>
public sealed class ComptesTechniquesTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _connectionString = string.Empty;
    private TestDbContextFactory _factory = null!;
    private CredentialStore _store = null!;
    private Guid _environmentId;
    private string _keyPath = string.Empty;

    public async Task InitializeAsync()
    {
        TestConnectionHelper.SkipIfUnavailable();
        _connectionString = TestConnectionHelper.BuildDatabaseConnectionString(_databaseName);

        var options = TestDbContextOptions.Builder(_connectionString).Options;

        _factory = new TestDbContextFactory(options);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        var environnement = new N4Environment { Code = "UAT", Name = "Recette N4" };
        db.Environments.Add(environnement);
        await db.SaveChangesAsync();
        _environmentId = environnement.Id;

        // Trousseau isole et jetable : les tests ne doivent ni dependre du
        // trousseau de la machine, ni le polluer.
        _keyPath = Path.Combine(Path.GetTempPath(), $"n4-cles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keyPath);

        _store = new CredentialStore(
            _factory,
            DataProtectionProvider.Create(new DirectoryInfo(_keyPath)),
            NullLogger<CredentialStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_keyPath))
            try { Directory.Delete(_keyPath, recursive: true); } catch { /* nettoyage au mieux */ }

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

    // -----------------------------------------------------------------------
    // Le point central : le secret ne doit jamais atteindre le disque en clair
    // -----------------------------------------------------------------------
    [Fact(DisplayName = "Le mot de passe n'est jamais stocke en clair")]
    public async Task Le_Mot_De_Passe_Est_Chiffre_En_Base()
    {
        const string motDePasse = "M0tDeP@sse-Tres-Reconnaissable-2026";

        var erreur = await _store.SaveAsync(new TechnicalCredential
        {
            EnvironmentId = _environmentId,
            Reference = "svc-n4-uat",
            Label = "Compte de service UAT",
            Mode = CredentialMode.CompteExplicite,
            UserName = "CIT\\svc_n4sentinel"
        }, motDePasse);

        Assert.Null(erreur);

        // On relit la colonne telle qu'elle existe sur le disque, sans passer
        // par le magasin : c'est la seule verification qui prouve quelque chose.
        await using var connexion = new Microsoft.Data.SqlClient.SqlConnection(_connectionString);
        await connexion.OpenAsync();
        var cmd = connexion.CreateCommand();
        cmd.CommandText = "SELECT ProtectedPassword FROM Credentials";
        var stocke = (string?)await cmd.ExecuteScalarAsync();

        Assert.False(string.IsNullOrWhiteSpace(stocke));
        Assert.DoesNotContain(motDePasse, stocke);
        Assert.DoesNotContain("Reconnaissable", stocke);

        // Le chiffre porte l'en-tete de Data Protection et depasse largement
        // la longueur du clair.
        Assert.True(stocke!.Length > motDePasse.Length * 2);
    }

    [Fact(DisplayName = "Le compte se relit sous forme de PSCredential exploitable")]
    public async Task Le_Compte_Se_Reconstruit()
    {
        await _store.SaveAsync(new TechnicalCredential
        {
            EnvironmentId = _environmentId,
            Reference = "svc-n4-uat",
            Label = "Compte de service UAT",
            Mode = CredentialMode.CompteExplicite,
            UserName = "CIT\\svc_n4sentinel"
        }, "M0tDeP@sse-2026");

        var compte = await _store.GetByReferenceAsync(_environmentId, "svc-n4-uat");
        Assert.NotNull(compte);

        var ps = _store.BuildPSCredential(compte!);

        Assert.NotNull(ps);
        Assert.Equal("CIT\\svc_n4sentinel", ps!.UserName);
        Assert.Equal("M0tDeP@sse-2026", ps.GetNetworkCredential().Password);
    }

    [Fact(DisplayName = "L'identite du processus ne produit aucun credential")]
    public async Task Identite_Du_Processus_Ne_Produit_Rien()
    {
        await _store.SaveAsync(new TechnicalCredential
        {
            EnvironmentId = _environmentId,
            Reference = "identite-appli",
            Label = "Identite de l'application",
            Mode = CredentialMode.IdentiteDuProcessus
        }, null);

        var compte = await _store.GetByReferenceAsync(_environmentId, "identite-appli");

        Assert.NotNull(compte);
        Assert.True(compte!.IsUsable);
        Assert.Null(_store.BuildPSCredential(compte));
        Assert.Equal("Sans objet - identite du processus", compte.SecretState);
    }

    [Fact(DisplayName = "Repasser en identite du processus efface le secret devenu inutile")]
    public async Task Bascule_Vers_Identite_Efface_Le_Secret()
    {
        var compte = new TechnicalCredential
        {
            EnvironmentId = _environmentId,
            Reference = "svc-bascule",
            Label = "Compte a basculer",
            Mode = CredentialMode.CompteExplicite,
            UserName = "CIT\\svc_test"
        };
        await _store.SaveAsync(compte, "M0tDeP@sse-2026");

        compte.Mode = CredentialMode.IdentiteDuProcessus;
        await _store.SaveAsync(compte, null);

        var relu = await _store.GetByReferenceAsync(_environmentId, "svc-bascule");

        // Un secret conserve "au cas ou" est un secret qui fuira un jour.
        Assert.Null(relu!.ProtectedPassword);
        Assert.Null(relu.UserName);
    }

    [Fact(DisplayName = "Modifier un compte sans fournir de mot de passe conserve le secret existant")]
    public async Task Modification_Sans_Mot_De_Passe_Conserve_Le_Secret()
    {
        var compte = new TechnicalCredential
        {
            EnvironmentId = _environmentId,
            Reference = "svc-n4",
            Label = "Libelle initial",
            Mode = CredentialMode.CompteExplicite,
            UserName = "CIT\\svc_n4"
        };
        await _store.SaveAsync(compte, "M0tDeP@sse-2026");

        // C'est ce qui permet a un ecran de renommer un compte sans jamais
        // avoir eu besoin de relire son mot de passe.
        compte.Label = "Libelle corrige";
        await _store.SaveAsync(compte, null);

        var relu = await _store.GetByReferenceAsync(_environmentId, "svc-n4");
        Assert.Equal("Libelle corrige", relu!.Label);
        Assert.Equal("M0tDeP@sse-2026", _store.BuildPSCredential(relu)!.GetNetworkCredential().Password);
    }

    [Fact(DisplayName = "Un compte explicite sans mot de passe est refuse a la creation")]
    public async Task Compte_Explicite_Sans_Secret_Est_Refuse()
    {
        var erreur = await _store.SaveAsync(new TechnicalCredential
        {
            EnvironmentId = _environmentId,
            Reference = "svc-incomplet",
            Label = "Incomplet",
            Mode = CredentialMode.CompteExplicite,
            UserName = "CIT\\svc"
        }, null);

        Assert.NotNull(erreur);
        Assert.Contains("mot de passe", erreur!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Deux comptes ne peuvent pas partager la meme reference dans un environnement")]
    public async Task Reference_Unique_Par_Environnement()
    {
        await _store.SaveAsync(new TechnicalCredential
        {
            EnvironmentId = _environmentId, Reference = "svc", Label = "Premier",
            Mode = CredentialMode.IdentiteDuProcessus
        }, null);

        var erreur = await _store.SaveAsync(new TechnicalCredential
        {
            EnvironmentId = _environmentId, Reference = "svc", Label = "Second",
            Mode = CredentialMode.IdentiteDuProcessus
        }, null);

        Assert.NotNull(erreur);
        Assert.Contains("deja utilisee", erreur!);
    }

    // -----------------------------------------------------------------------
    // Fabrique de cibles
    // -----------------------------------------------------------------------
    private ConnectorTargetFactory Fabrique() =>
        new(_factory, _store, NullLogger<ConnectorTargetFactory>.Instance);

    [Fact(DisplayName = "Sans compte reference, la connexion se fait sous l'identite du processus")]
    public async Task Sans_Reference_Identite_Du_Processus()
    {
        var serveur = await AjouterServeur("N4CENTER01");

        var resolution = await Fabrique().CreateAsync(serveur.Id);

        Assert.True(resolution.Succeeded, resolution.Error);
        Assert.Null(resolution.Target!.Credential);
        Assert.Equal("identite du processus", resolution.IdentityDescription);
    }

    [Fact(DisplayName = "La fiche serveur l'emporte sur le compte par defaut de l'environnement")]
    public async Task Le_Serveur_Surcharge_L_Environnement()
    {
        await _store.SaveAsync(new TechnicalCredential
        {
            EnvironmentId = _environmentId, Reference = "defaut", Label = "Compte par defaut",
            Mode = CredentialMode.CompteExplicite, UserName = "CIT\\svc_defaut"
        }, "M0tDeP@sse-2026");

        await _store.SaveAsync(new TechnicalCredential
        {
            EnvironmentId = _environmentId, Reference = "specifique", Label = "Compte specifique",
            Mode = CredentialMode.CompteExplicite, UserName = "CIT\\svc_specifique"
        }, "M0tDeP@sse-2026");

        await using (var db = _factory.CreateDbContext())
        {
            var env = await db.Environments.SingleAsync();
            env.DefaultCredentialReference = "defaut";
            await db.SaveChangesAsync();
        }

        var serveur = await AjouterServeur("N4BRIDGE01", credentialReference: "specifique");

        var resolution = await Fabrique().CreateAsync(serveur.Id);

        Assert.True(resolution.Succeeded, resolution.Error);
        Assert.Equal("CIT\\svc_specifique", resolution.Target!.Credential!.UserName);
    }

    [Fact(DisplayName = "Une reference de compte inconnue echoue au lieu de se rabattre en silence")]
    public async Task Reference_Inconnue_Echoue_Explicitement()
    {
        var serveur = await AjouterServeur("N4XPS01", credentialReference: "compte-supprime");

        var resolution = await Fabrique().CreateAsync(serveur.Id);

        // Se rabattre sur l'identite du processus pourrait faire reussir la
        // connexion avec les mauvais droits, sans que personne ne le sache.
        Assert.False(resolution.Succeeded);
        Assert.Contains("compte-supprime", resolution.Error!);
    }

    [Fact(DisplayName = "La machine hebergeant l'application est traitee en local, sans WinRM")]
    public async Task La_Machine_Locale_Est_Detectee()
    {
        var serveur = await AjouterServeur(Environment.MachineName);

        var resolution = await Fabrique().CreateAsync(serveur.Id);

        Assert.True(resolution.Succeeded, resolution.Error);
        Assert.True(resolution.Target!.IsLocal);
    }

    [Fact(DisplayName = "Un serveur absent du referentiel ne peut pas etre atteint")]
    public async Task Serveur_Hors_Referentiel_Est_Refuse()
    {
        var resolution = await Fabrique().CreateAsync(Guid.NewGuid());

        Assert.False(resolution.Succeeded);
        Assert.Contains("introuvable", resolution.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<N4Server> AjouterServeur(string hostName, string? credentialReference = null)
    {
        await using var db = _factory.CreateDbContext();
        var serveur = new N4Server
        {
            EnvironmentId = _environmentId,
            HostName = hostName,
            CredentialReference = credentialReference
        };
        db.Servers.Add(serveur);
        await db.SaveChangesAsync();
        return serveur;
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
