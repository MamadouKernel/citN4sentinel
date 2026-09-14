using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace N4Sentinel.Infrastructure.Ai;

public sealed class VoiceSpeechOptions
{
    public const string SectionName = "N4Sentinel:VoiceCopilot:Speech";
    public bool Enabled { get; set; }
    public string Region { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string VoiceName { get; set; } = "fr-FR-VivienneMultilingualNeural";
    public string Locale { get; set; } = "fr-FR";
    public string VoiceLabel { get; set; } = "Voix française multilingue (naturelle)";
    public string CustomDeploymentId { get; set; } = "";
    public int RatePercent { get; set; } = -3;

    // Expressivité : rendent la lecture nettement plus humaine. Le style est
    // ignoré sans erreur par Azure si la voix retenue ne le prend pas en charge.
    public string Style { get; set; } = "friendly";
    public string StyleDegree { get; set; } = "1";
    public int PitchPercent { get; set; } = 0;

    // Uniquement les hôtes régionaux Azure : aucune URL provenant du navigateur.
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey)
        && Regex.IsMatch(Region, "^[a-z0-9]{2,40}$", RegexOptions.CultureInvariant)
        && !string.IsNullOrWhiteSpace(VoiceName) && VoiceName.Length <= 100
        && Regex.IsMatch(Locale, "^fr-[A-Z]{2}$", RegexOptions.CultureInvariant)
        && (string.IsNullOrWhiteSpace(CustomDeploymentId) || Guid.TryParse(CustomDeploymentId, out _));

    public Uri GetEndpoint() => string.IsNullOrWhiteSpace(CustomDeploymentId)
        ? new($"https://{Region}.tts.speech.microsoft.com/cognitiveservices/v1")
        : new($"https://{Region}.voice.speech.microsoft.com/cognitiveservices/v1?deploymentId={Uri.EscapeDataString(CustomDeploymentId)}");
}

/// <summary>
/// Lecture neuronale facultative des réponses du copilote. La clé reste côté
/// serveur ; le navigateur conserve sa synthèse en cas d'absence ou d'échec.
/// Aucun appel payant n'est émis sans activation et configuration explicites.
/// </summary>
public sealed class VoiceSpeechService(
    HttpClient http,
    IOptions<VoiceSpeechOptions> options,
    ILogger<VoiceSpeechService> logger)
{
    public const int MaxTextLength = 1800;
    public VoiceSpeechOptions Settings => options.Value;

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            throw new ArgumentException("La réponse vocale doit contenir entre 1 et 1800 caractères.", nameof(text));

        if (!Settings.IsConfigured) return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, Settings.GetEndpoint());
        request.Headers.Add("Ocp-Apim-Subscription-Key", Settings.ApiKey);
        request.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
        request.Headers.UserAgent.ParseAdd("N4Sentinel-VoiceCopilot/1.0");
        request.Content = new StringContent(BuildSsml(text, Settings), Encoding.UTF8, "application/ssml+xml");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Ne pas journaliser texte, clé ou corps de réponse du fournisseur.
                logger.LogWarning("Synthèse vocale indisponible : HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            var audio = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            return audio.Length > 0 ? audio : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Délai de synthèse vocale dépassé ; repli sur la voix du navigateur.");
            return null;
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Service de synthèse vocale injoignable ; repli sur la voix du navigateur.");
            return null;
        }
    }

    internal static string BuildSsml(string text, VoiceSpeechOptions settings)
    {
        // Les sigles s'énoncent lettre par lettre ; la ponctuation porte les pauses.
        var spoken = Regex.Replace(text, @"\b(N4|CPU|RAM|NTP|EDI|SOP|SLA|SSO|DSI)\b", match => match.Value switch
        {
            "N4" => "enne quatre",
            "CPU" => "cé pé u",
            "RAM" => "mémoire vive",
            "NTP" => "enne té pé",
            "EDI" => "é dé i",
            "SOP" => "ès o pé",
            "SLA" => "ès elle a",
            "SSO" => "ès ès o",
            "DSI" => "dé ès i",
            _ => match.Value
        }, RegexOptions.CultureInvariant);

        XNamespace ns = "http://www.w3.org/2001/10/synthesis";
        XNamespace mstts = "http://www.w3.org/2001/mstts";

        var rate = Math.Clamp(settings.RatePercent, -20, 20).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%";
        var pitch = Math.Clamp(settings.PitchPercent, -20, 20).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%";

        // Prosodie : débit légèrement ralenti et hauteur ajustable, pour une
        // diction posée plutôt que mécanique.
        var prosody = new XElement(ns + "prosody",
            new XAttribute("rate", rate),
            new XAttribute("pitch", pitch),
            spoken);

        // Style conversationnel optionnel. Valeurs bornées/validées ici (jamais du
        // texte utilisateur) ; Azure retombe sur le style neutre si non supporté.
        XElement spokenNode = prosody;
        var style = Regex.IsMatch(settings.Style ?? "", "^[a-zA-Z-]{1,40}$", RegexOptions.CultureInvariant)
            ? settings.Style
            : "";
        if (!string.IsNullOrEmpty(style))
        {
            var degree = double.TryParse(settings.StyleDegree, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? Math.Clamp(d, 0.01, 2).ToString("0.##", CultureInfo.InvariantCulture)
                : "1";
            spokenNode = new XElement(mstts + "express-as",
                new XAttribute("style", style),
                new XAttribute("styledegree", degree),
                prosody);
        }

        // XElement échappe tout texte utilisateur : aucune balise SSML injectée.
        // La pause de fin de phrase donne une cadence plus humaine, moins hachée.
        return new XElement(ns + "speak", new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xml + "lang", settings.Locale),
            new XAttribute(XNamespace.Xmlns + "mstts", mstts.NamespaceName),
            new XElement(ns + "voice", new XAttribute("name", settings.VoiceName),
                new XElement(mstts + "silence",
                    new XAttribute("type", "Sentenceboundary"),
                    new XAttribute("value", "180ms")),
                spokenNode))
            .ToString(SaveOptions.DisableFormatting);
    }
}
