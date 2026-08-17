namespace N4Sentinel.Domain;

/// <summary>
/// Contrôle / Signal (§3.18) : relevé persisté d'un instantané de supervision.
/// Avant cette entité, l'état de santé (<c>ComponentHealthSnapshot</c>) ne
/// vivait qu'en cache mémoire (<c>SupervisionStateCache</c>) — perdu à
/// chaque redémarrage de l'application, alors que le modèle de données
/// minimal exige explicitement sa persistance.
///
/// Un relevé par composant et par passage de supervision (~30 s) : soumis à
/// <see cref="RetentionPolicy.SignalsRetentionDays"/>, pas conservé
/// indéfiniment.
/// </summary>
public class ComponentSignal : AuditableEntity
{
    public Guid ComponentId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string ComponentName { get; set; } = string.Empty;

    /// <summary>Nature du relevé : "ÉtatConsolidé", "Service Windows", "Écart horloge", "Temps de réponse"...</summary>
    public string SignalType { get; set; } = string.Empty;

    /// <summary>Ce qui est observé : nom de composant, hôte, service.</summary>
    public string Target { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>Seuil applicable, quand la nature du relevé en comporte un. Null sinon.</summary>
    public string? Threshold { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>Fiabilité du relevé : "Confirmé", "Incomplet" (signaux partiels), "Inconnu" (rien de mesurable).</summary>
    public string Quality { get; set; } = string.Empty;

    /// <summary>
    /// §3.18 : identifiant de l'incident ou de l'opération concernée, quand ce
    /// relevé a été capturé dans ce contexte (ex. pendant une exécution en
    /// cours). Null pour un relevé de supervision ambiant, hors de tout
    /// incident ou opération — ce qui est le cas normal.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// §3.18 : fraîcheur du relevé, calculée à la lecture — jamais stockée,
    /// pour ne jamais afficher une fraîcheur devenue fausse avec le temps.
    /// </summary>
    public TimeSpan Freshness => DateTimeOffset.UtcNow - CapturedAt;
}
