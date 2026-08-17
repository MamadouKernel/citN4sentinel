using Microsoft.Extensions.Logging;

namespace N4Sentinel.Infrastructure.Security;

/// <summary>
/// Intégration SSO Azure AD / OpenID Connect (SEC-001, prévue pour la V2).
///
/// AUCUNE VALIDATION DE JETON RÉELLE N'EST IMPLÉMENTÉE. Une intégration OIDC
/// authentique exige la vérification de signature contre le JWKS du
/// tenant, le contrôle d'audience/émetteur et d'expiration — rien de tout
/// cela n'existe ici. Simuler un succès aurait fabriqué une identité DSI de
/// toutes pièces à partir de n'importe quelle chaîne fournie : c'est
/// exactement le risque que le principe "Prudence en cas d'incertitude" du
/// cahier des charges interdit. Cette méthode échoue donc TOUJOURS,
/// paramètres activés ou non, jusqu'à ce qu'un connecteur OIDC réel soit
/// développé en V2.
/// </summary>
public sealed class AzureAdAuthProvider(
    AzureAdSettingsService settings,
    ILogger<AzureAdAuthProvider> logger)
{
    public Task<AzureAdUserInfo?> AuthenticateSsoTokenAsync(string idToken, CancellationToken ct = default)
    {
        logger.LogWarning(
            "Tentative d'authentification SSO Azure AD refusée : aucun connecteur OIDC réel n'est implémenté en V1 (SEC-001, intégration prévue V2).");
        return Task.FromResult<AzureAdUserInfo?>(null);
    }

    public Task<Domain.AzureAdSettings> GetSettingsAsync(CancellationToken ct = default) => settings.GetAsync(ct);
}

public sealed class AzureAdUserInfo
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];
}
