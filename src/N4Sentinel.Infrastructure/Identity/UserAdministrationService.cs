using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Identity;

public sealed record UserAdminResult
{
    public bool Succeeded { get; init; }
    public string? Error { get; init; }
    public static UserAdminResult Ok() => new() { Succeeded = true };
    public static UserAdminResult Failed(string error) => new() { Succeeded = false, Error = error };
}

/// <summary>
/// Gestion opérationnelle des comptes et des rôles (FR-091, SEC-002).
///
/// Réservée à l'Administrateur de la solution
/// (<see cref="N4Policies.PeutAdministrerReferentiel"/>) : double contrôle,
/// côté écran (<c>AuthorizeView</c>) ET côté service (<c>callerHasElevatedRole</c>),
/// même principe que <c>SopExecutionService</c> pour les SOP sensibles — un
/// écran caché n'est pas une autorisation, seulement une commodité.
///
/// Aucune méthode ici ne choisit ou ne connaît un mot de passe à la place de
/// l'utilisateur (SEC-003) : un compte créé reçoit un lien pour EN DÉFINIR un,
/// exactement comme le parcours « mot de passe oublié ».
/// </summary>
public sealed class UserAdministrationService(
    UserManager<ApplicationUser> userManager,
    IEmailSender<ApplicationUser> emailSender,
    IAuditWriter auditWriter,
    ILogger<UserAdministrationService> logger)
{
    private async Task<bool> RefuserSiNonHabiliteAsync(string actor, bool callerHasElevatedRole, string action, CancellationToken ct)
    {
        if (callerHasElevatedRole) return false;

        await auditWriter.WriteAsync(
            AuditAction.TentativeNonAutorisee, AuditOutcome.Echec, actor,
            entityType: nameof(ApplicationUser),
            reason: $"{action} tentée sans le droit d'administration du référentiel.",
            ct: ct);
        return true;
    }

    public async Task<UserAdminResult> CreateAsync(
        string email, string? displayName, string? department, IReadOnlyCollection<string> roles,
        string actor, bool callerHasElevatedRole, string resetPasswordPageUrl, CancellationToken ct = default)
    {
        if (await RefuserSiNonHabiliteAsync(actor, callerHasElevatedRole, "Création de compte", ct))
            return UserAdminResult.Failed("Seul un Administrateur de la solution peut créer un compte.");

        if (string.IsNullOrWhiteSpace(email)) return UserAdminResult.Failed("Adresse courriel obligatoire.");
        email = email.Trim();

        if (await userManager.FindByEmailAsync(email) is not null)
            return UserAdminResult.Failed("Un compte existe déjà pour cette adresse.");

        var roleNames = roles.Where(r => N4Roles.All.Contains(r)).Distinct().ToList();
        if (roleNames.Count == 0) return UserAdminResult.Failed("Au moins un rôle est requis.");

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            // L'administrateur qui crée le compte en vérifie l'existence — ce
            // n'est pas un auto-enregistrement, la confirmation par courriel
            // n'a pas lieu d'être redemandée.
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            Department = string.IsNullOrWhiteSpace(department) ? null : department.Trim()
        };

        // Créé sans mot de passe : UserManager.CreateAsync(user) sans second
        // argument ne pose pas de hash. L'utilisateur en choisit un lui-même
        // via le lien ci-dessous.
        var created = await userManager.CreateAsync(user);
        if (!created.Succeeded)
            return UserAdminResult.Failed(string.Join(" | ", created.Errors.Select(e => e.Description)));

        await userManager.AddToRolesAsync(user, roleNames);

        var code = await userManager.GeneratePasswordResetTokenAsync(user);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
        var callbackUrl = QueryHelpers.AddQueryString(resetPasswordPageUrl, "code", code);
        await emailSender.SendPasswordResetLinkAsync(user, email, callbackUrl);

        await auditWriter.WriteAsync(
            AuditAction.Creation, AuditOutcome.Succes, actor,
            entityType: nameof(ApplicationUser), entityId: user.Id, entityLabel: email,
            detail: $"Rôle(s) initial(aux) : {string.Join(", ", roleNames)}", ct: ct);

        logger.LogInformation("Compte créé par {Actor} pour {Email}, rôle(s) {Roles}.", actor, email, string.Join(", ", roleNames));
        return UserAdminResult.Ok();
    }

    public async Task<UserAdminResult> ChangeRolesAsync(
        string userId, IReadOnlyCollection<string> roles, string actor, bool callerHasElevatedRole, CancellationToken ct = default)
    {
        if (await RefuserSiNonHabiliteAsync(actor, callerHasElevatedRole, "Changement de rôle", ct))
            return UserAdminResult.Failed("Seul un Administrateur de la solution peut modifier les rôles.");

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return UserAdminResult.Failed("Compte introuvable.");

        var roleNames = roles.Where(r => N4Roles.All.Contains(r)).Distinct().ToList();
        if (roleNames.Count == 0) return UserAdminResult.Failed("Au moins un rôle est requis.");

        var current = (await userManager.GetRolesAsync(user)).ToList();
        var aRetirer = current.Except(roleNames).ToList();
        var aAjouter = roleNames.Except(current).ToList();

        if (aRetirer.Count == 0 && aAjouter.Count == 0) return UserAdminResult.Ok();

        if (aRetirer.Count > 0) await userManager.RemoveFromRolesAsync(user, aRetirer);
        if (aAjouter.Count > 0) await userManager.AddToRolesAsync(user, aAjouter);

        await auditWriter.WriteAsync(
            AuditAction.Modification, AuditOutcome.Succes, actor,
            entityType: nameof(ApplicationUser), entityId: user.Id, entityLabel: user.Email,
            detail: $"Rôles : [{string.Join(", ", current)}] → [{string.Join(", ", roleNames)}]", ct: ct);

        return UserAdminResult.Ok();
    }

    public async Task<UserAdminResult> SetDisabledAsync(
        string userId, bool disabled, string actor, bool callerHasElevatedRole, CancellationToken ct = default)
    {
        if (await RefuserSiNonHabiliteAsync(actor, callerHasElevatedRole, disabled ? "Désactivation de compte" : "Réactivation de compte", ct))
            return UserAdminResult.Failed("Seul un Administrateur de la solution peut désactiver ou réactiver un compte.");

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return UserAdminResult.Failed("Compte introuvable.");

        if (disabled && string.Equals(user.Email, actor, StringComparison.OrdinalIgnoreCase))
            return UserAdminResult.Failed("Vous ne pouvez pas désactiver votre propre compte.");

        if (user.IsDisabled == disabled) return UserAdminResult.Ok();

        user.IsDisabled = disabled;
        await userManager.UpdateAsync(user);

        // Invalide toute session déjà ouverte avant la prochaine revalidation
        // périodique du cookie — désactiver un compte doit couper l'accès,
        // pas seulement empêcher une future connexion.
        if (disabled) await userManager.UpdateSecurityStampAsync(user);

        await auditWriter.WriteAsync(
            AuditAction.ChangementDeStatut, AuditOutcome.Succes, actor,
            entityType: nameof(ApplicationUser), entityId: user.Id, entityLabel: user.Email,
            detail: disabled ? "Compte désactivé." : "Compte réactivé.", ct: ct);

        return UserAdminResult.Ok();
    }

    public async Task<UserAdminResult> ResetTwoFactorAsync(
        string userId, string actor, bool callerHasElevatedRole, CancellationToken ct = default)
    {
        if (await RefuserSiNonHabiliteAsync(actor, callerHasElevatedRole, "Révocation du second facteur", ct))
            return UserAdminResult.Failed("Seul un Administrateur de la solution peut révoquer un second facteur.");

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return UserAdminResult.Failed("Compte introuvable.");

        await userManager.SetTwoFactorEnabledAsync(user, false);
        await userManager.ResetAuthenticatorKeyAsync(user);

        await auditWriter.WriteAsync(
            AuditAction.Modification, AuditOutcome.Succes, actor,
            entityType: nameof(ApplicationUser), entityId: user.Id, entityLabel: user.Email,
            detail: "Second facteur révoqué — l'utilisateur devra le reconfigurer.", ct: ct);

        return UserAdminResult.Ok();
    }
}
