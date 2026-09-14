namespace N4Sentinel.Domain;

/// <summary>
/// MERISE — ENT_INC. Fiche incident capitalisé (AMÉLIORATIONS — Mémoire
/// organisationnelle). Générée en brouillon automatiquement après la Clôture
/// d'une session Diagnostic 8 phases + exécution Workflow réussie, puis
/// enrichie / validée par N2 (RG-1001 / RG-1002).
/// </summary>
public class CapitalizedIncident : AuditableEntity
{
    public string ExternalReference { get; set; } = string.Empty;

    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    /// <summary>Gravité initialement déclarée (P1→P4, Alerte=P1 par défaut).</summary>
    public CriticalityLevel DeclaredSeverity { get; set; } = CriticalityLevel.Elevee;

    /// <summary>Symptôme / sujet initial du ticket.</summary>
    public string? InitialSymptom { get; set; }

    public DateTimeOffset RealIncidentStartedAt { get; set; }

    public DateTimeOffset? RealIncidentEndedAt { get; set; }

    /// <summary>
    /// MTTR = RealIncidentEndedAt - RealIncidentStartedAt, en minutes.
    /// Calculé et persisté pour agrégations METBUS.
    /// </summary>
    public long? TotalDurationMinutes { get; set; }

    public DateTimeOffset? DetectedBySentinelAt { get; set; }

    /// <summary>Code famille cause racine (10 TOP causes P1 Navis).</summary>
    public string? MainRootCauseCode { get; set; }

    public string? MainRootCauseLabel { get; set; }

    /// <summary>
    /// Méthode des 5 Pourquoi appliquée lors de la revue post-mortem (RG-1004).
    /// </summary>
    public string? FiveWhysRootCause { get; set; }

    public string? CorrectiveActionsTaken { get; set; }

    /// <summary>Session Diagnostic qui a permis de résoudre l'incident.</summary>
    public Guid? LinkedDiagnosticSessionId { get; set; }
    public DiagnosticSession? LinkedDiagnosticSession { get; set; }

    public Guid? LinkedExecutionId { get; set; }
    public WorkflowExecution? LinkedExecution { get; set; }

    /// <summary>SOPs mis en jeu (liste de codes / strings séparés par ;).</summary>
    public string? SOPsUsedListString { get; set; }

    /// <summary>
    /// SOP proposé / créé comme capitalisation de cette résolution
    /// (FR-089B, généré en Brouillon).
    /// </summary>
    public Guid? GeneratedSOPId { get; set; }
    public Sop? GeneratedSOP { get; set; }

    public string? QuantifiedBusinessImpact { get; set; }

    public string? CompensatoryMeasures { get; set; }

    /// <summary>Nombre d'opérateurs / experts impliqués au total.</summary>
    public int OperatorsInvolvedCount { get; set; }

    public ResolutionSkillLevel? ResolutionSkillLevel { get; set; }

    public string? ClosedBy { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Référence documentaire Post-Mortem (PDF / Sharepoint / Confluence).</summary>
    public string? PostMortemDocumentRef { get; set; }

    /// <summary>
    /// Leçons apprises + Actions Correctives préventives à mettre en place
    /// (alimente les futures APR).
    /// </summary>
    public string? LessonsAndCorrectives { get; set; }
}
