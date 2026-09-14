using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Ticketing;

/// <summary>
/// S3-09 + PB-25 · Flux BIDIRECTIONNEL ticketing (V1 minimum viable)
///
/// CONTRAINTES RESPECTÉES :
///   • 0 nouveau package NuGet (NFR-009). HttpClient natif uniquement.
///   • SEC-001 : double-garde flux sortant désactivé tant que
///     <c>TicketingExternalOptions.Enabled</c> est à <c>false</c> OU
///     <c>BaseUrl</c> est vide. AUCUN appel HTTP émis tant que la DSI n'a pas
///     fourni ses coordonnées.
///
/// 2 sous-flux :
///   (1) SORTANT · Pousser vers ServiceNow/Jira/GLPI lors de la création
///       puis synchronisation périodique des commentaires.
///   (2) ENTRANT · Endpoint API Webhook : lèche-frites (POST /api/tickets/webhook
///       mappage état / comments.
/// </summary>
public sealed class TicketingSyncService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    IAuditWriter auditWriter,
    IOptions<TicketingExternalOptions> options,
    ILogger<TicketingSyncService> logger)
    : Domain.Interfaces.ITicketingConnector
{
    public static readonly TimeSpan RetryDelays = default;
    private static readonly TimeSpan[] RetryBackoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    // -----------------------------------------------------------------------
    // (1) FLUX SORTANT · Pousser le ticket vers l'outil DSI (ServiceNow/Jira/GLPI)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Crée l'incident correspondant côté DSI — retourne l'ExternalId DSI.
    /// Double-garde : si options désactivées → retourne null (pas d'appel HTTP, audit.
    /// Échec HTTP ne casse jamais l'orchestration : exception avalée,
    /// exception enveloppée + audit.
    /// </summary>
    public async Task<string?> PousserIncidentExterneAsync(Ticket ticket, CancellationToken ct = default)
    {
        var opt = options.Value;
        if (!opt.Enabled || string.IsNullOrWhiteSpace(opt.BaseUrl)
                           || string.IsNullOrWhiteSpace(opt.System))
        {
            logger.LogInformation(
                "[PB-25 Ticketing] Flux sortant DÉSACTIVÉ (Enabled={On} BaseUrl={Url}). " +
                "Aucun appel HTTP émis — SEC-001.", opt.Enabled, opt.BaseUrl);
            return null;
        }

        try
        {
            string? externalId = null;
            for (int t = 1; t <= 3; t++)
            {
                try
                {
                    using var client = httpFactory.CreateClient("ticketing-outbound");
                    client.Timeout = TimeSpan.FromSeconds(20);
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue(
                            "Basic", BasicAuthHeader(opt.ApiUsername ?? string.Empty, opt.ApiPassword ?? string.Empty));

                    var payload = opt.System.ToLowerInvariant() switch
                    {
                        "servicenow" => BuildServiceNowPayload(ticket),
                        "jira" => BuildJiraPayload(ticket),
                        _ => BuildGenericPayload(ticket)
                    };
                    var endpoint = opt.System.ToLowerInvariant() switch
                    {
                        "servicenow" => "/api/now/table/incident",
                        "jira" => "/rest/api/3/issue",
                        _ => "/api/tickets"
                    };
                    using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                    var reponse = await client.PostAsync(opt.BaseUrl!.TrimEnd('/') + endpoint, content, ct);
                    reponse.EnsureSuccessStatusCode();
                    var json = await reponse.Content.ReadFromJsonAsync<JsonNode>(ct);
                    externalId = ExtractSystemId(json, opt.System);
                    if (!string.IsNullOrWhiteSpace(externalId)) break;
                }
                catch when (t < 3)
                {
                    await Task.Delay(RetryBackoff[t - 1], ct);
                }
            }

            if (string.IsNullOrWhiteSpace(externalId))
            {
                logger.LogError(
                    "[PB-25 Ticketing] Échec push {Ticket} après 3 tentatives (Systeme).",
                    ticket.Code);
                await auditWriter.WriteAsync(
                    action: AuditAction.TicketSynchroExterneEchec,
                    outcome: AuditOutcome.Echec,
                    actor: "systeme",
                    entityType: nameof(Ticket),
                    entityId: ticket.Id.ToString(),
                    entityLabel: $"{ticket.Code} — {ticket.Title}",
                    detail: $"PushTicketCode={ticket.Code}",
                    ct: ct);
                return null;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var dbTicket = await db.Tickets.FirstAsync(x => x.Id == ticket.Id, ct);
            dbTicket.ExternalTicketId = externalId;
            await db.SaveChangesAsync(ct);
            await auditWriter.WriteAsync(
                action: AuditAction.TicketSynchroExterne,
                outcome: AuditOutcome.Succes,
                actor: ticket.OpenedBy ?? "systeme",
                entityType: nameof(Ticket),
                entityId: ticket.Id.ToString(),
                entityLabel: $"{ticket.Code} — {ticket.Title}",
                environmentId: ticket.EnvironmentId,
                detail: $"Push DSI : {ticket.Code} → {externalId}",
                ct: ct);
            return externalId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[PB-25 Ticketing] Exception push ticket {Code}", ticket.Code);
            await auditWriter.WriteAsync(
                action: AuditAction.TicketSynchroExterneEchec,
                outcome: AuditOutcome.Echec,
                actor: "systeme",
                entityType: nameof(Ticket),
                entityId: ticket.Id.ToString(),
                entityLabel: $"{ticket.Code} — {ticket.Title}",
                detail: $"{ticket.Code}: {ex.Message}",
                ct: ct);
            return null;
        }
    }

    /// <summary>Ajoute un commentaire côté DSI (Appel HTTP).
    /// Double-garde identique.</summary>
    public async Task PousserCommentaireExterneAsync(Ticket ticket, TicketComment commentaire, CancellationToken ct = default)
    {
        var opt = options.Value;
        if (!opt.Enabled || string.IsNullOrWhiteSpace(opt.BaseUrl)
            || string.IsNullOrWhiteSpace(ticket.ExternalTicketId)
            || string.IsNullOrWhiteSpace(opt.System)) return;
        if (commentaire.Source == TicketCommentSource.Externe) return;

        for (int t = 1; t <= 3; t++)
        {
            try
            {
                using var client = httpFactory.CreateClient("ticketing-outbound");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", BasicAuthHeader(opt.ApiUsername ?? "", opt.ApiPassword ?? ""));

                (string endpoint, JsonObject payload) =
                    (opt.System?.ToLowerInvariant()) switch
                    {
                        "servicenow" => ($"/api/now/table/sys_journal_field", new JsonObject
                        {
                            ["incident"] = ticket.ExternalTicketId,
                            ["comments"] = commentaire.Body,
                            ["element"] = "comments"
                        }),
                        "jira" => ($"/rest/api/3/issue/{ticket.ExternalTicketId}/comment", new JsonObject
                        {
                            ["body"] = commentaire.Body
                        }),
                        _ => ($"/api/tickets/{ticket.ExternalTicketId}/comments", new JsonObject
                        {
                            ["body"] = commentaire.Body,
                            ["author"] = commentaire.WrittenBy
                        })
                    };
                var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                var reponse = await client.PostAsync(opt.BaseUrl.TrimEnd('/') + endpoint, content, ct);
                reponse.EnsureSuccessStatusCode();
                return;
            }
            catch when (t < 3)
            {
                await Task.Delay(RetryBackoff[t - 1], ct);
            }
        }
    }

    // -----------------------------------------------------------------------
    // (2) FLUX ENTRANT · Webhook réception mise à jour DSI (état + commentaires)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Point d'entrée API Webhook générique. Appelé depuis Program.cs.MapPost("/api/tickets/webhook").
    ///
    /// SÉCURITÉ FAIL-CLOSED (SEC-001 DOUBLE-GARDE · CORRECTIF CRITIQUE 2026-09-13).
    /// Matrice de décision :
    ///   Enabled=false                         → REJET
    ///   WebhookSecret configuré  +  signature ABSENTE   → REJET
    ///   WebhookSecret configuré  +  signature INVALIDE   → REJET
    ///   WebhookSecret absent    ET  AllowUnsignedWebhooksForDebug=false (défaut)  → REJET
    ///   WebhookSecret absent    ET  AllowUnsignedWebhooksForDebug=true (DEV SEUL) → ⚠️ accepté avec warning
    ///   WebhookSecret configuré  +  signature VALIDE    → ✅ accepté
    /// </summary>
    public async Task<(bool Accepte, string? RaisonRefus, Ticket? TicketMisAJour)> TraiterWebhookEntrantAsync(
        string systeme, JsonNode jsonBrut, string? signatureHmacSha256, CancellationToken ct)
    {
        var opt = options.Value;
        if (!opt.Enabled) return (false, "Flux entrant DÉSACTIVÉ (Enabled=false).", null);

        bool secretConfigured = !string.IsNullOrWhiteSpace(opt.WebhookSecret);
        bool signaturePresent = !string.IsNullOrWhiteSpace(signatureHmacSha256);

        // ── CAS (a) : Secret configuré mais signature ABSENTE → REJET SYSTÉMATIQUE ──
        if (secretConfigured && !signaturePresent)
        {
            logger.LogCritical(
                "[SEC-001 Ticketing] Rejet webhook — WebhookSecret configuré MAIS signature HMAC ABSENTE (Systeme={Sys}). " +
                "Header X-Hmac-Sha256 ou x-ticketing-signature manquant.", systeme);
            await auditWriter.WriteAsync(
                action: AuditAction.TicketSynchroExterneEchec,
                outcome: AuditOutcome.Echec,
                actor: "systeme",
                entityType: nameof(Ticket),
                detail: $"Webhook {systeme}: HMAC REQUIS mais absent (secret configuré)",
                ct: ct).ConfigureAwait(false);
            return (false, "Signature HMAC requise (WebhookSecret configuré).", null);
        }

        // ── CAS (b) : Secret NON configuré ET flux activé ──
        if (!secretConfigured)
        {
            if (opt.AllowUnsignedWebhooksForDebug)
            {
                logger.LogWarning(
                    "[SEC-001 Ticketing] MODE DÉBUG UNIQUEMENT — Webhook accepté SANS signature car " +
                    "AllowUnsignedWebhooksForDebug=true (Systeme={Sys}). À DÉSACTIVER IMPÉRATIVEMENT hors dev local.", systeme);
                await auditWriter.WriteAsync(
                    action: AuditAction.TicketSynchroExterneEchec,
                    outcome: AuditOutcome.Succes,
                    actor: "systeme",
                    entityType: nameof(Ticket),
                    detail: $"[MODE DEBUG-SEUL] Webhook {systeme}: accepté non signé (AllowUnsignedWebhooksForDebug=true — hors dev c'est une faille)",
                    ct: ct).ConfigureAwait(false);
            }
            else
            {
                logger.LogCritical(
                    "[SEC-001 Ticketing] Rejet webhook — WebhookSecret NON configuré ET AllowUnsignedWebhooksForDebug=false " +
                    "(Systeme={Sys}). Mode STRICT par défaut.", systeme);
                await auditWriter.WriteAsync(
                    action: AuditAction.TicketSynchroExterneEchec,
                    outcome: AuditOutcome.Echec,
                    actor: "systeme",
                    entityType: nameof(Ticket),
                    detail: $"Webhook {systeme}: rejeté (WebhookSecret absent, AllowUnsignedWebhooksForDebug=false)",
                    ct: ct).ConfigureAwait(false);
                return (false, "Webhook désactivé : WebhookSecret non configuré (mode strict).", null);
            }
        }

        // ── CAS (c) : Les deux présents → vérification cryptographique TIME-SAFE ──
        if (secretConfigured && signaturePresent)
        {
            var payload = jsonBrut.ToJsonString();
            var attenduHex = HmacSha256Hex(opt.WebhookSecret!, payload);
            byte[] attenduBytes = Convert.FromHexString(attenduHex);
            byte[] recuBytes;
            try { recuBytes = Convert.FromHexString(signatureHmacSha256!); }
            catch (FormatException)
            {
                logger.LogWarning(
                    "[SEC-001 Ticketing] Rejet webhook — format de signature HMAC invalide (non-hexadécimal) (Systeme={Sys}).",
                    systeme);
                await auditWriter.WriteAsync(
                    action: AuditAction.TicketSynchroExterneEchec,
                    outcome: AuditOutcome.Echec,
                    actor: "systeme",
                    entityType: nameof(Ticket),
                    detail: $"Webhook {systeme}: HMAC format invalide (non hex)",
                    ct: ct).ConfigureAwait(false);
                return (false, "Format de signature HMAC invalide.", null);
            }

            if (recuBytes.Length != attenduBytes.Length
                || !CryptographicOperations.FixedTimeEquals(attenduBytes, recuBytes))
            {
                logger.LogWarning(
                    "[SEC-001 Ticketing] Rejet webhook — signature HMAC invalide (Systeme={Sys}).", systeme);
                await auditWriter.WriteAsync(
                    action: AuditAction.TicketSynchroExterneEchec,
                    outcome: AuditOutcome.Echec,
                    actor: "systeme",
                    entityType: nameof(Ticket),
                    detail: $"Webhook {systeme}: HMAC invalide",
                    ct: ct).ConfigureAwait(false);
                return (false, "Signature HMAC invalide.", null);
            }
            logger.LogInformation("[SEC-001 Ticketing] Webhook authentifié par HMAC (Systeme={Sys}).", systeme);
        }

        // ── Parsing payload ──
        var (codeTicket, nouvelEtat, nouveauCommentaire) = systeme.ToLowerInvariant() switch
        {
            "servicenow" => ParseServiceNowWebhook(jsonBrut),
            "jira" => ParseJiraWebhook(jsonBrut),
            _ => ParseGenericWebhook(jsonBrut)
        };

        if (string.IsNullOrWhiteSpace(codeTicket))
        {
            return (false, "Impossible d'extraire l'identifiant ticket/code ticket code ticket DSI.", null);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        Ticket? ticket = await db.Tickets
            .Include(t => t.Comments)
            .FirstOrDefaultAsync(t => t.Code == codeTicket || t.ExternalTicketId == codeTicket, ct);
        if (ticket is null)
        {
            logger.LogWarning("[PB-25 Ticketing] Webhook reçu mais ticket {Code} introuvable.", codeTicket);
            return (false, $"Ticket {codeTicket} introuvable.", null);
        }

        if (nouvelEtat.HasValue && ticket.Status != nouvelEtat.Value)
        {
            ticket.Status = nouvelEtat.Value;
            await auditWriter.WriteAsync(
                action: AuditAction.TicketModifie,
                outcome: AuditOutcome.Succes,
                actor: $"Webhook-{systeme}",
                entityType: nameof(Ticket),
                entityId: ticket.Id.ToString(),
                entityLabel: $"{ticket.Code} — {ticket.Title}",
                detail: $"Statut DSI: {codeTicket} → {nouvelEtat}",
                ct: ct);
        }
        if (!string.IsNullOrWhiteSpace(nouveauCommentaire.texte))
        {
            db.TicketComments.Add(new TicketComment
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                WrittenBy = string.IsNullOrWhiteSpace(nouveauCommentaire.auteur) ? $"webhook-{systeme}" : nouveauCommentaire.auteur,
                WrittenAt = DateTimeOffset.UtcNow,
                Body = nouveauCommentaire.texte,
                Source = TicketCommentSource.Externe
            });
        }
        await db.SaveChangesAsync(ct);
        return (true, null, ticket);
    }

    // -----------------------------------------------------------------------
    // Builders internes (payloads parsers
    // -----------------------------------------------------------------------
    private static JsonObject BuildServiceNowPayload(Ticket t) => new()
    {
        ["short_description"] = t.Title,
        ["description"] = t.Description,
        ["priority"] = t.Priority switch
        {
            TicketPriority.P1Critique => "1",
            TicketPriority.P2Majeur => "2",
            TicketPriority.P3Normal => "3",
            _ => "4"
        },
        ["u_correlation_display"] = t.Code,
        ["caller_id"] = t.OpenedBy ?? "N4-Sentinel"
    };

    private static JsonObject BuildJiraPayload(Ticket t) => new()
    {
        ["fields"] = new JsonObject
        {
            ["summary"] = t.Title,
            ["description"] = t.Description,
            ["issuetype"] = new JsonObject { ["name"] = "Bug" },
            ["labels"] = new JsonArray("N4Sentinel", t.Priority.ToString())
        }
    };

    private static JsonObject BuildGenericPayload(Ticket t) => new()
    {
        ["title"] = t.Title,
        ["description"] = t.Description,
        ["priority"] = t.Priority.ToString(),
        ["severity"] = t.Severity.ToString(),
        ["correlationId"] = t.Code,
        ["environmentId"] = t.EnvironmentId?.ToString(),
        ["openedBy"] = t.OpenedBy
    };

    private static string? ExtractSystemId(JsonNode? json, string? systeme)
    {
        if (json is null) return null;
        return (systeme?.ToLowerInvariant()) switch
        {
            "servicenow" => json["result"]?["sys_id"]?.GetValue<string>(),
            "jira" => json["key"]?.GetValue<string>() ?? json["id"]?.GetValue<string>(),
            _ => json["id"]?.GetValue<string>() ?? json["ticketId"]?.GetValue<string>()
        };
    }

    private static (string? codeTicket, TicketStatus? nouvelEtat, (string texte, string? auteur) commentaire) ParseGenericWebhook(JsonNode node)
    {
        var code = node["correlationId"]?.GetValue<string>()
                   ?? node["ticketCode"]?.GetValue<string>()
                   ?? node["externalId"]?.GetValue<string>();
        TicketStatus? etat = null;
        if (Enum.TryParse<TicketStatus>(node["status"]?.GetValue<string>() ?? string.Empty, true, out var s)) etat = s;
        var txt = node["comment"]?["body"]?.GetValue<string>() ?? node["commentBody"]?.GetValue<string>();
        var auteur = node["comment"]?["author"]?.GetValue<string>();
        return (code, etat, (txt ?? string.Empty, auteur));
    }

    private static (string? codeTicket, TicketStatus? nouvelEtat, (string texte, string? auteur) commentaire) ParseJiraWebhook(JsonNode node)
    {
        var code = node["issue"]?["key"]?.GetValue<string>()
                   ?? node["issue"]?["fields"]?["customfield_10000"]?.GetValue<string>();
        TicketStatus? etat = null;
        var jstatut = node["issue"]?["fields"]?["status"]?["name"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(jstatut)) etat = jstatut.ToLowerInvariant() switch
        {
            "done" or "closed" or "fermé" => TicketStatus.Ferme,
            "in progress" or "en cours" => TicketStatus.EnCours,
            "resolved" or "résolu" => TicketStatus.Resolu,
            "to do" or "à faire" => TicketStatus.Nouveau,
            _ => null
        };
        var comment = node["comment"]?["body"]?.GetValue<string>();
        var auteur = node["comment"]?["author"]?["displayName"]?.GetValue<string>();
        return (code, etat, (comment ?? string.Empty, auteur));
    }

    private static (string? codeTicket, TicketStatus? nouvelEtat, (string texte, string? auteur) commentaire) ParseServiceNowWebhook(JsonNode node)
    {
        var code = node["u_correlation_display"]?.GetValue<string>()
                   ?? node["correlation_id"]?.GetValue<string>()
                   ?? node["number"]?.GetValue<string>();
        var state = node["state"]?.GetValue<string>();
        TicketStatus? etat = null;
        if (!string.IsNullOrWhiteSpace(state)) etat = state switch
        {
            "1" => TicketStatus.Nouveau,
            "2" => TicketStatus.EnCours,
            "3" or "6" => TicketStatus.Resolu,
            "7" => TicketStatus.Ferme,
            "8" => TicketStatus.Annule,
            _ => null
        };
        var comments = node["comments"]?.GetValue<string>();
        var auteur = node["sys_updated_by"]?.GetValue<string>();
        return (code, etat, (comments ?? string.Empty, auteur));
    }

    private static string BasicAuthHeader(string username, string password)
    {
        var bytes = Encoding.UTF8.GetBytes($"{username}:{password}");
        return Convert.ToBase64String(bytes);
    }

    private static string HmacSha256Hex(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    // -----------------------------------------------------------------------
    // Implémentation ITicketingConnector (S1-09 · Contrat interfaces externes)
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<int> PousserCommentairesExternesAsync(Guid ticketId, CancellationToken ct = default)
    {
        if (ticketId == Guid.Empty) return 0;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ticket = await db.Tickets
            .Include(t => t.Comments)
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct).ConfigureAwait(false);
        if (ticket is null) return 0;

        int pousses = 0;
        foreach (var c in ticket.Comments.OrderBy(c => c.WrittenAt))
        {
            if (c.Source == TicketCommentSource.Externe) continue;
            await PousserCommentaireExterneAsync(ticket, c, ct).ConfigureAwait(false);
            pousses++;
        }
        return pousses;
    }

    /// <inheritdoc />
    public async Task<TicketStatus?> RecupererStatutExterneAsync(Guid ticketId, CancellationToken ct = default)
    {
        var opt = options.Value;
        if (!opt.Enabled || string.IsNullOrWhiteSpace(opt.BaseUrl) || ticketId == Guid.Empty) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ticket = await db.Tickets.FindAsync([ticketId], ct).ConfigureAwait(false);
        if (ticket is null || string.IsNullOrWhiteSpace(ticket.ExternalTicketId)) return null;

        for (int t = 1; t <= 3; t++)
        {
            try
            {
                using var client = httpFactory.CreateClient("ticketing-outbound");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", BasicAuthHeader(opt.ApiUsername ?? "", opt.ApiPassword ?? ""));
                var sys = (opt.System ?? "").ToLowerInvariant();
                var endpoint = sys switch
                {
                    "servicenow" => $"/api/now/table/incident/{ticket.ExternalTicketId}?sysparm_fields=state",
                    "jira" => $"/rest/api/3/issue/{ticket.ExternalTicketId}?fields=status",
                    _ => $"/api/tickets/{ticket.ExternalTicketId}"
                };
                var reponse = await client.GetAsync(opt.BaseUrl.TrimEnd('/') + endpoint, ct).ConfigureAwait(false);
                if (!reponse.IsSuccessStatusCode) return null;
                var json = await reponse.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct).ConfigureAwait(false);
                if (json is null) return null;

                TicketStatus? etat = sys switch
                {
                    "servicenow" => (json["result"]?["state"]?.GetValue<string>()) switch
                    {
                        "1" => TicketStatus.Nouveau, "2" => TicketStatus.EnCours,
                        "3" or "6" => TicketStatus.Resolu, "7" => TicketStatus.Ferme,
                        "8" => TicketStatus.Annule, _ => null
                    },
                    "jira" => (json["fields"]?["status"]?["name"]?.GetValue<string>()) switch
                    {
                        var s when s != null && s.Contains("esolved", StringComparison.OrdinalIgnoreCase) => TicketStatus.Resolu,
                        var s when s != null && s.Contains("losed", StringComparison.OrdinalIgnoreCase) => TicketStatus.Ferme,
                        var s when s != null && s.Contains("n Progress", StringComparison.OrdinalIgnoreCase) => TicketStatus.EnCours,
                        _ => TicketStatus.Nouveau
                    },
                    _ => (json["status"]?.GetValue<string>()) switch
                    {
                        var s when s != null && s.Contains("resolu", StringComparison.OrdinalIgnoreCase) => TicketStatus.Resolu,
                        var s when s != null && s.Contains("erme", StringComparison.OrdinalIgnoreCase) => TicketStatus.Ferme,
                        var s when s != null && s.Contains("cours", StringComparison.OrdinalIgnoreCase) => TicketStatus.EnCours,
                        _ => TicketStatus.Nouveau
                    }
                };
                return etat;
            }
            catch when (t < 3) { await Task.Delay(RetryBackoff[t - 1], ct).ConfigureAwait(false); }
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<(bool Accepte, string? StatutSentinel, string? ExternalId)>
        RecevoirWebhookExterneAsync(string systemeExterne, JsonNode payload, string? correlationId, CancellationToken ct = default)
    {
        var (accepte, _, misAJour) = await TraiterWebhookEntrantAsync(
            systemeExterne, payload, signatureHmacSha256: null, ct).ConfigureAwait(false);
        return (accepte, misAJour?.Status.ToString(), misAJour?.ExternalTicketId);
    }
}

/// <summary>
/// Configuration du flux externe (DSI).
/// SEC-001 · DOUBLE-GARDE PAR DÉFAUT :
///   • Enabled=false (flux désactivés par défaut)
///   • AllowUnsignedWebhooksForDebug=false : webhook SANS secret + SANS signature
///     sont REJETÉS en mode strict. Passer à true UNIQUEMENT en dev local.
/// </summary>
public sealed class TicketingExternalOptions
{
    public const string SectionName = "N4Sentinel:TicketingExterne";
    public bool Enabled { get; set; }
    public string? System { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiUsername { get; set; }
    public string? ApiPassword { get; set; }
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// DANGEREUX · AUTORISE LES WEBHOOKS SANS SIGNATURE HMAC.
    /// À true UNIQUEMENT en dev local. JAMAIS en UAT ni Production.
    /// Vaut false par défaut (SEC-001).
    /// </summary>
    public bool AllowUnsignedWebhooksForDebug { get; set; }
}
