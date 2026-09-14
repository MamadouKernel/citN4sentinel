using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using N4Sentinel.Domain;
using N4Sentinel.Domain.Interfaces;

namespace N4Sentinel.Infrastructure.Identity;

/// <summary>
/// Point d'extension standard d'ASP.NET Core Identity pour :
///   (1) Bloquer la connexion d'un compte désactivé (<see cref="ApplicationUser.IsDisabled"/>),
///       quel que soit le chemin (mot de passe, 2FA, SSO Azure AD, clé de sécurité).
///   (2) Prendre en charge le flux SSO Azure AD configuré dans
///       <see cref="Security.AzureAdSettings"/> — DÉSACTIVÉ PAR DÉFAUT tant que
///       <c>RequireSignatureValidation=true</c> ou <c>AllowTokenPastedLogin=false</c>.
///
/// Le SSO est donc « configuré mais désactivé » : l'UI Admin permet de renseigner
/// TenantId/ClientId/Authority/mappage rôles, tester la validation d'intégrité du
/// jeton hors signature en recettes, mais AUCUN login OIDC réel n'est accepté tant
/// que les deux gardes d'activation ne sont pas explicitement levées par un Admin.
/// </summary>
public sealed class N4SignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    IServiceProvider serviceProvider)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(ApplicationUser user)
    {
        if (user.IsDisabled) return false;
        return await base.CanSignInAsync(user);
    }

    /// <summary>
    /// Tente une authentification SSO en mode « coller un id_token » uniquement.
    /// Retourne :
    ///   - null : échec (SSO désactivé, signature exigée, audience erronée, dates…).
    ///   - SignInResult.Success + rattachement au user Identity local (création
    ///     à la volée si l'email n'existe pas encore ET que
    ///     <c>AzureAdSettings.AllowAutoProvisioning=false</c> par défaut → refus).
    ///
    /// Cette méthode est le « connecteur » appelé depuis la page Login.razor
    /// si le bouton SSO est affiché. DANS L'ETAT ACTUEL (par défaut), elle
    /// ECHOUE SYSTEMATIQUEMENT : Enabled=false ou RequireSignatureValidation=true
    /// → garde-fou SEC-001.
    /// </summary>
    public async Task<ExternalSsoLoginResult> PasswordlessSsoLoginFromTokenAsync(
        string idToken, bool isPersistent, CancellationToken ct = default)
    {
        var auth = serviceProvider.GetRequiredService<IAuthProvider>();
        if (auth is null)
        {
            Logger.LogWarning("[S3-08 SSO] Aucun IAuthProvider enregistré (DI).");
            return ExternalSsoLoginResult.Failed("Connecteur SSO non enregistré.");
        }

        var identite = await auth.AuthenticateAsync(idToken, ct);
        if (identite is null)
            return ExternalSsoLoginResult.Failed(
                "Authentification SSO refusée. Vérifiez l'activation, la signature (ou le mode recettes sans signature) et la validité du jeton.");

        // Récupérer mapping de rôles (s'il y a un AzureAdProvider).
        IReadOnlyList<string> sentinelRoles = [];
        if (auth is Security.AzureAdAuthProvider aadProv)
        {
            var settings = await aadProv.GetSettingsAsync(ct);
            sentinelRoles = aadProv.ApplyRoleMappings(settings, identite.ProviderRoles);

            if (!settings.AllowTokenPastedLogin)
            {
                Logger.LogWarning(
                    "[S3-08 SSO] Jeton accepté mais AllowTokenPastedLogin=false — refusé par configuration (SEC-001 double-garde).");
                return ExternalSsoLoginResult.Failed(
                    "Jeton valide mais le flux « Coller un token SSO » est désactivé. Rapprochez-vous d'un Admin pour activer ce mode de recettes.");
            }
        }

        // --- Rattacher au compte local. ---
        var user = await UserManager.FindByEmailAsync(identite.Email);
        if (user is null)
        {
            Logger.LogWarning(
                "[S3-08 SSO] Identité {Email} acceptée mais aucun compte Identity local n'existe avec cet email (provisioning automatique interdit en V1).",
                identite.Email);
            return ExternalSsoLoginResult.Failed(
                $"Compte SSO reconnu ({identite.Email}) mais aucun compte utilisateur N4 Sentinel ne porte cet email. Créez d'abord le compte dans Comptes & droits.");
        }

        if (!await CanSignInAsync(user))
        {
            Logger.LogWarning("[S3-08 SSO] Compte {Email} désactivé — CanSignIn=false.", identite.Email);
            return ExternalSsoLoginResult.Failed("Compte désactivé.");
        }

        // Appliquer les rôles du mapping (s'il y en a).
        if (sentinelRoles.Count > 0)
        {
            var rolesActuels = await UserManager.GetRolesAsync(user);
            foreach (var r in sentinelRoles)
            {
                if (!rolesActuels.Contains(r, StringComparer.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(r))
                {
                    _ = await UserManager.AddToRoleAsync(user, r);
                }
            }
        }

        await Context.SignOutAsync(IdentityConstants.ExternalScheme);
        await SignInAsync(user, isPersistent, auth.ProviderName);

        Logger.LogInformation(
            "[S3-08 SSO] Connexion réussie {Email} via {Provider}. Rôles appliqués Sentinel : {Roles}.",
            identite.Email, auth.ProviderName, string.Join(", ", sentinelRoles.DefaultIfEmpty("(aucun)")));

        return ExternalSsoLoginResult.Success(user.Id, identite.Email, sentinelRoles);
    }
}

public sealed class ExternalSsoLoginResult
{
    public bool Succeeded { get; init; }
    public string? UserId { get; init; }
    public string? Email { get; init; }
    public IReadOnlyList<string>? Roles { get; init; }
    public string? ErrorMessage { get; init; }

    public static ExternalSsoLoginResult Success(string userId, string email, IReadOnlyList<string> roles) => new()
    {
        Succeeded = true,
        UserId = userId,
        Email = email,
        Roles = roles
    };

    public static ExternalSsoLoginResult Failed(string message) => new()
    {
        Succeeded = false,
        ErrorMessage = message
    };
}

