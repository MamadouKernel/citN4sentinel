namespace N4Sentinel.Domain;

/// <summary>
/// État d'une exécution de SOP — plus simple que <see cref="ExecutionStatus"/> :
/// pas d'automatisation, donc pas d'approbation, de pause, ni de réconciliation
/// après un arrêt brutal à distinguer. FR-089.
/// </summary>
public enum SopExecutionStatus
{
    EnCours = 0,
    Termine = 1,
    Abandonne = 2
}

/// <summary>
/// Résultat attesté par l'opérateur, DISTINCT du statut « Terminé » (FR-089D).
/// Aller au bout des étapes ne dit pas si l'incident est résolu — seul un
/// humain peut le dire. C'est cette attestation, et elle seule, qui alimente
/// le taux de réussite historique proposé aux futurs incidents similaires.
/// </summary>
public enum SopOutcome
{
    ProblemeResolu = 0,
    ProblemeNonResolu = 1,

    /// <summary>La procédure a été suivie, mais son effet réel reste incertain.</summary>
    Indetermine = 2
}

/// <summary>
/// État d'une étape de SOP. Toujours confirmée à la main par l'opérateur —
/// jamais déduite d'une commande technique, puisqu'il n'y en a pas : un SOP
/// n'automatise rien.
/// </summary>
public enum SopExecutionStepState
{
    AVenir = 0,
    EnCours = 1,

    /// <summary>Confirmée, conforme au résultat attendu.</summary>
    Confirmee = 2,

    /// <summary>
    /// Confirmée, mais avec un écart par rapport au résultat attendu (FR-089C).
    /// Ce n'est pas un échec — c'est un constat qui ne doit pas passer inaperçu.
    /// </summary>
    Ecart = 3,

    Ignoree = 4,
    Annulee = 5
}

/// <summary>Nature du lien entre un SOP et l'objet auquel il se rattache (FR-089D).</summary>
public enum SopAssociationKind
{
    Composant = 0,
    Signature = 1,
    SessionDiagnostic = 2
}

/// <summary>
/// Procédure opérationnelle standard (SOP) — FR-087 à FR-089D.
///
/// À LA DIFFÉRENCE D'UN WORKFLOW, UN SOP N'AUTOMATISE RIEN. Chaque étape est
/// réalisée à la main par l'opérateur ; l'application se contente de la
/// présenter, de tracer sa confirmation, sa preuve et ses écarts. C'est le
/// même principe que la base documentaire (FR-084/085) : l'application
/// conseille et trace, elle n'agit jamais à la place de quelqu'un.
///
/// Le cycle de vie (Brouillon → Validé, versionnement dès qu'un SOP déjà
/// exécuté est modifié) est calqué sur celui de <see cref="Workflow"/> — voir
/// <c>SopService.SaveAsync</c> pour la même logique de bascule de version.
/// </summary>
public class Sop : AuditableEntity
{
    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    /// <summary>Identifiant stable à travers les versions.</summary>
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>Numéro de version, croissant à chaque modification une fois exécuté.</summary>
    public int Version { get; set; } = 1;
    public LifecycleStatus Status { get; set; } = LifecycleStatus.Brouillon;

    /// <summary>Vrai dès qu'une exécution s'est appuyée sur cette version. Elle devient alors immuable.</summary>
    public bool HasBeenExecuted { get; set; }

    /// <summary>
    /// Vrai pour les SOP sensibles (ex. reconstitution ActiveMQ/KahaDB) :
    /// démarrer une exécution exige alors la capacité
    /// <see cref="N4Policies.PeutExecuterActionsSensibles"/>, pas seulement
    /// <see cref="N4Policies.PeutExecuter"/> — vérifié à la fois côté écran
    /// et côté service (<c>SopExecutionService.StartAsync</c>), pas
    /// seulement côté écran.
    /// </summary>
    public bool RequiresElevatedRole { get; set; }

    // --- Gabarit structuré (FR-088) ---------------------------------------
    public string? Objective { get; set; }
    public string? Scope { get; set; }
    public string? Prerequisites { get; set; }
    public string? Risks { get; set; }
    public string? Controls { get; set; }
    public string? ExpectedOutcome { get; set; }
    public string? RollbackPlan { get; set; }
    public string? EscalationPath { get; set; }

    /// <summary>
    /// Version N4 à laquelle ce SOP s'applique, si connue. Un conseil tiré
    /// d'une procédure 3.7 peut être faux en 3.9.
    /// </summary>
    public string? AppliesToVersion { get; set; }

    /// <summary>Document de la base documentaire à l'origine de ce SOP, si généré depuis une réponse (FR-088).</summary>
    public Guid? SourceDocumentId { get; set; }

    /// <summary>Exécution de workflow à l'origine de ce SOP, si généré à partir d'une opération réussie (FR-089B).</summary>
    public Guid? SourceExecutionId { get; set; }

    /// <summary>
    /// FR-097 : session de diagnostic CLÔTURÉE à l'origine de ce SOP, si
    /// généré à partir d'un incident résolu — pas seulement d'une opération
    /// orchestrée réussie. Capitaliser un incident traité à la main (actions
    /// externes déclarées) mérite la même reprise qu'une opération pilotée.
    /// </summary>
    public Guid? SourceDiagnosticSessionId { get; set; }

    public ICollection<SopStep> Steps { get; set; } = [];
    public ICollection<SopAssociation> Associations { get; set; } = [];

