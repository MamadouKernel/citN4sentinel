namespace N4Sentinel.Domain;

/// <summary>
/// Abstraction du mécanisme de second facteur d'authentification (MFA).
/// Répond à l'exigence SEC-001 de ne pas coupler fortement l'application à une seule méthode.
/// </summary>
public interface IMfaProvider
{
    /// <summary>
    /// Génère un URI utilisable par une application d'authentification (ex: Google Authenticator) 
    /// ou pour générer un QRCode.
    /// </summary>
    string GenerateQrCodeUri(string email, string unformattedKey);

    /// <summary>
    /// Formate la clé secrète pour l'affichage (souvent avec des espaces).
    /// </summary>
    string FormatKey(string unformattedKey);
}
