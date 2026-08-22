using Xunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// Comptes d'exploitation nominatifs : un compte d'administration par
/// operateur, employe pour les actions qu'il declenche.
///
/// CE QUE CES TESTS PROTEGENT. L'objectif du dispositif est l'attribution :
/// que le journal de securite du serveur N4 nomme la personne, et pas un compte
/// de service derriere lequel tout le monde se confond. Trois proprietes le
/// rendent tenable, et chacune se casse silencieusement :
///
///   — le compte d'un operateur ne doit apparaitre a personne d'autre ;
///   — un refus d'authentification doit ECARTER le compte au premier essai,
///     faute de quoi les reprises de sequence verrouillent le compte de domaine
///     de l'operateur ;
///   — un acces refuse ne doit RIEN ecarter : le mot de passe est bon, ce sont
///     les droits qui manquent, et faire ressaisir masquerait la vraie cause.
///
/// Executes sur SQLite, comme la cible de deploiement.
/// </summary>
public sealed class ComptesNominatifsTests : IDisposable
{
    private readonly string _fichier;
    private readonly DbContextOptions<N4SentinelDbContext> _options;
    private readonly string _dossierCles;
    private readonly CredentialStore _magasin;

    public ComptesNominatifsTests()
    {
        _fichier = Path.Combine(Path.GetTempPath(), $"n4-nominatif-{Guid.NewGuid():N}.db");

        _options = new DbContextOptionsBuilder<N4SentinelDbContext>()
            .UseSqlite($"Data Source={_fichier}",
                s => s.MigrationsAssembly("N4Sentinel.Migrations.Sqlite"))
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        using (var db = new N4SentinelDbContext(_options))
            db.Database.Migrate();

        // Trousseau isole : ces tests ne doivent ni dependre du trousseau de la
        // machine, ni le polluer.
        _dossierCles = Path.Combine(Path.GetTempPath(), $"n4-cles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dossierCles);

        _magasin = new CredentialStore(
            new Fabrique(_options),
            DataProtectionProvider.Create(new DirectoryInfo(_dossierCles)),
            NullLogger<CredentialStore>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_fichier)) File.Delete(_fichier); } catch { /* verrou residuel */ }
        try { Directory.Delete(_dossierCles, recursive: true); } catch { /* nettoyage au mieux */ }
    }

    private N4SentinelDbContext Contexte() => new(_options);

    private async Task<Guid> CreerEnvironnementAsync()
    {
        await using var db = Contexte();
        var env = new N4Environment { Code = "PROD", Name = "Production N4" };
        db.Environments.Add(env);
        await db.SaveChangesAsync();
        return env.Id;
    }

    // -----------------------------------------------------------------------
    // Cloisonnement
    // -----------------------------------------------------------------------
    [Fact(DisplayName = "Le compte d'un opérateur n'apparaît pas dans les comptes partagés de l'environnement")]
    public async Task Un_Compte_Nominatif_Reste_Invisible()
    {
        var env = await CreerEnvironnementAsync();

        await _magasin.SaveAsync(new TechnicalCredential
        {
            EnvironmentId = env,
            Reference = "svc-partage",
            Label = "Compte de service",
            Mode = CredentialMode.CompteExplicite,
            UserName = "AGLPORTS\\svc_n4"
        }, "M0tDeP@sse-2026");

        await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", "M0tDeP@sse-2026");

        var partages = await _magasin.GetForEnvironmentAsync(env);

        // La liste deroulante d'une fiche serveur puise ici : y laisser filtrer
        // un compte nominatif reviendrait a le montrer a tous les autres.
        Assert.Single(partages);
        Assert.Equal("svc-partage", partages[0].Reference);
        Assert.DoesNotContain(partages, c => c.IsNominative);
    }

    [Fact(DisplayName = "Un compte nominatif n'est pas joignable par référence d'environnement")]
    public async Task Un_Compte_Nominatif_N_Est_Pas_Referencable()
    {
        var env = await CreerEnvironnementAsync();

        await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", "M0tDeP@sse-2026");

        // Meme en devinant la reference, une fiche serveur ne doit pas pouvoir
        // designer le compte personnel de quelqu'un.
        var trouve = await _magasin.GetByReferenceAsync(env, "operateur:user-1");

        Assert.Null(trouve);
    }

    [Fact(DisplayName = "Un administrateur ne peut pas réécrire le compte d'un opérateur")]
    public async Task L_Administrateur_Ne_Peut_Pas_Reecrire_Le_Compte_D_Autrui()
    {
        await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", "M0tDeP@sse-2026");

        var compte = await _magasin.GetForOwnerAsync("user-1");
        Assert.NotNull(compte);

        compte!.UserName = "AGLPORTS\\quelqu-un-d-autre";
        var erreur = await _magasin.SaveAsync(compte, "AutreM0tDeP@sse");

        Assert.NotNull(erreur);
        Assert.Contains("lui seul", erreur, StringComparison.OrdinalIgnoreCase);

        var relu = await _magasin.GetForOwnerAsync("user-1");
        Assert.Equal("AGLPORTS\\adm-mkonate", relu!.UserName);
    }

    // -----------------------------------------------------------------------
    // Verrouillage de compte : la regle qui n'a rien d'intuitif
    // -----------------------------------------------------------------------
    [Fact(DisplayName = "Un compte écarté cesse immédiatement d'être utilisable")]
    public async Task Un_Compte_Ecarte_N_Est_Plus_Utilisable()
    {
        await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", "M0tDeP@sse-2026");

        var compte = await _magasin.GetForOwnerAsync("user-1");
        Assert.True(compte!.IsUsable);

        await _magasin.InvalidateAsync(compte.Id, "Authentification refusee.");

        var relu = await _magasin.GetForOwnerAsync("user-1");

        // Le secret est toujours en base - c'est voulu, on ne perd pas la fiche.
        // Mais le compte ne doit plus jamais partir vers un serveur : c'est ce
        // qui evite de verrouiller le compte de domaine a force de reessayer.
        Assert.True(relu!.RequiresReentry);
        Assert.False(relu.IsUsable);
        Assert.Equal("A ressaisir", relu.SecretState);
    }

    [Fact(DisplayName = "La ressaisie remet le compte en service")]
    public async Task La_Ressaisie_Remet_Le_Compte_En_Service()
    {
        await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", "AncienM0tDeP@sse");

        var compte = await _magasin.GetForOwnerAsync("user-1");
        await _magasin.InvalidateAsync(compte!.Id, "Mot de passe expire.");

        var erreur = await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", "NouveauM0tDeP@sse");

        Assert.Null(erreur);

        var relu = await _magasin.GetForOwnerAsync("user-1");
        Assert.False(relu!.RequiresReentry);
        Assert.True(relu.IsUsable);
        Assert.Null(relu.InvalidatedAt);

        // Et c'est bien le NOUVEAU secret qui repart.
        var ps = _magasin.BuildPSCredential(relu);
        Assert.Equal("NouveauM0tDeP@sse", ps!.GetNetworkCredential().Password);
    }

    [Fact(DisplayName = "Le compte ne porte jamais deux fois l'écartement")]
    public async Task Un_Deuxieme_Refus_N_Ecrase_Pas_Le_Motif_Initial()
    {
        await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", "M0tDeP@sse-2026");

        var compte = await _magasin.GetForOwnerAsync("user-1");
        await _magasin.InvalidateAsync(compte!.Id, "Premier refus, sur SRV-N4-CENTER.");
        await _magasin.InvalidateAsync(compte.Id, "Second refus, ailleurs.");

        var relu = await _magasin.GetForOwnerAsync("user-1");

        // Le premier refus est celui qui explique la cause ; les suivants ne
        // sont que des consequences et ne doivent pas la recouvrir.
        Assert.Contains("Premier refus", relu!.InvalidationReason);
    }

    // -----------------------------------------------------------------------
    // Resolution de l'identite
    // -----------------------------------------------------------------------
    [Fact(DisplayName = "L'action d'un opérateur part sous SON compte, pas sous le compte partagé")]
    public async Task Le_Compte_De_L_Operateur_L_Emporte()
    {
        var env = await CreerEnvironnementAsync();
        var serveur = await CreerServeurAsync(env, "svc-partage");

        await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", "M0tDeP@sse-2026");

        var fabrique = Fabriquer();

        var resolution = await fabrique.CreateForActorAsync(serveur, "mkonate");

        Assert.True(resolution.Succeeded);
        Assert.Equal("AGLPORTS\\adm-mkonate", resolution.Credential!.UserName);
        Assert.True(resolution.Credential.IsNominative);
        Assert.Contains("Konaté Mamadou", resolution.IdentityDescription);
    }

    [Fact(DisplayName = "Sans compte nominatif, on retombe sur le compte partagé plutôt que d'échouer")]
    public async Task Sans_Compte_Nominatif_On_Retombe_Sur_Le_Partage()
    {
        var env = await CreerEnvironnementAsync();
        var serveur = await CreerServeurAsync(env, "svc-partage");

        var fabrique = Fabriquer();

        // Un site qui n'emploie pas encore de comptes nominatifs doit continuer
        // de fonctionner : le dispositif s'ajoute, il ne casse pas l'existant.
        var resolution = await fabrique.CreateForActorAsync(serveur, "inconnu");

        Assert.True(resolution.Succeeded);
        Assert.Equal("AGLPORTS\\svc_n4", resolution.Credential!.UserName);
    }

    [Fact(DisplayName = "Un compte écarté refuse l'action au lieu d'emprunter le compte partagé")]
    public async Task Un_Compte_Ecarte_Refuse_Plutot_Que_De_Se_Rabattre()
    {
        var env = await CreerEnvironnementAsync();
        var serveur = await CreerServeurAsync(env, "svc-partage");

        await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", "M0tDeP@sse-2026");
        var compte = await _magasin.GetForOwnerAsync("user-1");
        await _magasin.InvalidateAsync(compte!.Id, "Mot de passe expire.");

        var fabrique = Fabriquer();
        var resolution = await fabrique.CreateForActorAsync(serveur, "mkonate");

        // Se rabattre en silence sur le compte partage ferait exactement ce que
        // le dispositif existe pour empecher : l'action passerait, attribuee a
        // personne, et l'operateur ne saurait jamais que son compte est perime.
        Assert.False(resolution.Succeeded);
        Assert.Contains("ressaisissez", resolution.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "La supervision de fond n'emprunte le compte de personne")]
    public async Task La_Supervision_N_Emprunte_Aucun_Compte_Nominatif()
    {
        var env = await CreerEnvironnementAsync();
        var serveur = await CreerServeurAsync(env, "svc-partage");

        await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", "M0tDeP@sse-2026");

        var fabrique = Fabriquer();

        // CreateAsync est la voie du travail non surveille. Lui preter le compte
        // du dernier connecte ferait porter a cet operateur des releves faits
        // par la machine a trois heures du matin.
        var resolution = await fabrique.CreateAsync(serveur);

        Assert.True(resolution.Succeeded);
        Assert.Equal("AGLPORTS\\svc_n4", resolution.Credential!.UserName);
    }

    // -----------------------------------------------------------------------
    // Cycle de vie
    // -----------------------------------------------------------------------
    [Fact(DisplayName = "Effacer le compte d'un opérateur ne laisse aucun secret derrière lui")]
    public async Task L_Effacement_Ne_Laisse_Rien()
    {
        await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", "M0tDeP@sse-2026");

        Assert.True(await _magasin.EraseForOwnerAsync("user-1"));

        Assert.Null(await _magasin.GetForOwnerAsync("user-1"));

        await using var db = Contexte();
        Assert.Equal(0, await db.Credentials.CountAsync(c => c.OwnerUserId == "user-1"));
    }

    [Theory(DisplayName = "Un compte sans domaine est refusé à la saisie")]
    [InlineData("adm-mkonate")]
    [InlineData("  ")]
    public async Task Un_Compte_Sans_Domaine_Est_Refuse(string saisie)
    {
        var erreur = await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", saisie, "M0tDeP@sse-2026");

        // Sans domaine, l'authentification echouerait sur chaque serveur et le
        // compte serait ecarte au premier essai - pour une faute de saisie.
        Assert.NotNull(erreur);
    }

    [Fact(DisplayName = "Le mot de passe d'un compte nominatif n'atteint jamais le disque en clair")]
    public async Task Le_Secret_Nominatif_Est_Chiffre()
    {
        const string motDePasse = "M0tDeP@sse-Tres-Reconnaissable-2026";

        await _magasin.SaveOwnAsync(
            "user-1", "mkonate", "Konaté Mamadou", "AGLPORTS\\adm-mkonate", motDePasse);

        // Relu tel qu'il existe sur le disque, sans passer par le magasin :
        // c'est la seule verification qui prouve quelque chose.
        await using var connexion = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_fichier}");
        await connexion.OpenAsync();
        var cmd = connexion.CreateCommand();
        cmd.CommandText = "SELECT ProtectedPassword FROM Credentials WHERE OwnerUserId = 'user-1'";
        var stocke = (string?)await cmd.ExecuteScalarAsync();

        Assert.False(string.IsNullOrWhiteSpace(stocke));
        Assert.DoesNotContain(motDePasse, stocke);
        Assert.DoesNotContain("Reconnaissable", stocke);
    }

    // -----------------------------------------------------------------------
    private async Task<N4Server> CreerServeurAsync(Guid environmentId, string referencePartagee)
    {
        await using var db = Contexte();

        db.Credentials.Add(new TechnicalCredential
        {
            EnvironmentId = environmentId,
            Reference = referencePartagee,
            Label = "Compte de service",
            Mode = CredentialMode.CompteExplicite,
            UserName = "AGLPORTS\\svc_n4",
            ProtectedPassword = "chiffre-factice",
            Status = LifecycleStatus.Actif
        });

        var serveur = new N4Server
        {
            EnvironmentId = environmentId,
            HostName = "SRV-N4-CENTER",
            CredentialReference = referencePartagee,
            Status = LifecycleStatus.Valide
        };
        db.Servers.Add(serveur);
        await db.SaveChangesAsync();

        return serveur;
    }

    private ConnectorTargetFactory Fabriquer() => new(
        new Fabrique(_options), _magasin, NullLogger<ConnectorTargetFactory>.Instance);

    private sealed class Fabrique(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
