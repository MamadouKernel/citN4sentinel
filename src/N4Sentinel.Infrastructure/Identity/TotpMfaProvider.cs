using System.Text;
using System.Text.Encodings.Web;
using N4Sentinel.Domain;

namespace N4Sentinel.Infrastructure.Identity;

public class TotpMfaProvider(UrlEncoder urlEncoder) : IMfaProvider
{
    private const string AuthenticatorUriFormat = "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6";
    private const string Issuer = "N4Sentinel";

    public string GenerateQrCodeUri(string email, string unformattedKey)
    {
        return string.Format(
            AuthenticatorUriFormat,
            urlEncoder.Encode(Issuer),
            urlEncoder.Encode(email),
            unformattedKey);
    }

    public string FormatKey(string unformattedKey)
    {
        var result = new StringBuilder();
        int currentPosition = 0;
        while (currentPosition + 4 < unformattedKey.Length)
        {
            result.Append(unformattedKey.AsSpan(currentPosition, 4)).Append(' ');
            currentPosition += 4;
        }
        if (currentPosition < unformattedKey.Length)
        {
            result.Append(unformattedKey.AsSpan(currentPosition));
        }

        return result.ToString().ToLowerInvariant();
    }
}
