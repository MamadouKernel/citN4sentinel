namespace N4Sentinel.Domain;

/// <summary>
/// Entree du journal d'audit (SEC-008, FR-091, FR-092).
///
/// Trace les connexions, les changements de configuration et de droits, les
/// actions sensibles ET les echecs d'autorisation - une tentative refusee est
/// une information de securite, pas un non-evenement.
///
/// Ces entrees ne sont jamais modifiables ni supprimables par un operateur :
/// la contrainte est posee cote base (declencheur) et cote application
/// (aucune methode de mise a jour n'est exposee).
/// </summary>
public class AuditEntry
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Identifiant de l'acteur : domaine\utilisateur, ou compte technique.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Adresse IP d'origine de l'action, quand elle est connue.</summary>
    public string? ActorIpAddress { get; set; }

    public AuditAction Action { get; set; }

    public AuditOutcome Outcome { get; set; } = AuditOutcome.Succes;

    /// <summary>Type d'objet concerne (N4Environment, N4Component...).</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Cle de l'objet concerne.</summary>
    public string? EntityId { get; set; }

    /// <summary>Libelle lisible de l'objet, conserve meme si l'objet est renomme ensuite.</summary>
    public string? EntityLabel { get; set; }

    /// <summary>Environnement concerne, pour filtrer l'audit par perimetre.</summary>
    public Guid? EnvironmentId { get; set; }

    /// <summary>Valeurs avant modification, en JSON. Null a la creation.</summary>
    public string? BeforeJson { get; set; }

    /// <summary>Valeurs apres modification, en JSON. Null a la suppression.</summary>
    public string? AfterJson { get; set; }

    /// <summary>
    /// Motif ou justification. Obligatoire pour un contournement ou une
    /// operation mutative en Production (FR-011, FR-027).
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>Identifiant de correlation, pour relier les entrees d'une meme operation.</summary>
    public string? CorrelationId { get; set; }

    public string? Detail { get; set; }
}
