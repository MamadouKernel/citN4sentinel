using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace N4Sentinel.Web.Security;

/// <summary>
/// Exige que le compte soit protégé par un second facteur (audit SEC-A5).
///
/// LE TOTP ÉTAIT ACTIVABLE, PAS EXIGIBLE. Chaque utilisateur pouvait
/// l'activer — ce qui était la demande — mais rien ne permettait de l'imposer
/// aux comptes qui peuvent arrêter la Production. Un mot de passe seul suffisait
/// donc à obtenir le droit d'éteindre un terminal.
///
/// L'exigence est posée sur l'ACTION, jamais sur la consultation : un lecteur
/// N1 qui regarde un tableau de bord n'a aucune raison d'être empêché. Celui
/// qui lance une séquence d'arrêt, si.
/// </summary>
public sealed class SecondFacteurRequirement : IAuthorizationRequirement;

public sealed class SecondFacteurHandler : AuthorizationHandler<SecondFacteurRequirement>
{
    /// <summary>
    /// Revendication portée par Identity quand la session a été ouverte avec un
    /// second facteur, ou quand l'appareil est mémorisé après l'avoir été.
    /// </summary>
    public const string ClaimAmr = "amr";

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SecondFacteurRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return Task.CompletedTask;

        // Deux signaux acceptes :
        //   - amr = mfa : la session a ete ouverte avec le second facteur ;
        //   - AmrMfa : revendication equivalente posee par certains flux.
        var second = context.User.FindAll(ClaimAmr)
            .Any(c => string.Equals(c.Value, "mfa", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(c.Value, "otp", StringComparison.OrdinalIgnoreCase));

        if (second) context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
