namespace N4Sentinel.Domain.Interfaces;

/// <summary>
/// Vue métier minimaliste d'un endpoint JMX : hôte / port / type / identifiants.
/// Le connecteur réel en V1 exploite Jolokia (HTTP JSON sur MBean) quand
/// l'endpoint a un <see cref="JmxEndpoint.JolokiaBaseUrl"/> ; sinon l'appel
/// est SIMULÉ (logs explicites + retour métadonnées) et un warning SEC est
/// tracé. Pour une implémentation JMX native (Java RMI), il faudrait une
/// dépendance lourde que nous n'introduisons pas en V1.
/// </summary>
public interface IJmxConnector
{
    /// <summary>Retourne true si l'endpoint est joignable via son réseau.</summary>
    Task<bool> TestConnectivityAsync(Guid jmxEndpointId, CancellationToken ct = default);

    /// <summary>
    /// Appelle un opératif standard sur le broker ActiveMQ : purge d'une queue
    /// Bridge, lecture de Size, reset des statistiques. Retourne un texte
    /// exploitable affichable dans UI Admin / journal d'audit.
    /// </summary>
    Task<string> InvokeBrokerOperationAsync(
        Guid jmxEndpointId,
        string operationName,
        string? queueName = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lit une métrique standard (depth de queue Bridge, consommateur,
    /// mémoire ActiveMQ, uptime…). Retourne -1 si la métrique n'est pas
    /// lisible (connexion impossible, attribut absent).
    /// </summary>
    Task<long> ReadMetricAsync(Guid jmxEndpointId, string metricName, string? scope = null, CancellationToken ct = default);
}
