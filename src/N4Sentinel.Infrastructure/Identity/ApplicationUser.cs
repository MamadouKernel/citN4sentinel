using Microsoft.AspNetCore.Identity;

namespace N4Sentinel.Infrastructure.Identity;

/// <summary>
/// Utilisateur de N4 Sentinel.
///
/// ÉCART ASSUMÉ AVEC LE TEXTE DE SEC-001 : le cahier des charges demande un
/// second facteur par e-mail en V1. Le code livre un TOTP applicatif
/// (Microsoft/Google Authenticator) via <c>EnableAuthenticator.razor</c>,
/// choix documenté dans <see cref="N4Sentinel.Web.Security"/>
/// (SmtpEmailSender.cs) : un TOTP ne dépend d'aucune boîte mail disponible au
/// moment de l'incident, ce qu'un e-mail ne garantit pas. Cet écart reste à
/// faire valider formellement par la DSI — voir doc/Backlog-V1.md.
/// L'intégration Azure AD / SSO est prévue en V2 : le modèle reste
/// compatible, Identity gérant nativement les connexions externes.
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
