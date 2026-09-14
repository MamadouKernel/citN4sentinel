namespace N4Sentinel.Domain;

/// <summary>
/// MERISE — ENT_METBUS. Métrique Business de Succès (AMÉLIORATIONS).
/// Calculée par Job quotidien / hebdo / mensuel.
/// Objectifs V1 → V2 : MTTR /2, #P1 /mois -50%, %N1/N2 résolution 30%→70%,
/// Taux réussite Workflow version &lt; 95% = alerte.
/// </summary>
public class BusinessSuccessMetric : AuditableEntity
{
    /// <summary>Période : "Mois-2026-09", "Sem-37", "Jour-2026-09-12".</summary>
    public string ReportingPeriod { get; set; } = string.Empty;

    /// <summary>
    /// Environnement cible. Null = métrique globale multi-env (ex. moyenne entreprise).
    /// </summary>
    public Guid? EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    /// <summary>Code unique : MTTR_MIN, P1_COUNT, P1_N1_N2_PCT, WF_SUCCESS_PCT...</summary>
    public string MetricCode { get; set; } = string.Empty;

    public string MetricLabel { get; set; } = string.Empty;

    public decimal? NumericValueAvgOrSum { get; set; }

    public decimal? TargetFixedValue { get; set; }

    /// <summary>
    /// Valeur AVANT N4 Sentinel (base line mesurée sur 3 mois), pour comparaison.
    /// </summary>
    public decimal? BaselineBeforeSentinel { get; set; }

    /// <summary>% d'évolution vs période précédente (négatif = amélioration MTTR).</summary>
    public decimal? TrendVsPreviousPeriodPct { get; set; }

    public BusinessMetricAggregation AggregationKind { get; set; }

    public DateTimeOffset CalculatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Taille de l'échantillon (nb incidents, nb exécutions WF...).</summary>
    public long? SampleSizeCount { get; set; }

    /// <summary>
    /// Vrai = l'objectif est RATÉ. Calculé par persister pour tris / alertes.
    /// </summary>
    public bool IsTargetMissed { get; set; }

    /// <summary>
    /// Commentaire managérial : expliquer la dégradation / les raisons.
    /// </summary>
    public string? NarrativeComment { get; set; }
}
