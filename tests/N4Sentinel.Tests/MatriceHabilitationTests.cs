using Xunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using N4Sentinel.Domain;
using N4Sentinel.Web.Security;
using System.Security.Claims;

namespace N4Sentinel.Tests;

/// <summary>
/// Matrice d'habilitation.
///
/// Décision d'exploitation du 20/08 : l'Administrateur de la solution détient
/// TOUTES les capacités, pour pouvoir tout faire depuis l'application sans
/// s'attribuer un rôle de plus à chaque geste.
///
/// Ce que ces tests protègent :
///   — que l'administrateur les conserve toutes, y compris après un ajout de
///     politique fait sans y penser ;
///   — qu'AUCUN autre rôle n'ait été élargi au passage.
/// </summary>
public sealed class MatriceHabilitationTests
{
    private static IAuthorizationService Construire()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddN4SentinelAuthorization(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal Avec(params string[] roles)
    {
        var identite = new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)), "test");
        return new ClaimsPrincipal(identite);
    }

    /// <summary>Toutes les politiques déclarées par l'application.</summary>
    public static TheoryData<string> ToutesLesPolitiques() =>
    [
        N4Policies.PeutConsulter,
        N4Policies.PeutDiagnostiquer,
        N4Policies.PeutExecuter,
        N4Policies.PeutExecuterActionsSensibles,
        N4Policies.PeutExecuterActionUnitaire,
        N4Policies.PeutApprouver,
        N4Policies.PeutAdministrerReferentiel,
        N4Policies.PeutAdministrerConnecteurs,
        N4Policies.PeutAuditer
    ];

    [Theory]
    [MemberData(nameof(ToutesLesPolitiques))]
    public async Task L_Administrateur_De_La_Solution_Detient_Toutes_Les_Capacites(string politique)
    {
        var autorisation = Construire();

        var r = await autorisation.AuthorizeAsync(
            Avec(N4Roles.AdministrateurSolution), resource: null, politique);

        Assert.True(r.Succeeded, $"L'administrateur n'a pas la capacité « {politique} ».");
    }

    [Fact(DisplayName = "L'Auditeur ne gagne aucune capacité d'action")]
    public async Task L_Auditeur_Ne_Peut_Pas_Agir()
    {
        var autorisation = Construire();
        var auditeur = Avec(N4Roles.Auditeur);

        // Contrôler ne donne pas le droit d'exécuter. C'est le sens même du
        // rôle : l'élargir viderait l'audit de sa portée.
        foreach (var politique in new[]
                 {
                     N4Policies.PeutExecuter,
                     N4Policies.PeutExecuterActionsSensibles,
                     N4Policies.PeutExecuterActionUnitaire,
                     N4Policies.PeutApprouver
                 })
        {
            var r = await autorisation.AuthorizeAsync(auditeur, resource: null, politique);
            Assert.False(r.Succeeded, $"L'auditeur ne devrait pas détenir « {politique} ».");
        }
    }

    [Fact(DisplayName = "Le Validateur approuve mais n'exécute pas")]
    public async Task Le_Validateur_N_Execute_Pas()
    {
        var autorisation = Construire();
        var validateur = Avec(N4Roles.Validateur);

        Assert.True((await autorisation.AuthorizeAsync(
            validateur, null, N4Policies.PeutApprouver)).Succeeded);

        Assert.False((await autorisation.AuthorizeAsync(
            validateur, null, N4Policies.PeutExecuter)).Succeeded);
    }

    [Fact(DisplayName = "L'Opérateur N4 exécute mais n'approuve ni n'administre")]
    public async Task L_Operateur_Reste_Dans_Son_Perimetre()
    {
        var autorisation = Construire();
        var operateur = Avec(N4Roles.OperateurN4);

        Assert.True((await autorisation.AuthorizeAsync(
            operateur, null, N4Policies.PeutExecuter)).Succeeded);

        // Non élargis par la décision du 20/08 : elle ne portait que sur
        // l'Administrateur de la solution.
        Assert.False((await autorisation.AuthorizeAsync(
            operateur, null, N4Policies.PeutApprouver)).Succeeded);
        Assert.False((await autorisation.AuthorizeAsync(
            operateur, null, N4Policies.PeutAdministrerReferentiel)).Succeeded);
        Assert.False((await autorisation.AuthorizeAsync(
            operateur, null, N4Policies.PeutExecuterActionsSensibles)).Succeeded);
    }

    [Fact(DisplayName = "Le Lecteur Support N1 consulte, et rien de plus")]
    public async Task Le_Lecteur_Ne_Fait_Que_Consulter()
    {
        var autorisation = Construire();
        var lecteur = Avec(N4Roles.LecteurSupportN1);

        Assert.True((await autorisation.AuthorizeAsync(
            lecteur, null, N4Policies.PeutConsulter)).Succeeded);

        var autres = new[]
        {
            N4Policies.PeutDiagnostiquer,
            N4Policies.PeutExecuter,
            N4Policies.PeutExecuterActionsSensibles,
            N4Policies.PeutExecuterActionUnitaire,
            N4Policies.PeutApprouver,
            N4Policies.PeutAdministrerReferentiel,
            N4Policies.PeutAdministrerConnecteurs,
            N4Policies.PeutAuditer
        };

        foreach (var politique in autres)
        {
            var r = await autorisation.AuthorizeAsync(lecteur, null, politique);
            Assert.False(r.Succeeded, $"Le lecteur ne devrait pas détenir « {politique} ».");
        }
    }
}
