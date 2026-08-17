namespace N4Sentinel.Domain;

/// <summary>
/// Déclaration d'une action manuelle effectuée HORS de N4 Sentinel pendant
/// le traitement d'un incident ou une exécution en cours (§3.10.1, §3.19).
///
/// N4 Sentinel ne peut pas détecter automatiquement une intervention
/// directe sur un serveur (RDP, console, script exécuté à la main) : le
/// texte du cahier des charges autorise explicitement la déclaration comme
/// mécanisme équivalent à la détection ("déclarée OU détectée"). Une fois
/// déclarée, l'action rejoint la chronologie et le journal d'audit — elle
/// n'est plus un angle mort.
/// </summary>
public class ExternalActionDeclaration : AuditableEntity
{
    /// <summary>Dénormalisé depuis la session ou l'exécution parente, pour le filtrage par environnement.</summary>
    public Guid EnvironmentId { get; set; }

    public Guid? DiagnosticSessionId { get; set; }
    public DiagnosticSession? DiagnosticSession { get; set; }

    public Guid? WorkflowExecutionId { get; set; }
    public WorkflowExecution? WorkflowExecution { get; set; }

    public Guid? ComponentId { get; set; }
    public string? ComponentName { get; set; }

    /// <summary>Ce qui a été fait, en clair.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Quand l'action a réellement eu lieu (peut différer de l'horodatage de la déclaration).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public string DeclaredBy { get; set; } = string.Empty;
}
