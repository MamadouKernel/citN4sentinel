using Xunit;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// Cloisonnement des environnements — SEC-004, correction de l'audit SEC-A1.
///
/// LE DÉFAUT CORRIGÉ : les politiques d'autorisation reposaient uniquement sur
/// des rôles globaux. Un opérateur habilité à redémarrer un nœud en recette
/// pouvait, avec exactement les mêmes droits, arrêter la Production — il lui
/// suffisait de changer d'environnement dans la liste déroulante.
///
/// Deux comportements pèsent autant que la restriction elle-même :
///
///   — une installation qui n'a déclaré AUCUNE habilitation n'est pas
///     verrouillée. Activer la restriction d'office mettrait tout le monde
///     dehors du jour au lendemain, y compris pendant un incident ;
///
///   — consulter et agir sont deux habilitations distinctes. C'est ce qui
///     permet à un support N1 de suivre la Production sans pouvoir l'arrêter.
/// </summary>
public sealed class CloisonnementTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private TestDbContextFactory _factory = null!;
    private EnvironmentAccessService _acces = null!;

    private Guid _prod;
    private Guid _uat;

    public async Task InitializeAsync()
    {
        TestConnectionHelper.SkipIfUnavailable();
        var cs = TestConnectionHelper.BuildDatabaseConnectionString(_databaseName);

        _factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<N4SentinelDbContext>().UseSqlServer(cs).Options);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        var prod = new N4Environment { Code = "PRD", Name = "Production", Kind = EnvironmentKind.Production };
        var uat = new N4Environment { Code = "UAT", Name = "Recette", Kind = EnvironmentKind.UAT };
        db.Environments.AddRange(prod, uat);
        await db.SaveChangesAsync();

        _prod = prod.Id;
        _uat = uat.Id;

        _acces = Construire(strict: false);
    }

    private EnvironmentAccessService Construire(bool strict)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EnvironmentAccessService.CleCloisonnementStrict] = strict ? "true" : "false"
            })
            .Build();

        return new EnvironmentAccessService(
            _factory, configuration, NullLogger<EnvironmentAccessService>.Instance);
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

    // =======================================================================
    // Le défaut d'origine
    // =======================================================================
    [SkippableFact]
    public async Task Un_Operateur_Habilite_Sur_La_Recette_Ne_Peut_Pas_Agir_En_Production()
    {
        // LE TEST QUI DONNE SON SENS A TOUT LE RESTE.
        await _acces.GrantAsync("u-op", "operateur", _uat, EnvironmentGrantLevel.Action, "Recette", null);

        var operateur = Utilisateur("u-op", N4Roles.OperateurN4);

        Assert.True((await _acces.CanActAsync(operateur, _uat)).Allowed);

        var refus = await _acces.CanActAsync(operateur, _prod);
        Assert.False(refus.Allowed);
        Assert.Contains("pas habilité", refus.Reason!);
        Assert.Contains("PRD", refus.Reason!);
    }

    [SkippableFact]
    public async Task Consulter_Et_Agir_Sont_Deux_Habilitations_Distinctes()
    {
        // Le cas du support N1 : suivre la Production sans pouvoir l'arreter.
        await _acces.GrantAsync("u-n1", "support.n1", _prod, EnvironmentGrantLevel.Consultation, null, null);

        var support = Utilisateur("u-n1", N4Roles.OperateurN4);

        Assert.True((await _acces.CanViewAsync(support, _prod)).Allowed);

        var refus = await _acces.CanActAsync(support, _prod);
        Assert.False(refus.Allowed);
        Assert.Contains("CONSULTER", refus.Reason!);
        Assert.Contains("pas à y agir", refus.Reason!);
    }

    // =======================================================================
    // Ne pas verrouiller une installation existante
    // =======================================================================
    [SkippableFact]
    public async Task Sans_Aucune_Habilitation_Declaree_Rien_N_Est_Cloisonne()
    {
        // Une mise a jour ne doit pas mettre tout le monde dehors.
        var operateur = Utilisateur("u-quelconque", N4Roles.OperateurN4);

        Assert.True((await _acces.CanActAsync(operateur, _prod)).Allowed);
        Assert.True((await _acces.CanViewAsync(operateur, _uat)).Allowed);
        Assert.False(await _acces.IsEnforcedAsync());
    }

    [SkippableFact]
    public async Task La_Premiere_Habilitation_Met_Le_Cloisonnement_En_Vigueur()
    {
        var autre = Utilisateur("u-autre", N4Roles.OperateurN4);
        Assert.True((await _acces.CanActAsync(autre, _prod)).Allowed);

        // Des qu'une habilitation existe, le cloisonnement s'applique a tous.
        await _acces.GrantAsync("u-op", "operateur", _uat, EnvironmentGrantLevel.Action, null, null);

        Assert.True(await _acces.IsEnforcedAsync());
        Assert.False((await _acces.CanActAsync(autre, _prod)).Allowed);
    }

    [SkippableFact]
    public async Task Le_Mode_Strict_Cloisonne_Des_Le_Depart()
    {
        // Un site qui prefere fermer d'emblee plutot qu'ouvrir puis restreindre.
        var strict = Construire(strict: true);
        var operateur = Utilisateur("u-op", N4Roles.OperateurN4);

        Assert.True(await strict.IsEnforcedAsync());
        Assert.False((await strict.CanViewAsync(operateur, _prod)).Allowed);
    }

    // =======================================================================
    // Cas particuliers
    // =======================================================================
    [SkippableFact]
    public async Task L_Administrateur_De_La_Solution_N_Est_Jamais_Cloisonne()
    {
        // Il administre le referentiel : lui interdire un environnement
        // l'empecherait de le creer.
        await _acces.GrantAsync("u-op", "operateur", _uat, EnvironmentGrantLevel.Action, null, null);

        var admin = Utilisateur("u-admin", N4Roles.AdministrateurSolution);

        Assert.True((await _acces.CanActAsync(admin, _prod)).Allowed);
        Assert.Equal(2, (await _acces.VisibleEnvironmentsAsync(admin)).Count);
    }

    [SkippableFact]
    public async Task Une_Habilitation_Expiree_Ne_Vaut_Plus_Rien()
    {
        // Les droits que personne ne retire sont ceux qui s'accumulent.
        await _acces.GrantAsync("u-presta", "prestataire", _prod, EnvironmentGrantLevel.Action,
            "Intervention du 12 au 14", DateTimeOffset.UtcNow.AddDays(-1));

        var prestataire = Utilisateur("u-presta", N4Roles.OperateurN4);

        var refus = await _acces.CanActAsync(prestataire, _prod);
        Assert.False(refus.Allowed);
        Assert.Contains("a expiré", refus.Reason!);

        Assert.Empty(await _acces.VisibleEnvironmentsAsync(prestataire));
    }

    [SkippableFact]
    public async Task La_Liste_Visible_Se_Limite_Aux_Environnements_Habilites()
    {
        await _acces.GrantAsync("u-op", "operateur", _uat, EnvironmentGrantLevel.Consultation, null, null);

        var visibles = await _acces.VisibleEnvironmentsAsync(Utilisateur("u-op", N4Roles.OperateurN4));

        Assert.Single(visibles);
        Assert.Equal(_uat, visibles[0]);
    }

    [SkippableFact]
    public async Task Une_Session_Non_Authentifiee_Est_Refusee()
    {
        var anonyme = new ClaimsPrincipal(new ClaimsIdentity());

        var refus = await _acces.CanViewAsync(anonyme, _prod);
        Assert.False(refus.Allowed);
        Assert.Contains("Aucune session", refus.Reason!);
    }

    [SkippableFact]
    public async Task Une_Habilitation_Se_Met_A_Jour_Sans_Se_Dupliquer()
    {
        await _acces.GrantAsync("u-op", "operateur", _prod, EnvironmentGrantLevel.Consultation, null, null);
        await _acces.GrantAsync("u-op", "operateur", _prod, EnvironmentGrantLevel.Action, "Promu", null);

        var habilitations = await _acces.GetGrantsAsync(_prod);

        Assert.Single(habilitations);
        Assert.Equal(EnvironmentGrantLevel.Action, habilitations[0].Level);
        Assert.Equal("Promu", habilitations[0].Reason);
    }

    [SkippableFact]
    public async Task Le_Retrait_D_Une_Habilitation_Ferme_L_Acces()
    {
        await _acces.GrantAsync("u-op", "operateur", _prod, EnvironmentGrantLevel.Action, null, null);
        await _acces.GrantAsync("u-autre", "autre", _uat, EnvironmentGrantLevel.Action, null, null);

        var operateur = Utilisateur("u-op", N4Roles.OperateurN4);
        Assert.True((await _acces.CanActAsync(operateur, _prod)).Allowed);

        var habilitation = (await _acces.GetGrantsAsync(_prod)).Single();
        await _acces.RevokeAsync(habilitation.Id);

        Assert.False((await _acces.CanActAsync(operateur, _prod)).Allowed);
    }

    // =======================================================================
    // SEC-001 — second facteur exigé pour agir en Production
    // =======================================================================
    [Fact]
    public void Une_Session_Sans_Second_Facteur_Est_Reconnue_Comme_Telle()
    {
        // La revendication amr=mfa prouve que CETTE session a franchi le second
        // facteur — plus fort que de constater qu'un compte l'a activé ailleurs.
        var sansSecondFacteur = Utilisateur("u-op", N4Roles.OperateurN4);

        Assert.False(
            N4Sentinel.Infrastructure.Orchestration.PreflightService.ASecondFacteur(sansSecondFacteur));
    }

    [Theory]
    [InlineData("mfa")]
    [InlineData("MFA")]
    [InlineData("otp")]
    public void Une_Session_Ouverte_Avec_Un_Second_Facteur_Est_Reconnue(string valeur)
    {
        var revendications = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "u-op"),
            new(ClaimTypes.Role, N4Roles.OperateurN4),
            new("amr", valeur)
        };

        var avecSecondFacteur = new ClaimsPrincipal(new ClaimsIdentity(revendications, "test"));

        Assert.True(
            N4Sentinel.Infrastructure.Orchestration.PreflightService.ASecondFacteur(avecSecondFacteur));
    }

    // =======================================================================
    private static ClaimsPrincipal Utilisateur(string identifiant, params string[] roles)
    {
        var revendications = new List<Claim> { new(ClaimTypes.NameIdentifier, identifiant) };
        revendications.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        return new ClaimsPrincipal(new ClaimsIdentity(revendications, "test"));
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
