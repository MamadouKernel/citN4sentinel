using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace N4Sentinel.Infrastructure.Notifications;

/// <summary>
/// S3-06 / PB-22 · Notifications multi-canaux résilientes V1.
///
/// Canaux disponibles (priorité déclarante) :
///   (1) Webhook Microsoft Teams (JSON carte adaptative)
///   (2) Webhook Slack (JSON message)
///   (3) Canal de repli : <see cref="InnerSender"/> (typiquement SmtpEmailSender).
///
/// Stratégie de résilience SANS package (règle NFR-009 · ZÉRO package supplémentaire) :
///   - Retry exponentiel 1 / 2 / 4 s (3 tentatives max par canal).
///   - Échec d'un canal = journal + passage au canal suivant.
///   - Échec des 3 canaux = exception AVALÉE (l'orchestration ne doit pas
///     être bloquée par une notification — RG-022).
///
/// Tous les webhooks sont désactivés par défaut tant que l'URL n'est pas
/// renseignée dans appsettings — SEC-001 : AUCUN appel HTTP n'est émis
/// si les URLs sont vides.
/// </summary>
public sealed class ResilientMultiChannelNotifier(
    IEnumerable<INotificationSender> innerSenders,
    IHttpClientFactory httpFactory,
    IOptions<NotificationChannelsOptions> options,
    ILogger<ResilientMultiChannelNotifier> logger)
    : INotificationSender
{
    public const int MaxRetriesParCanal = 3;
    private static readonly TimeSpan[] BackoffDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)
    ];

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var opt = options.Value;
        bool auMoinsUnCanalAReussi = false;

        // --- Canal #1 · Teams webhook (carte adaptative minimaliste). ---
        if (!string.IsNullOrWhiteSpace(opt.TeamsWebhookUrl))
        {
            auMoinsUnCanalAReussi |= await EssayerCanal(
                "Teams",
                () => EmettreTeamsAsync(opt.TeamsWebhookUrl, to, subject, htmlBody, ct), ct);
        }

        // --- Canal #2 · Slack webhook. ---
        if (!string.IsNullOrWhiteSpace(opt.SlackWebhookUrl))
        {
            auMoinsUnCanalAReussi |= await EssayerCanal(
                "Slack",
                () => EmettreSlackAsync(opt.SlackWebhookUrl, to, subject, htmlBody, ct), ct);
        }

        // --- Canal #3 · Senders internes (Smtp / Identity). ---
        foreach (var inner in innerSenders)
        {
            if (ReferenceEquals(inner, this)) continue;
            auMoinsUnCanalAReussi |= await EssayerCanal(
                inner.GetType().Name,
                () => inner.SendAsync(to, subject, htmlBody, ct), ct);
        }

        if (!auMoinsUnCanalAReussi)
        {
            logger.LogWarning(
                "[PB-22 Notifications] AUCUN canal n'a pu émettre '{Sujet}' vers {Destinataire} (3 canaux × 3 tentatives = 9 essais épuisés). " +
                "L'opération métier continue malgré tout (RG-022 · échec notification ≠ échec opération).",
                subject, to);
        }
    }

    // -------------------------------------------------------------------
    // Retry custom — remplace Polly pour respecter NFR-009.
    // -------------------------------------------------------------------
    private async Task<bool> EssayerCanal(string nomCanal, Func<Task> emission, CancellationToken ct)
    {
        for (int tentative = 1; tentative <= MaxRetriesParCanal; tentative++)
        {
            try
            {
                await emission();
                if (tentative > 1)
                    logger.LogInformation("[PB-22 Notifications] {Canal} réussi après {T} tentatives.", nomCanal, tentative);
                return true;
            }
            catch (Exception ex) when (tentative < MaxRetriesParCanal)
            {
                var delai = BackoffDelays[tentative - 1];
                logger.LogWarning(
                    ex,
                    "[PB-22 Notifications] Échec émission {Canal} tentative {T}/{Max} — retry dans {D} ms.",
                    nomCanal, tentative, MaxRetriesParCanal, delai.TotalMilliseconds);
                await Task.Delay(delai, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "[PB-22 Notifications] Échec définitif canal {Canal} ({T}/{Max}). Passage au canal suivant.",
                    nomCanal, tentative, MaxRetriesParCanal);
                return false;
            }
        }
        return false;
    }

    // -------------------------------------------------------------------
    // Canaux HTTP — Teams / Slack. HttpClient via IHttpClientFactory.
    // -------------------------------------------------------------------
    private async Task EmettreTeamsAsync(
        string webhookUrl, string to, string subject, string htmlBody, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["type"] = "MessageCard",
            ["@type"] = "MessageCard",
            ["@context"] = "https://schema.org/extensions",
            ["themeColor"] = "7C3AED",
            ["summary"] = subject,
            ["sections"] = new JsonArray
            {
                new JsonObject
                {
                    ["activityTitle"] = subject,
                    ["activitySubtitle"] = $"À : {to}",
                    ["text"] = StripHtmlToPlainText(htmlBody)
                }
            }
        };
        using var client = httpFactory.CreateClient("notifications-teams");
        client.Timeout = TimeSpan.FromSeconds(15);
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        var reponse = await client.PostAsync(webhookUrl, content, ct);
        reponse.EnsureSuccessStatusCode();
    }

    private async Task EmettreSlackAsync(
        string webhookUrl, string to, string subject, string htmlBody, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["username"] = "N4 Sentinel",
            ["icon_emoji"] = ":rotating_light:",
            ["text"] = $"*{subject}*\nDestinataire : {to}",
            ["blocks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "section",
                    ["text"] = new JsonObject
                    {
                        ["type"] = "mrkdwn",
                        ["text"] = $"*{subject}*\n_{to}_\n\n{StripHtmlToPlainText(htmlBody)}"
                    }
                }
            }
        };
        using var client = httpFactory.CreateClient("notifications-slack");
        client.Timeout = TimeSpan.FromSeconds(15);
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        var reponse = await client.PostAsync(webhookUrl, content, ct);
        reponse.EnsureSuccessStatusCode();
    }

    /// <summary>Transforme le HTML en texte plat — Teams/Slack ne supportent pas HTML natif.</summary>
    private static string StripHtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var sb = new StringBuilder(html.Length);
        bool dansBalise = false;
        for (int i = 0; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '<') { dansBalise = true; continue; }
            if (c == '>') { dansBalise = false; continue; }
            sb.Append(c);
        }
        var txt = sb.ToString();
        txt = txt.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        return System.Net.WebUtility.HtmlDecode(txt);
    }
}

/// <summary>
/// Configuration des webhooks Teams / Slack.
/// SEC-001 : URLs vides par défaut → AUCUN appel HTTP émis tant que non configuré.
/// </summary>
public sealed class NotificationChannelsOptions
{
    public const string SectionName = "N4Sentinel:Notifications";
    public string? TeamsWebhookUrl { get; set; }
    public string? SlackWebhookUrl { get; set; }
}
