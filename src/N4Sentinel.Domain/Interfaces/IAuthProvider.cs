namespace N4Sentinel.Domain.Interfaces;

/// <summary>
/// Abstraction minimale pour une source d'identité externe (Azure AD / Entra
/// ID aujourd'hui, étendue demain à Okta / PingIdentity sans toucher au reste).
///
/// La méthode <c>AuthenticateAsync</c> reçoit un jeton d'identité (OIDC
/// id_token) produit par le fournisseur et retourne :
///   - <c>null</c> : l'authentification est rejettée (jeton invalide, émetteur
///     inattendu, audience erronée, expiré, signature HS256/RS256 non
///     vérifiable dans la version actuelle).
///   - un <see cref="ExternalIdentity"/> : identité validée, exploitable par
///     <see cref="Infrastructure.Identity.N4SignInManager"/> pour rattacher
///     l'utilisateur Identity local (inscription à la volée, mise à jour des
///     rôles, contrôle du statut CanSignInAsync).
///
/// Aucun des implémentations de ce contrat ne doit FABRIQUER une identité à
/// partir d'une simple chaîne non vérifiée — principe « Prudence en cas
/// d'incertitude » (SEC-001).
/// </summary>
public interface IAuthProvider
{
    /// <summary>Nom technique du fournisseur — utilisé dans les journaux.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Tente d'authentifier un jeton d'identité provenant du fournisseur.
    /// </summary>
    /// <param name="idToken">Jeton au format JWT (header.payload.signature).</param>
    /// <param name="ct">Jeton d'annulation.</param>
    /// <returns>L'identité validée, ou null si le jeton n'est pas acceptable.</returns>
    Task<ExternalIdentity?> AuthenticateAsync(string idToken, CancellationToken ct = default);
}

/// <summary>
/// Identité externe validée, exploitable côté ASP.NET Core Identity.
/// La notion de « rôles » est volontairement une chaîne libre : le mapping
/// vers les rôles Sentinel (<see cref="N4Roles"/>) est fait au niveau service
/// via <c>AzureAdSettings.RoleClaimMappings</c>.
/// </summary>
public sealed record ExternalIdentity(
    string Email,
    string DisplayName,
    string TenantId,
    string SubjectId,
    IReadOnlyList<string> ProviderRoles);
