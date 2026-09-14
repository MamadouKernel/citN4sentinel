namespace N4Sentinel.Domain;

/// <summary>
/// Point d'accès JMX à un composant de l'écosystème N4 (broker ActiveMQ,
/// nœud Center, XPS, ECN4…).
///
/// En V1 : l'implémentation réelle passe par un endpoint HTTP Jolokia exposé
/// par le broker/serveur. Les identifiants sont stockés dans
/// <see cref="TechnicalCredential"/> (chiffrés dans CredentialStore).
/// </summary>
public class JmxEndpoint : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    public JmxEndpointType EndpointType { get; set; } = JmxEndpointType.ActiveMQBroker;

    /// <summary>Nom DNS ou IP de l'hôte hébergeant l'agent JMX.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Port Jolokia HTTP (typiquement 8161 pour ActiveMQ) ou port RMI.</summary>
    public int Port { get; set; }

    /// <summary>
    /// Base URL Jolokia — typiquement http://hote:port/api/jolokia/.
    /// Si null ou vide : l'implémentation ne fait qu'une simulation contrôlée
    /// (appel impossible → log + warning) — jamais d'exécution fantôme.
    /// </summary>
    public string? JolokiaBaseUrl { get; set; }

    /// <summary>Référence vers <see cref="TechnicalCredential"/> pour authentification.</summary>
    public Guid? CredentialId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Dernière véracité OK de la connectivité JMX.</summary>
    public DateTimeOffset? LastReachableAt { get; set; }

    /// <summary>Message du dernier échec — aide à la résolution côté Admin.</summary>
    public string? LastFailureMessage { get; set; }

    /// <summary>Horodatage dernier passage périodique (HostedService PB-26).</summary>
    public DateTimeOffset? LastPooledAt { get; set; }

    /// <summary>Horodatage dernier succès Jolokia / ReadMetric.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Horodatage dernier échec collecte périodique.</summary>
    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>Message d'erreur dernière tentative (tronqué 1000 car, séparé de LastFailureMessage UI).</summary>
    public string? LastErrorMessage { get; set; }
}
