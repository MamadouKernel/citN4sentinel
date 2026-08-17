namespace N4Sentinel.Domain;

/// <summary>
/// Paramètres de l'intégration Azure AD / SSO (SEC-001, prévue pour la V2).
/// Ligne unique, administrable — voir
/// <see cref="Infrastructure.Security.AzureAdSettingsService"/>.
///
/// <see cref="Enabled"/> gouverne l'AFFICHAGE de l'option de connexion SSO,
/// pas une intégration fonctionnelle : aucun client OpenID Connect réel
/// n'est câblé en V1. Ces paramètres préparent la V2 sans jamais autoriser
/// une connexion SSO tant que le connecteur réel n'existe pas — voir
/// <see cref="Infrastructure.Security.AzureAdAuthProvider"/>.
/// </summary>
public class AzureAdSettings : AuditableEntity
{
    public bool Enabled { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? Authority { get; set; }
    public string? PostLogoutRedirectUri { get; set; }
}
