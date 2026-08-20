using Microsoft.AspNetCore.Authorization;
using N4Sentinel.Domain;

namespace N4Sentinel.Web.Security;

/// <summary>
/// Matrice d'habilitation : quelles capacites metier chaque profil detient.
///
/// Le code applicatif verifie une CAPACITE ([Authorize(Policy = ...)]), jamais
/// un role. La difference compte : quand la DSI decidera qu'un Support N2 peut
/// approuver une operation en UAT, la modification tient ici, dans un seul
/// fichier, sans rouvrir les ecrans ni les services.
///
/// Ce qui n'est PAS exprimable ici et reste a la charge des services metier :
///   - le demandeur et l'approbateur d'une meme operation critique doivent
///     etre deux personnes distinctes ;
///   - un Administrateur N4 ne peut pas approuver son propre contournement ;
///   - les droits se differencient par environnement (UAT vs Production).
/// Ces regles dependent de l'objet vise, pas seulement de l'utilisateur.
/// </summary>
public static class AuthorizationSetup
{
    /// <summary>
    /// Clé de configuration exigeant le second facteur sur les capacités
    /// d'action (audit SEC-A5).
    ///
    /// DÉSACTIVÉE PAR DÉFAUT, ET C'EST DÉLIBÉRÉ. L'activer d'office
    /// interdirait l'accès à tout compte n'ayant pas encore enrôlé son
    /// authentificateur — à commencer par l'administrateur qui vient
    /// d'installer le produit, et qui se retrouverait dehors sans recours.
    ///
    /// Marche à suivre : faire enrôler les comptes habilités à l'action, puis
    /// passer ce réglage à true. Le refuser durablement est un choix, mais il
    /// doit être un choix, pas un oubli.
    /// </summary>
    public const string CleSecondFacteur = "N4Sentinel:Securite:SecondFacteurExigePourAction";

    public static IServiceCollection AddN4SentinelAuthorization(
        this IServiceCollection services, IConfiguration configuration)
    {
        var exigeSecondFacteur = configuration.GetValue(CleSecondFacteur, false);

        services.AddSingleton<IAuthorizationHandler, SecondFacteurHandler>();

        // Applique l'exigence de second facteur aux seules capacites d'action.
        // La consultation n'est jamais concernee : empecher un lecteur N1 de
        // regarder un tableau de bord n'ajoute aucune securite, et pousserait a
        // desactiver le reglage.
        void Action(AuthorizationPolicyBuilder p)
        {
            if (exigeSecondFacteur) p.AddRequirements(new SecondFacteurRequirement());
        }

        services.AddAuthorizationBuilder()

            .AddPolicy(N4Policies.PeutConsulter, p => p.RequireRole(
                N4Roles.LecteurSupportN1,
                N4Roles.OperateurN4,
                N4Roles.AdministrateurN4,
                N4Roles.AdministrateurInfrastructure,
                N4Roles.Validateur,
                N4Roles.AdministrateurSolution,
                N4Roles.Auditeur,
                N4Roles.TosManager))

            .AddPolicy(N4Policies.PeutDiagnostiquer, p => p.RequireRole(
                N4Roles.OperateurN4,
                N4Roles.AdministrateurN4,
                N4Roles.AdministrateurInfrastructure,
                N4Roles.AdministrateurSolution))

            // L'ADMINISTRATEUR DE LA SOLUTION DETIENT TOUTES LES CAPACITES.
            //
            // Decision d'exploitation du 20/08 : sur ce site, l'administrateur
            // doit pouvoir tout faire depuis l'application, sans avoir a
            // s'attribuer un role supplementaire pour chaque geste.
            //
            // Ce que cela NE supprime PAS, et c'est ce qui rend la decision
            // tenable : le refus d'auto-approbation ne dependant pas des roles
            // mais des NOMS (ApproveExecutionUseCase compare le demandeur a
            // l'approbateur), un administrateur qui lance une operation ne peut
            // toujours pas approuver la sienne. Le controle a quatre yeux tient
            // donc, meme avec un compte qui detient tout.
            //
            // Restent egalement en place : le second facteur exige en
            // Production (SEC-001, pose dans le pre-check), le cloisonnement
            // par environnement, et la piste d'audit qui nomme l'acteur reel.
            //
            // Ce qui se perd, en revanche : sur un site n'ayant qu'un seul
            // compte, plus rien ne distingue qui administre de qui exploite. La
            // separation des roles reste recommandee pour la Production — elle
            // s'obtient en creant des comptes d'operateurs distincts, pas en
            // restreignant celui-ci.
            .AddPolicy(N4Policies.PeutExecuter, p =>
            {
                p.RequireRole(
                    N4Roles.OperateurN4, N4Roles.AdministrateurN4, N4Roles.AdministrateurSolution);
                Action(p);
            })

            // Actions sensibles (ex. SOP RequiresElevatedRole).
            .AddPolicy(N4Policies.PeutExecuterActionsSensibles, p =>
            {
                p.RequireRole(N4Roles.AdministrateurN4, N4Roles.AdministrateurSolution);
                Action(p);
            })

            // SEC-002 : action unitaire ad hoc sur un seul composant, distincte
            // de l'execution d'une operation complete meme si le perimetre de
            // roles est identique aujourd'hui — la separation permet a la DSI
            // de resserrer l'une sans toucher l'autre.
            .AddPolicy(N4Policies.PeutExecuterActionUnitaire, p =>
            {
                p.RequireRole(
                    N4Roles.OperateurN4, N4Roles.AdministrateurN4, N4Roles.AdministrateurSolution);
                Action(p);
            })

            // Approuver engage autant qu'executer : c'est la signature qui
            // autorise l'arret. Le second facteur s'y applique aussi.
            //
            // L'administrateur y figure, mais le refus d'auto-approbation le
            // rattrape : il peut approuver l'operation d'un autre, jamais la
            // sienne.
            .AddPolicy(N4Policies.PeutApprouver, p =>
            {
                p.RequireRole(N4Roles.Validateur, N4Roles.AdministrateurSolution);
                Action(p);
            })

            .AddPolicy(N4Policies.PeutAdministrerReferentiel, p => p.RequireRole(
                N4Roles.AdministrateurSolution))

            .AddPolicy(N4Policies.PeutAdministrerConnecteurs, p => p.RequireRole(
                N4Roles.AdministrateurInfrastructure,
                N4Roles.AdministrateurSolution))

            .AddPolicy(N4Policies.PeutAuditer, p => p.RequireRole(
                N4Roles.Auditeur,
                N4Roles.AdministrateurSolution));

        return services;
    }
}
