namespace N4Sentinel.Domain;

/// <summary>
/// MERISE — ENT_VRF. Contrôle préalable MODÈLE rattaché à un Workflow (template).
/// Chaque ligne est un test exécuté avant de laisser passer une exécution
/// Workflow en Production (RG-0301→RG-0308).
///
/// VARIANTE TABULAIRE par rapport à <see cref="WorkflowExecution.PreflightJson"/>
/// (qui reste le JSON de snapshot de l'exécution). Cette version est le
/// MODELE paramétrable (TOP 10 causes P1 Navis) qui s'applique à TOUTES les
/// exécutions du Workflow.
/// </summary>
public class WorkflowPreCheck : AuditableEntity
{
    public Guid WorkflowId { get; set; }
    public Workflow? Workflow { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public WorkflowPreCheckCategory Category { get; set; }

    /// <summary>
    /// Composant concerné. Null = contrôle transverse (ex. toutes horloges env).
    /// </summary>
    public Guid? TargetComponentId { get; set; }
    public N4Component? TargetComponent { get; set; }

    /// <summary>Description longue / motif / règle du contrôle.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Commande Powershell / SQL / HTTP / pattern à exécuter.</summary>
    public string ControlInstruction { get; set; } = string.Empty;

    /// <summary>
    /// Seuil déclenchant un avertissement (CheckOutcome = Avertissement).
    /// Format "valeur" bruttte : type / unité dépend de Category.
    /// </summary>
    public string? WarningThreshold { get; set; }

    /// <summary>
    /// Seuil déclenchant un blocage (CheckOutcome = Bloquant).
    /// Si atteint, l'exécution Workflow ne démarre PAS (RG-0305).
    /// </summary>
    public string? BlockingThreshold { get; set; }

    /// <summary>
    /// Timeout pour le contrôle, en secondes. Au delà = ImpossibleAVerifier.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    public int Order { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Vrai = si Bloquant, les approbateurs reçoivent une demande explicite
    /// de contournement avant exécution (RG-0304 Double approbation Prod).
    /// </summary>
    public bool RequiresExplicitApprovalIfBlocking { get; set; } = false;

    /// <summary>
    /// Procédure de remédiation / contournement affichée à l'opérateur si
    /// le contrôle échoue en Bloquant (lien SOP recommandé).
    /// </summary>
    public string? RemediationProcedure { get; set; }

    public Guid? SuggestedSopId { get; set; }
    public Sop? SuggestedSop { get; set; }
}
