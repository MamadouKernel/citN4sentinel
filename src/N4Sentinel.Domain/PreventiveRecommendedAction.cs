namespace N4Sentinel.Domain;

/// <summary>
/// MERISE — ENT_APR. Action Préventive Recommandée (AMÉLIORATIONS).
///
/// Générée par le service APR à partir de signaux faibles (tendance 15min)
/// pour éviter les P1 Navis AVANT qu'ils ne surviennent (≠ Alertes réactives).
/// Score de priorité = RisqueP1Evité / Durée.
/// </summary>
public class PreventiveRecommendedAction : AuditableEntity
{
    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    /// <summary>Composant à l'origine du signal. Null = problème transverse.</summary>
    public Guid? SourceComponentId { get; set; }
    public N4Component? SourceComponent { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Famille cause P1 (correspond aux 10 TOP causes P1 Navis /
    /// RG-0307 Pré-checks TOP 10).
    /// </summary>
    public WorkflowPreCheckCategory RootCauseFamily { get; set; }

    /// <summary>% de probabilité qu'elle devienne un P1 si on n'agit pas (0-100).</summary>
    public decimal EstimatedP1RiskPct { get; set; }

    /// <summary>Durée estimée pour réaliser l'action, en minutes.</summary>
    public int EstimatedDurationMinutes { get; set; }

    /// <summary>Score de priorité (calculé et persisté pour tris). Plus haut = prioritaire.</summary>
    public decimal PriorityScore { get; set; }

    public AprStatus APRStatus { get; set; } = AprStatus.Detectee;

    /// <summary>Date de première détection du signal faible.</summary>
    public DateTimeOffset? FirstWeakSignalAt { get; set; }

    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EstimatedDeadlineForAction { get; set; }

    public DateTimeOffset? ScheduledAt { get; set; }

    public string? ScheduledBy { get; set; }

    /// <summary>
    /// Workflow d'exécution si l'APR est lancée via le moteur d'orchestration.
    /// </summary>
    public Guid? LaunchWorkflowId { get; set; }
    public Workflow? LaunchWorkflow { get; set; }

    public Guid? ResultingExecutionId { get; set; }
    public WorkflowExecution? ResultingExecution { get; set; }

    public string? ExecutedBy { get; set; }

    public DateTimeOffset? ExecutedAt { get; set; }

    /// <summary>
    /// Utilisateur ayant ignoré l'APR (rejet explicite, pas juste laisser expirer).
    /// </summary>
    public string? DismissedBy { get; set; }

    public DateTimeOffset? DismissedAt { get; set; }

    public string? DismissalReason { get; set; }

    /// <summary>
    /// Vrai = l'APR a été générée sur la base d'une tendance confirmée sur
    /// plusieurs mesures (15 min mini, RG-0902).
    /// </summary>
    public bool IsBasedOnTrend { get; set; } = false;

    /// <summary>Échantillon de signaux ayant conduit à l'APR (pour traçabilité).</summary>
    public string? EvidenceSummary { get; set; }

    /// <summary>
    /// Métriques / seuils exacts observés (JSON stringifié :
    /// ClockSkewSec = 0.8, IndexFragPct = 28, etc.).
    /// </summary>
    public string? RawSignalSnapshot { get; set; }

    public string? Notes { get; set; }
}
