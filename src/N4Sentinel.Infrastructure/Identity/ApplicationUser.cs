using Microsoft.AspNetCore.Identity;

namespace N4Sentinel.Infrastructure.Identity;

/// <summary>
/// Utilisateur de N4 Sentinel.
///
/// SEC-001 — DÉCISION DSI DU 16/08/2026 : le second facteur retenu pour la V1
/// est un TOTP applicatif (Microsoft Authenticator, Google Authenticator ou
/// équivalent RFC 6238) via <c>EnableAuthenticator.razor</c>, et non l'e-mail
/// mentionné dans le texte initial du cahier des charges — un TOTP ne dépend
/// d'aucune boîte mail disponible au moment de l'incident, ce qu'un e-mail ne
/// garantit pas. Ce choix a été validé formellement par la DSI ; SEC-001 est
/// désormais considéré satisfait par cette implémentation, pas en écart.
///
/// Le second facteur est OBLIGATOIRE (pas seulement disponible) pour les
/// rôles Opérateur N4, Administrateur N4, Validateur, Administrateur
/// Infrastructure et Administrateur de la solution — voir
/// <c>Login.razor.RequiertSecondFacteurObligatoireAsync</c>, qui redirige
/// systématiquement vers son activation tant qu'il ne l'est pas.
///
/// L'intégration Azure AD / SSO reste prévue pour la V2 (voir
/// <see cref="N4Sentinel.Infrastructure.Security.AzureAdAuthProvider"/>,
/// dont les paramètres sont administrables mais l'activation désactivée en
/// V1) : le modèle reste compatible, Identity gérant nativement les
/// connexions externes.
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>Nom affiche dans les journaux, rapports et ecrans d'approbation.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Service ou equipe de rattachement (Solutions IT, Infrastructure, Support N1...).</summary>
    public string? Department { get; set; }

    /// <summary>
    /// Compte desactive : conserve pour la tracabilite des operations passees,
    /// mais ne peut plus se connecter. On ne supprime jamais un utilisateur
    /// qui apparait dans un historique d'operation.
    /// </summary>
    public bool IsDisabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastLoginAt { get; set; }
}
