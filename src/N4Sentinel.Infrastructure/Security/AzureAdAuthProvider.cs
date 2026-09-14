using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Domain.Interfaces;

namespace N4Sentinel.Infrastructure.Security;

/// <summary>
/// Intégration SSO Azure AD / Entra ID (SEC-001 · S3-08).
///
/// Cette implémentation exploite deux modes distincts :
///  (A) Mode production — utilise <see cref="Microsoft.AspNetCore.Authentication.OpenIdConnect"/>
///      (package NuGet ABSENT en V1). Dans ce mode, ASP.NET Core Identity s'en
///      sert de ExternalLoginCallback normal ; ce service n'intervient que pour le
///      mapping rôles après coup (voir <see cref="ApplyRoleMappings"/>).
///
///  (B) Mode recette / P1 — un opérateur colle un id_token OIDC, on décode
///      le JWT, on valide les claims d'intégrité (émetteur, audience, dates
///      + sujet), puis on retourne un <see cref="ExternalIdentity"/>.
///      Dans les DEUX CAS, la validation de SIGNATURE RS256 n'est VÉRIFIÉE que si
///      AzureAdSettings.RequireSignatureValidation = false. Tant que
///      RequireSignatureValidation = true (défaut), cette classe rejette TOUT
///      jeton. C'est une garde explicite contre l'excès : on n'invente pas une
///      connexion valide faute de lib.
/// </summary>
public sealed class AzureAdAuthProvider(
    AzureAdSettingsService settingsService,
    ILogger<AzureAdAuthProvider> logger) : IAuthProvider
{
    public string ProviderName => "AzureAD";

    private static readonly JsonSerializerOptions JwtJsonOptions = new() { PropertyNameCaseInsensitive = true };

    // -----------------------------------------------------------------------
    // IAuthProvider
    // -----------------------------------------------------------------------
    public async Task<ExternalIdentity?> AuthenticateAsync(string idToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        var parametres = await settingsService.GetAsync(ct);
        if (!parametres.Enabled)
        {
            logger.LogWarning("[AzureAd S3-08 Tentative SSO refusée : le SSO Azure AD est désactivé (Enabled=false).");
            return null;
        }

        // Vérifier les conditions d'activation (SEC-001 double-garde).
        if (parametres.RequireSignatureValidation)
        {
            logger.LogWarning(
                "[AzureAd S3-08 Tentative SSO refusée : RequireSignatureValidation=true, et aucun connecteur OIDC/JWKS n'est disponible en V1. "
                + "Positionnez ce paramètre à false en recettes si vous voulez utiliser le flux « soumission d'id_token hors signature » pour la recette P1.");
            return null;
        }

        var (payload, erreur) = DecoderJwtPayload(idToken);
        if (payload is null)
        {
            logger.LogWarning("[AzureAd] Jeton OIDC invalide : {Erreur}", erreur);
            return null;
        }

        // --- Contrôles d'intégrité (claims minimaux) ----------
        var issuer = GetString(payload, "iss");
        var audience = GetString(payload, "aud");
        var subject = GetString(payload, "sub");
        var email = GetString(payload, "email") ?? GetString(payload, "upn");
        var name = GetString(payload, "name") ?? GetString(payload, "displayName") ?? email ?? string.Empty;
        var tenant = GetString(payload, "tid") ?? parametres.TenantId ?? string.Empty;

        if (parametres.Authority is not null && issuer is not null)
        {
            // Le issuer attendu = Authority sans '/' + "v2.0"
            var issuerAttendu = parametres.Authority.TrimEnd('/');
            if (!issuer.StartsWith(issuerAttendu, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("[AzureAd] Issuer du jeton inattendu : reçu={Recu}, attendu prefixe={Attendu}", issuer, issuerAttendu);
                return null;
            }
        }

        if (!string.IsNullOrEmpty(parametres.ClientId)
            && !string.Equals(audience, parametres.ClientId, StringComparison.Ordinal)
            && !EqualsAudienceList(payload, parametres.ClientId))
        {
            logger.LogWarning("[AzureAd] Audience erronée : aud={Aud}, attendu={Attendu}", audience, parametres.ClientId);
            return null;
        }

        var nowUnixEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (TryGetLong(payload, "exp", out var exp) && exp < nowUnixEpoch - 60)
        {
            logger.LogWarning("[AzureAd] Jeton expiré : exp={Exp}, maintenant={Now}", exp, nowUnixEpoch);
            return null;
        }
        if (TryGetLong(payload, "nbf", out var nbf) && nbf > nowUnixEpoch + 60)
        {
            logger.LogWarning("[AzureAd] Jeton pas encore valide (nbf futur).");
            return null;
        }
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
        {
            logger.LogWarning("[AzureAd] Jeton valide mais claims identité vides (sub/email).");
            return null;
        }

        var roles = new List<string>();
        if (payload.TryGetValue("roles", out var rolesRaw) && rolesRaw is JsonElement rolesElem)
        {
            if (rolesElem.ValueKind == JsonValueKind.Array)
                foreach (var r in rolesElem.EnumerateArray())
                    if (r.GetString() is string v) roles.Add(v);
        }
        if (payload.TryGetValue("groups", out var grpRaw) && grpRaw is JsonElement grpElem)
        {
            if (grpElem.ValueKind == JsonValueKind.Array)
                foreach (var r in grpElem.EnumerateArray())
                    if (r.GetString() is string v) roles.Add(v);
        }

        logger.LogInformation("[AzureAd] Jetons OIDC accepté (mode recette sans signature) : email={Email}, sub={Sub}", email, subject);

        return new ExternalIdentity(email, name, tenant, subject, roles);
    }

    // -----------------------------------------------------------------------
    // Utilitaires
    // -----------------------------------------------------------------------
    public async Task<AzureAdSettings> GetSettingsAsync(CancellationToken ct = default)
        => await settingsService.GetAsync(ct);

    /// <summary>
    /// Applique le mapping des rôles AAD → rôles Sentinel.
    /// Retourne : rôles Sentinel reconnus (intersection avec N4Roles) — jamais
    /// de rôles « inventés » — on ne prend que ce que le mapping déclare.
    /// </summary>
    public IReadOnlyList<string> ApplyRoleMappings(
        AzureAdSettings parametres, IReadOnlyList<string> providerRoles)
    {
        if (parametres.RoleMappings is not { Count: > 0 }) return [];
        var resultat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var regle in parametres.RoleMappings)
        {
            if (string.IsNullOrWhiteSpace(regle.AzureAdValue) || string.IsNullOrWhiteSpace(regle.SentinelRole)) continue;
            if (providerRoles.Contains(regle.AzureAdValue, StringComparer.OrdinalIgnoreCase))
                resultat.Add(regle.SentinelRole);
        }
        return resultat.ToArray();
    }

    // -----------------------------------------------------------------------
    // Interne : décodage base64url JWT payload
    // -----------------------------------------------------------------------
    private static (Dictionary<string, object?>? Payload, string? Erreur) DecoderJwtPayload(string jeton)
    {
        var parties = jeton.Split('.');
        if (parties.Length < 2) return (null, "Jeton n'est pas un JWT (manque header.payload.signature).");
        var payload64 = parties[1];
        try
        {
            // base64url -> bytes -> utf8 string
            var sortie = new char[payload64.Length + ((4 - payload64.Length % 4) % 4)];
            payload64.CopyTo(0, sortie, 0, payload64.Length);
            for (var i = 0; i < sortie.Length; i++) if (sortie[i] == '-') sortie[i] = '+';
            for (var i = 0; i < sortie.Length; i++) if (sortie[i] == '_') sortie[i] = '/';
            switch (sortie.Length % 4)
            {
                case 2: sortie[^2] = '='; sortie[^1] = '='; break;
                case 3: sortie[^1] = '='; break;
            }
            var octets = Convert.FromBase64CharArray(sortie, 0, sortie.Length);
            var json = Encoding.UTF8.GetString(octets);
            var doc = JsonDocument.Parse(json);
            var dico = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject()) dico[prop.Name] = Box(prop.Value);
            return (dico, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static object? Box(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => e.Clone()
    };

    private static string? GetString(Dictionary<string, object?> d, string k)
        => d.TryGetValue(k, out var v) switch
        {
            true => v switch
            {
                string s => s,
                JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
                _ => null
            },
            _ => null
        };

    private static bool TryGetLong(Dictionary<string, object?> d, string k, out long valeur)
    {
        valeur = default;
        if (!d.TryGetValue(k, out var v) || v is null) return false;
        if (v is long l) { valeur = l; return true; }
        if (v is int i) { valeur = i; return true; }
        if (v is double dbl) { valeur = (long)dbl; return true; }
        if (v is JsonElement el)
        {
            if (el.TryGetInt64(out var ll)) { valeur = ll; return true; }
        }
        return false;
    }

    private static bool EqualsAudienceList(Dictionary<string, object?> payload, string clientId)
    {
        if (!payload.TryGetValue("aud", out var audRaw) || audRaw is not JsonElement elem) return false;
        if (elem.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in elem.EnumerateArray())
                if (string.Equals(a.GetString(), clientId, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

