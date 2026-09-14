using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Domain.Interfaces;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Infrastructure.Ticketing;

/// <summary>
/// Connecteur JMX en mode Jolokia (HTTP JSON) pour les endpoints
/// ActiveMQ Broker et serveurs applicatifs N4 (S3-09).
///
/// Principe de sécurité : l'appel HTTP réel n'est émis que si :
///   (1) L'endpoint a JolokiaBaseUrl renseigné ET
///   (2) L'endpoint est Enabled ET
///   (3) Les identifiants de CredentialId existent dans le CredentialStore.
/// Dans TOUS les autres cas, le connecteur retourne une réponse simulée
/// (métadonnées / warning) et trace un audit JmxEchec : jamais de silence
/// d'erreur.
///
/// Pour une implémentation JMX RMI (Java) : il faudrait installer un bridge
/// Jolokia sur chaque serveur ou une bibliothèque lourde côté .NET, ce qui
/// est hors scope de la V1.
/// </summary>
public sealed class JolokiaJmxConnector(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    CredentialStore credentials,
    IHttpClientFactory httpFactory,
    IAuditWriter auditWriter,
    ILogger<JolokiaJmxConnector> logger) : IJmxConnector
{
    // 12s est suffisant pour une requête Jolokia (timeout firewall lent).
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    // -----------------------------------------------------------------------
    // Connectivité
    // -----------------------------------------------------------------------
    public async Task<bool> TestConnectivityAsync(Guid jmxEndpointId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var endpoint = await db.JmxEndpoints.FirstOrDefaultAsync(e => e.Id == jmxEndpointId, ct);
        if (endpoint is null) return false;
        if (!endpoint.Enabled)
        {
            endpoint.LastFailureMessage = "Endpoint désactivé (Enabled=false).";
            await db.SaveChangesAsync(ct);
            return false;
        }
        if (string.IsNullOrWhiteSpace(endpoint.JolokiaBaseUrl))
        {
            endpoint.LastFailureMessage = "JolokiaBaseUrl non renseigné : appel HTTP impossible en V1.";
            await db.SaveChangesAsync(ct);
            await auditWriter.WriteAsync(
                AuditAction.JmxEchec, AuditOutcome.Refuse, actor: "systeme",
                entityType: nameof(JmxEndpoint), entityId: endpoint.Id.ToString(),
                entityLabel: endpoint.Name,
                reason: endpoint.LastFailureMessage, ct: ct);
            return false;
        }

        var basic = await LireCredentielsBasicAuth(endpoint, ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            using var http = httpFactory.CreateClient();
            if (basic is not null)
            {
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", basic);
            }

            // Jolokia fournit un endpoint "version" : c'est un ping idéal.
            var req = new
            {
                type = "version",
            };
            var response = await http.PostAsJsonAsync(
                endpoint.JolokiaBaseUrl.TrimEnd('/') + "/", req, cts.Token);
            var ok = response.IsSuccessStatusCode;
            if (ok)
            {
                endpoint.LastReachableAt = DateTimeOffset.UtcNow;
                endpoint.LastFailureMessage = null;
                await auditWriter.WriteAsync(
                    AuditAction.JmxSessionOuverte, AuditOutcome.Succes,
                    actor: "systeme",
                    entityType: nameof(JmxEndpoint), entityId: endpoint.Id.ToString(),
                    entityLabel: endpoint.Name,
                    reason: $"Test connectivité Jolokia OK — {endpoint.Host}:{endpoint.Port}.",
                    ct: ct);
            }
            else
            {
                var reason = $"HTTP {(int)response.StatusCode} : {response.ReasonPhrase}.";
                endpoint.LastFailureMessage = reason;
                await auditWriter.WriteAsync(
                    AuditAction.JmxEchec, AuditOutcome.Echec, actor: "systeme",
                    entityType: nameof(JmxEndpoint), entityId: endpoint.Id.ToString(),
                    entityLabel: endpoint.Name,
                    reason: "Test connectivité échoué : " + reason, ct: ct);
            }

            await db.SaveChangesAsync(ct);
            return ok;
        }
        catch (Exception ex)
        {
            var raison = ex.InnerException?.Message ?? ex.Message;
            endpoint.LastFailureMessage = raison;
            await db.SaveChangesAsync(ct);
            logger.LogWarning(ex, "[JMX] Échec test connectivité endpoint {Name} ({Host}:{Port}).",
                endpoint.Name, endpoint.Host, endpoint.Port);
            await auditWriter.WriteAsync(
                AuditAction.JmxEchec, AuditOutcome.Echec, actor: "systeme",
                entityType: nameof(JmxEndpoint), entityId: endpoint.Id.ToString(),
                entityLabel: endpoint.Name,
                reason: "Exception pendant test connectivité : " + raison, ct: ct);
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Opérations Broker ActiveMQ (purge queue / count)
    // -----------------------------------------------------------------------
    public async Task<string> InvokeBrokerOperationAsync(
        Guid jmxEndpointId,
        string operationName,
        string? queueName = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var endpoint = await db.JmxEndpoints.FirstOrDefaultAsync(e => e.Id == jmxEndpointId, ct);
        if (endpoint is null) return "Endpoint JMX introuvable.";
        if (!endpoint.Enabled) return "Endpoint JMX désactivé.";
        if (endpoint.EndpointType != JmxEndpointType.ActiveMQBroker)
            return "Cet endpoint n'est pas un Broker ActiveMQ.";

        if (string.IsNullOrWhiteSpace(endpoint.JolokiaBaseUrl))
        {
            var msgSimule = operationName.ToLowerInvariant() switch
            {
                "purge" or "removemessage" or "clearqueue" =>
                    $"[SIMULÉ/JolokiaBaseUrlAbsent] Purge de la queue « {queueName} » — appel HTTP ignoré car endpoint non configuré avec une base URL Jolokia. "
                    + "Pour exécuter réellement, renseignez JolokiaBaseUrl + CredentialId et activez l'endpoint.",
                "resetstatistics" or "browse" =>
                    $"[SIMULÉ/JolokiaBaseUrlAbsent] Opération {operationName} sur broker {endpoint.Name}. "
                    + "Configurez JolokiaBaseUrl + CredentialId pour exécuter le MBean.",
                _ => $"[SIMULÉ/JolokiaBaseUrlAbsent] Opération {operationName} — non émise."
            };
            logger.LogWarning("[JMX {Op}] Opération simulée : {Msg}", operationName, msgSimule);
            await auditWriter.WriteAsync(
                AuditAction.JmxMBeanAppel, AuditOutcome.Refuse, actor: "systeme",
                entityType: nameof(JmxEndpoint), entityId: endpoint.Id.ToString(),
                entityLabel: endpoint.Name,
                reason: $"Appel MBean {operationName} (simulé — JolokiaBaseUrl absent).",
                detail: msgSimule, ct: ct);
            return msgSimule;
        }

        var basic = await LireCredentielsBasicAuth(endpoint, ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            using var http = httpFactory.CreateClient();
            if (basic is not null)
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);

            JsonObject request;
            switch (operationName.ToLowerInvariant())
            {
                case "size":
                case "depth":
                case "count":
                    request = new JsonObject
                    {
                        ["type"] = "read",
                        ["mbean"] = $"org.apache.activemq:type=Broker,brokerName=*,destinationType=Queue,destinationName={queueName}",
                        ["attribute"] = "QueueSize"
                    };
                    break;
                case "purge":
                    request = new JsonObject
                    {
                        ["type"] = "exec",
                        ["mbean"] = $"org.apache.activemq:type=Broker,brokerName=*,destinationType=Queue,destinationName={queueName}",
                        ["operation"] = "purge",
                        ["arguments"] = new JsonArray()
                    };
                    break;
                case "reset":
                    request = new JsonObject
                    {
                        ["type"] = "exec",
                        ["mbean"] = "org.apache.activemq:type=Broker,brokerName=*",
                        ["operation"] = "resetStatistics",
                        ["arguments"] = new JsonArray()
                    };
                    break;
                default:
                    return $"Opération '{operationName}' inconnue. Supporté V1 : size / purge / reset.";
            }

            var url = endpoint.JolokiaBaseUrl.TrimEnd('/') + "/";
            var content = new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            var response = await http.PostAsync(url, content, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var statut = (int)response.StatusCode;

            await auditWriter.WriteAsync(
                AuditAction.JmxMBeanAppel,
                response.IsSuccessStatusCode ? AuditOutcome.Succes : AuditOutcome.Echec,
                actor: "systeme",
                entityType: nameof(JmxEndpoint), entityId: endpoint.Id.ToString(),
                entityLabel: endpoint.Name,
                reason: $"Appel MBean {operationName} sur queue '{queueName}' — HTTP {statut}.",
                detail: body.Length < 1500 ? body : body.Substring(0, 1500),
                ct: ct);

            if (response.IsSuccessStatusCode)
            {
                endpoint.LastReachableAt = DateTimeOffset.UtcNow;
                endpoint.LastFailureMessage = null;
                await db.SaveChangesAsync(ct);
            }

            return response.IsSuccessStatusCode
                ? $"HTTP 200 OK — {operationName} — réponse Jolokia : {Truncate(body, 600)}"
                : $"Échec HTTP {statut} : {response.ReasonPhrase} — {Truncate(body, 300)}";
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            endpoint.LastFailureMessage = msg;
            await db.SaveChangesAsync(ct);
            logger.LogWarning(ex, "[JMX] Échec appel MBean {Op} sur endpoint {Name}.", operationName, endpoint.Name);
            await auditWriter.WriteAsync(
                AuditAction.JmxEchec, AuditOutcome.Echec, actor: "systeme",
                entityType: nameof(JmxEndpoint), entityId: endpoint.Id.ToString(),
                entityLabel: endpoint.Name,
                reason: $"Exception MBean {operationName} : {msg}", ct: ct);
            return $"Exception technique appel MBean : {msg}";
        }
    }

    public async Task<long> ReadMetricAsync(
        Guid jmxEndpointId,
        string metricName,
        string? scope = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        var reponse = await InvokeBrokerOperationAsync(
            jmxEndpointId, operationName: "size", queueName: scope, ct: ct);
        if (reponse.StartsWith("[SIMULÉ", StringComparison.Ordinal))
        {
            logger.LogWarning("[JMX ReadMetric {Metric}] Réponse simulée : -1.", metricName);
            return -1;
        }
        if (reponse.StartsWith("HTTP 200 OK", StringComparison.Ordinal)
            && TryExtraireValeurJolokia(reponse, metricName, out var valeur))
        {
            return valeur;
        }
        return -1;
    }

    // -----------------------------------------------------------------------
    // Utilitaires internes
    // -----------------------------------------------------------------------
    private async Task<string?> LireCredentielsBasicAuth(JmxEndpoint endpoint, CancellationToken ct)
    {
        var login = await credentials.GetClearTextLoginAsync(endpoint.CredentialId, ct);
        if (login is null) return null;
        var (user, pass) = login.Value;
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass)) return null;
        var octets = System.Text.Encoding.ASCII.GetBytes($"{user}:{pass}");
        return Convert.ToBase64String(octets);
    }

    private static bool TryExtraireValeurJolokia(string reponse, string metricName, out long valeur)
    {
        valeur = default;
        try
        {
            var idx = reponse.IndexOf("réponse Jolokia :", StringComparison.Ordinal);
            if (idx < 0) return false;
            var json = reponse.Substring(idx + "réponse Jolokia :".Length).Trim();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var v))
            {
                if (v.TryGetInt64(out var l)) { valeur = l; return true; }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
}
