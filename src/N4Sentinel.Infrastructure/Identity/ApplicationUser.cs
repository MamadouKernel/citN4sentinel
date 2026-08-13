using Microsoft.AspNetCore.Identity;

namespace N4Sentinel.Infrastructure.Identity;

/// <summary>
/// Utilisateur de N4 Sentinel.
///
/// L'authentification V1 repose sur des identifiants applicatifs avec MFA par
/// e-mail (SEC-001). L'integration Azure AD / SSO est prevue en V2 : le modele
/// reste compatible, Identity gerant nativement les connexions externes.
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
