using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace N4Sentinel.Infrastructure.Identity;

/// <summary>
/// Point d'extension standard d'ASP.NET Core Identity pour bloquer la
/// connexion d'un compte désactivé (<see cref="ApplicationUser.IsDisabled"/>),
/// quel que soit le chemin emprunté — mot de passe, second facteur, connexion
/// externe (Azure AD/V2) ou clé de sécurité : tous passent par
/// <c>PreSignInCheck</c>, qui appelle <see cref="CanSignInAsync"/> en premier.
///
/// Sans ce point d'extension, un compte désactivé depuis l'écran « Comptes et
/// droits » resterait utilisable : le champ existait déjà dans le modèle mais
/// n'était vérifié nulle part (constat d'audit).
/// </summary>
public sealed class N4SignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(ApplicationUser user)
    {
        if (user.IsDisabled) return false;
        return await base.CanSignInAsync(user);
    }
}
