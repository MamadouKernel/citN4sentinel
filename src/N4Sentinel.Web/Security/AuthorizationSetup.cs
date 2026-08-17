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
    public static IServiceCollection AddN4SentinelAuthorization(this IServiceCollection services)
    {
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

            // Executer, c'est agir sur la Production. Ni le Validateur ni
            // l'Auditeur ne figurent ici : approuver et controler ne donnent
            // pas le droit d'executer.
            .AddPolicy(N4Policies.PeutExecuter, p => p.RequireRole(
                N4Roles.OperateurN4,
                N4Roles.AdministrateurN4))

            // Sous-ensemble de PeutExecuter : actions sensibles (ex. SOP
            // RequiresElevatedRole) reservees a l'Administrateur N4.
            .AddPolicy(N4Policies.PeutExecuterActionsSensibles, p => p.RequireRole(
                N4Roles.AdministrateurN4))

            // SEC-002 : action unitaire ad hoc sur un seul composant, distincte
            // de l'execution d'une operation complete meme si le perimetre de
            // roles est identique aujourd'hui — la separation permet a la DSI
            // de resserrer l'une sans toucher l'autre.
            .AddPolicy(N4Policies.PeutExecuterActionUnitaire, p => p.RequireRole(
                N4Roles.OperateurN4,
                N4Roles.AdministrateurN4))

            .AddPolicy(N4Policies.PeutApprouver, p => p.RequireRole(
                N4Roles.Validateur))

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
