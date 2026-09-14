namespace N4Sentinel.Domain;

/// <summary>
/// Paramètres de l'intégration Azure AD / Entra ID (SEC-001, V2).
///
/// Ces paramètres contrôlent deux chemins distincts :
///   (A) le flux OpenID Connect « réel » qui exige le package
///       Microsoft.AspNetCore.Authentication.OpenIdConnect (absent en V1,
///       à installer pour la production) ;
///   (B) le flux « id_token soumis » utilisé pour valider les claims (émetteur,
///       audience, dates, sujet) hors signature — pour P1, la signature n'est
///       vérifiée que si <see cref="RequireSignatureValidation"/> est désactivé,
///       car nous n'avons aucun package JWKS à disposition. Cette désactivation
///       est VERROUILLÉE tant qu'aucun administrateur ne l'a explicitement
///       autorisée (SEC-001 : prudence systématique).
/// </summary>
public class AzureAdSettings : AuditableEntity
{
    public bool Enabled { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? Authority { get; set; }
    public string? PostLogoutRedirectUri { get; set; }

    /// <summary>
    /// À false, les jetons sont acceptés UNIQUEMENT si nous pouvons valider
    /// leur signature RS256 contre la clé publique du tenant — impossible en
    /// V1 sans package. Par conséquent, tant que cette valeur reste true,
    /// AzureAdAuthProvider rejette systématiquement tout jeton.
    /// </summary>
    public bool RequireSignatureValidation { get; set; } = true;

    /// <summary>
    /// Quand true, le flux « login par id_token soumis » est activé dans
    /// N4SignInManager (débogage et recettes uniquement). DOIT rester false
    /// en production tant que le flow OIDC Authorization Code + PKCE n'est
    /// pas câblé dans Program.cs avec le package adéquat.
    /// </summary>
    public bool AllowTokenPastedLogin { get; set; } = false;

    /// <summary>
    /// Mapping des valeurs du claim « roles » d'Azure AD vers les rôles
    /// Sentinel. Clé = valeur du claim rôle Azure AD ; Valeur = nom du rôle
    /// Sentinel (<see cref="N4Roles.AdminN4"/> / <see cref="N4Roles.ExploitN4"/>
    /// / <see cref="N4Roles.LectureSeule"/>).
    /// </summary>
    public List<AzureAdRoleMapping> RoleMappings { get; set; } = [];
}

/// <summary>
/// Une règle de correspondance entre un rôle Azure AD (provenance claim
/// <c>roles</c> / <c>groups</c>) et un rôle Sentinel (AdminN4 / ExploitN4 /
/// LectureSeule). Stockée en JSON dans la colonne <c>RoleMappings</c>.
/// </summary>
public sealed class AzureAdRoleMapping
{
    public string AzureAdValue { get; set; } = string.Empty;
    public string SentinelRole { get; set; } = string.Empty;
}