    public bool IsUsable => Status is LifecycleStatus.Valide or LifecycleStatus.Actif && Steps.Count > 0;
    public string DisplayName => $"{Title} (v{Version})";
}

/// <summary>Étape d'un SOP — une instruction, pas une commande.</summary>
public class SopStep : AuditableEntity
{
    public Guid SopId { get; set; }
    public Sop? Sop { get; set; }

    public int Order { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Ce que l'opérateur doit faire, précisément. Toujours obligatoire : une
    /// étape de SOP EST une instruction, contrairement à une étape de workflow
    /// qui peut être une commande technique auto-porteuse.
    /// </summary>
    public string Instruction { get; set; } = string.Empty;

    /// <summary>Ce à quoi ressemble une étape réussie, pour que l'opérateur puisse se contrôler lui-même.</summary>
    public string? ExpectedResult { get; set; }

    public Guid? ComponentId { get; set; }
    public N4Component? Component { get; set; }

    public bool IsSkippable { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Rattachement d'un SOP à un composant, une signature d'anomalie ou une
/// session de diagnostic — la matière première de la suggestion de
/// réutilisation (FR-089D). Un même SOP peut porter plusieurs associations.
/// </summary>
public class SopAssociation : AuditableEntity
{
    public Guid SopId { get; set; }
    public Sop? Sop { get; set; }

    public SopAssociationKind Kind { get; set; }

    public Guid? ComponentId { get; set; }
    public ComponentRole? Role { get; set; }

    public Guid? SignatureId { get; set; }
    public string? SignatureCode { get; set; }

    public Guid? DiagnosticSessionId { get; set; }
}

/// <summary>
/// Exécution d'un SOP — la trace de son déroulement réel (FR-089, FR-089A-C).
/// </summary>
public class SopExecution : AuditableEntity
{
    public Guid SopId { get; set; }
    public Sop? Sop { get; set; }

    /// <summary>Recopiés du modèle : le rapport reste exact même si le SOP évolue ensuite.</summary>
    public int SopVersion { get; set; }
    public string SopCode { get; set; } = string.Empty;
    public string SopTitle { get; set; } = string.Empty;

    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }
    public string EnvironmentCode { get; set; } = string.Empty;

    public SopExecutionStatus Status { get; set; } = SopExecutionStatus.EnCours;

    public string StartedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? TicketReference { get; set; }

    /// <summary>Alerte à l'origine de cette exécution, si elle a été ouverte depuis le centre d'alertes.</summary>
    public Guid? SourceAlertId { get; set; }

    /// <summary>Session de diagnostic à l'origine de cette exécution, si elle en découle.</summary>
    public Guid? SourceDiagnosticSessionId { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }

    public string? AbandonReason { get; set; }

    /// <summary>Attestation du résultat réel (FR-089D), saisie une fois l'exécution finie.</summary>
    public SopOutcome? Outcome { get; set; }
    public string? OutcomeNote { get; set; }
    public string? OutcomeDeclaredBy { get; set; }
    public DateTimeOffset? OutcomeDeclaredAt { get; set; }

    /// <summary>Identifiant de corrélation, repris dans les journaux applicatifs.</summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public ICollection<SopExecutionStep> Steps { get; set; } = [];

    public bool IsFinished => Status is SopExecutionStatus.Termine or SopExecutionStatus.Abandonne;
    public TimeSpan Duration => (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}

/// <summary>Étape d'une exécution de SOP, avec sa preuve et ses écarts éventuels.</summary>
public class SopExecutionStep : AuditableEntity
{
    public Guid SopExecutionId { get; set; }
    public SopExecution? SopExecution { get; set; }

    public int Order { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Instruction { get; set; } = string.Empty;
    public string? ExpectedResult { get; set; }

    public Guid? ComponentId { get; set; }
    public string? ComponentName { get; set; }

    public SopExecutionStepState State { get; set; } = SopExecutionStepState.AVenir;

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    public bool IsSkippable { get; set; }

    /// <summary>Confirmation (FR-089A) : qui, quand.</summary>
    public string? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>Preuve — ce que l'opérateur a constaté, saisi par lui, jamais déduit.</summary>
    public string? Evidence { get; set; }

    /// <summary>
    /// Écart par rapport au résultat attendu, s'il y en a un (FR-089C).
    /// Distinct de la preuve : la preuve dit CE QUI a été constaté, l'écart
    /// dit EN QUOI ça diffère de ce qui était prévu.
    /// </summary>
    public string? DeviationNote { get; set; }

    public string? SkippedBy { get; set; }
    public string? SkipReason { get; set; }

    /// <summary>
    /// Retour en arrière (FR-089A). Quand une étape déjà confirmée est
    /// rouverte, RIEN N'EST EFFACÉ : la confirmation, la preuve et l'écart
    /// précédents sont d'abord archivés ici, horodatés, avant que l'étape ne
    /// soit réinitialisée — la trace complète d'un SOP compte autant que sa
    /// conclusion.
    /// </summary>
    public string? History { get; set; }

    public bool IsTerminal => State is SopExecutionStepState.Confirmee
                                     or SopExecutionStepState.Ecart
                                     or SopExecutionStepState.Ignoree
                                     or SopExecutionStepState.Annulee;

    public TimeSpan? Duration => StartedAt is null ? null : (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt.Value;
}
