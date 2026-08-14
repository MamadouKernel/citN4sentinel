namespace N4Sentinel.Domain;

/// <summary>États d'une exécution (FR-020).</summary>
public enum ExecutionStatus
{
    EnPreparation = 0,
    EnAttenteApprobation = 1,
    EnCours = 2,
    EnPause = 3,
    AnnulationDemandee = 4,

    /// <summary>
    /// L'état enregistré et l'état réel divergent. Le moteur refuse de
    /// poursuivre : reprendre sur une base fausse est pire que s'arrêter.
    /// </summary>
    ReconciliationRequise = 5,

    TermineSucces = 6,
    TermineAvecAvertissements = 7,
    Echec = 8,
    Annule = 9
}

/// <summary>États d'une étape en cours d'exécution (FR-020).</summary>
public enum ExecutionStepState
{
    AVenir = 0,
    EnAttente = 1,
    EnCours = 2,

    /// <summary>Action émise, on cherche la preuve que le résultat est atteint.</summary>
    Verification = 3,

    Reussi = 4,
    Avertissement = 5,
    Bloque = 6,
    Echec = 7,
    Ignore = 8,
    Annule = 9
}

/// <summary>
/// Exécution d'un workflow.
///
/// L'état vit en base, pas en mémoire. Une exécution interrompue par le
/// redémarrage du serveur applicatif doit pouvoir reprendre là où elle en
/// était (NFR-002) — et surtout ne jamais rejouer aveuglément une action déjà
/// réalisée.
/// </summary>
public class WorkflowExecution : AuditableEntity
{
    public Guid WorkflowId { get; set; }
    public Workflow? Workflow { get; set; }

    /// <summary>
    /// Version du workflow au moment du lancement, recopiée. Le rapport reste
    /// exact même si le workflow évolue ensuite.
    /// </summary>
    public int WorkflowVersion { get; set; }
    public string WorkflowName { get; set; } = string.Empty;
    public WorkflowKind Kind { get; set; }

    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }
    public string EnvironmentCode { get; set; } = string.Empty;

    public ExecutionStatus Status { get; set; } = ExecutionStatus.EnPreparation;

    /// <summary>Simulation : aucune commande n'est émise (FR-005).</summary>
    public bool IsSimulation { get; set; }

    // --- Motif et rattachement (FR-011) ---------------------------------
    public string RequestedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? TicketReference { get; set; }
    public string? ExpectedImpact { get; set; }

    // --- Approbation (FR-013) -------------------------------------------
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// Battement du moteur. Sert à repérer, au redémarrage, une exécution
    /// abandonnée en vol par un arrêt brutal.
    /// </summary>
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    public string? PauseRequestedBy { get; set; }
    public string? CancelRequestedBy { get; set; }

    /// <summary>Motif de l'issue : cause de l'échec, ou nature des avertissements.</summary>
    public string? Outcome { get; set; }

    /// <summary>Identifiant de corrélation, repris dans les journaux applicatifs.</summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public ICollection<ExecutionStep> Steps { get; set; } = [];

    public bool IsActive => Status is ExecutionStatus.EnCours
                                    or ExecutionStatus.EnPause
                                    or ExecutionStatus.AnnulationDemandee
                                    or ExecutionStatus.EnAttenteApprobation
                                    or ExecutionStatus.EnPreparation;

    public bool IsFinished => Status is ExecutionStatus.TermineSucces
                                     or ExecutionStatus.TermineAvecAvertissements
                                     or ExecutionStatus.Echec
                                     or ExecutionStatus.Annule;

    public TimeSpan? Duration => StartedAt is null ? null : (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt.Value;
}

/// <summary>Étape d'une exécution, avec ses preuves (FR-021).</summary>
public class ExecutionStep : AuditableEntity
{
    public Guid ExecutionId { get; set; }
    public WorkflowExecution? Execution { get; set; }

    public int Order { get; set; }
    public string Name { get; set; } = string.Empty;
    public StepAction Action { get; set; }

    public Guid? ComponentId { get; set; }
    public string? ComponentName { get; set; }
    public string? HostName { get; set; }

    public ExecutionStepState State { get; set; } = ExecutionStepState.AVenir;

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Message d'avancement, rafraîchi pendant l'attente.</summary>
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// Preuve technique du résultat : ligne de journal reconnue, statut de
    /// service observé. C'est elle qui distingue « j'ai envoyé la commande »
    /// de « le résultat est atteint ».
    /// </summary>
    public string? Evidence { get; set; }

    public string? Error { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Recopiés du modèle, pour que le rapport reste exact si le workflow évolue.</summary>
    public int MaxRetries { get; set; }
    public bool AutomaticRetry { get; set; }

    /// <summary>Contournement : qui, pourquoi, et le risque accepté (FR-027).</summary>
    public string? SkippedBy { get; set; }
    public string? SkipReason { get; set; }

    /// <summary>Confirmation d'une intervention manuelle (FR-026).</summary>
    public string? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? OperatorNote { get; set; }

    public int TimeoutSeconds { get; set; } = 1800;
    public int ExpectedSeconds { get; set; } = 60;
    public bool IsSkippable { get; set; }
    public bool RequiresConfirmation { get; set; }
    public StepFailurePolicy FailurePolicy { get; set; } = StepFailurePolicy.Bloquer;
    public string? Instruction { get; set; }

    public bool IsTerminal => State is ExecutionStepState.Reussi
                                    or ExecutionStepState.Avertissement
                                    or ExecutionStepState.Echec
                                    or ExecutionStepState.Ignore
                                    or ExecutionStepState.Annule;

    public TimeSpan? Duration => StartedAt is null ? null : (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt.Value;
}

/// <summary>
/// Verrou d'environnement (FR-015, recette AC-12).
///
/// Une seule opération mutative à la fois par environnement. Deux séquences
/// d'arrêt lancées en parallèle sur le même écosystème produiraient un état
/// que personne ne saurait plus décrire — et le rapport de chacune serait faux.
///
/// Le verrou porte une date d'expiration : sans elle, un serveur applicatif
/// qui meurt en tenant le verrou bloquerait l'environnement définitivement,
/// et il faudrait intervenir en base pour le libérer.
/// </summary>
public class EnvironmentLock
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    public Guid ExecutionId { get; set; }

    public string HeldBy { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset AcquiredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Prolongée par le battement du moteur tant que l'exécution vit.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
}
