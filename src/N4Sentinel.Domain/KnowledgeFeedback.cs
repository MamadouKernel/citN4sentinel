namespace N4Sentinel.Domain;

/// <summary>
/// Décision d'un administrateur du référentiel sur une correction proposée
/// (FR-087). Distincte du simple « résolu » : accepter engage réellement le
/// contenu du document, rejeter ne le fait jamais taire silencieusement.
/// </summary>
public enum FeedbackReviewStatus
{
    EnAttente = 0,
    Acceptee = 1,
    Rejetee = 2
}

/// <summary>
/// Signalement d'une réponse jugée incorrecte par un opérateur (FR-087).
/// Un opérateur ne peut pas corriger la documentation lui-même — il n'a pas
/// forcément l'autorité ni le contexte pour le faire — mais il peut signaler
/// qu'un passage cité ne répond pas correctement ET proposer une correction,
/// pour qu'un administrateur du référentiel documentaire la revoie et
/// l'accepte ou la rejette explicitement.
/// </summary>
public class KnowledgeFeedback : AuditableEntity
{
    public Guid DocumentId { get; set; }
    public Guid SectionId { get; set; }
    public DocumentSection? Section { get; set; }

    /// <summary>La question posée qui a produit ce passage comme réponse.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>Ce qui, selon l'opérateur, ne va pas. Facultatif.</summary>
    public string? Comment { get; set; }

    /// <summary>
    /// Texte de remplacement proposé pour la section citée (FR-087). Facultatif :
    /// un signalement peut se limiter à alerter, sans proposer de correction.
    /// </summary>
    public string? ProposedCorrection { get; set; }

    public string ReportedBy { get; set; } = string.Empty;

    public FeedbackReviewStatus ReviewStatus { get; set; } = FeedbackReviewStatus.EnAttente;
    public string? ReviewNote { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>Vrai dès que ReviewStatus quitte EnAttente — utilisé par l'écran de revue pour filtrer la file.</summary>
    public bool Resolved { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}
