namespace N4Sentinel.Domain;

/// <summary>Nature d'un workflow.</summary>
public enum WorkflowKind
{
    DemarrageComplet = 0,
    ArretComplet = 1,
    RedemarrageComplet = 2,
    OperationPartielle = 3,
    OperationUnitaire = 4,
    RollingRestart = 5,
    ControleSeul = 6,
    RepriseApresEchec = 7
}

/// <summary>Action portée par une étape.</summary>
public enum StepAction
{
    /// <summary>Contrôle en lecture seule. Ne modifie rien.</summary>
    Verifier = 0,
    Demarrer = 1,
    Arreter = 2,
    Redemarrer = 3,

    /// <summary>
    /// L'étape attend un geste humain. Le moteur présente ce qu'il faut faire
    /// et attend confirmation : certaines actions ne peuvent pas, ou ne doivent
    /// pas, être exécutées automatiquement.
    /// </summary>
    InterventionManuelle = 4,

    /// <summary>Temporisation explicite, motivée. À utiliser avec parcimonie.</summary>
    Attendre = 5,

    /// <summary>Arrêt forcé explicite (FR-029B).</summary>
    ArretForce = 6
}

/// <summary>Conduite à tenir quand une étape n'aboutit pas.</summary>
public enum StepFailurePolicy
{
    /// <summary>La séquence s'arrête. Choix par défaut, et le seul sûr par défaut.</summary>
    Bloquer = 0,

    /// <summary>La séquence continue, l'étape est marquée en avertissement.</summary>
    Continuer = 1,

    /// <summary>Le moteur suspend et demande à l'opérateur ce qu'il faut faire.</summary>
    DemanderALOperateur = 2
}

/// <summary>
/// Modèle d'opération, versionné (FR-003).
///
/// UN WORKFLOW UTILISÉ NE SE MODIFIE PLUS. Toute modification crée une
/// nouvelle version, et une exécution déjà réalisée reste rattachée à la
/// version exacte qui l'a produite. Sans cela, le rapport d'une opération de
/// la semaine dernière décrirait des étapes qui n'étaient pas celles qui ont
/// tourné — et la traçabilité ne vaudrait plus rien le jour où elle compte.
/// </summary>
public class Workflow : AuditableEntity
{
    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    /// <summary>Identifiant stable à travers les versions.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public WorkflowKind Kind { get; set; }

    /// <summary>Numéro de version, croissant à chaque modification.</summary>
    public int Version { get; set; } = 1;

    public LifecycleStatus Status { get; set; } = LifecycleStatus.Brouillon;

    public string? Description { get; set; }

    /// <summary>
    /// Vrai dès qu'une exécution s'est appuyée sur cette version. Elle devient
    /// alors immuable : la modifier reviendrait à réécrire l'histoire.
    /// </summary>
    public bool HasBeenExecuted { get; set; }

    /// <summary>
    /// Exige une approbation avant lancement. Positionné d'office en
    /// Production pour toute opération mutative.
    /// </summary>
    public bool RequiresApproval { get; set; }

    /// <summary>
    /// Exige DEUX approbateurs distincts, ni l'un ni l'autre le demandeur
    /// (FR-013). Réservé aux opérations dont l'impact justifie un double
    /// regard — n'a d'effet que si <see cref="RequiresApproval"/> l'est aussi ;
    /// l'inverse (double sans simple) n'aurait pas de sens.
    /// </summary>
    public bool RequiresDoubleApproval { get; set; }

    /// <summary>Niveau d'automatisation de ce workflow (Palier 1 ou Palier 2).</summary>
    public AutomationLevel AutomationLevel { get; set; } = AutomationLevel.SemiAutomatique;

    public ICollection<WorkflowStep> Steps { get; set; } = [];

    /// <summary>
    /// Un workflow doit être Validé ou Actif pour être lancé, et comporter au
    /// moins une étape.
    /// </summary>
    public bool IsRunnable => Status is LifecycleStatus.Valide or LifecycleStatus.Actif
                              && Steps.Count > 0;

    public string DisplayName => $"{Name} (v{Version})";
}

/// <summary>Étape d'un workflow (FR-004).</summary>
public class WorkflowStep : AuditableEntity
{
    public Guid WorkflowId { get; set; }
    public Workflow? Workflow { get; set; }

    public int Order { get; set; }
    public string Name { get; set; } = string.Empty;
    public StepAction Action { get; set; }

    /// <summary>Composant visé. Null pour un contrôle d'infrastructure global.</summary>
    public Guid? ComponentId { get; set; }
    public N4Component? Component { get; set; }

    /// <summary>Ce qui est attendu de l'opérateur, pour une intervention manuelle.</summary>
    public string? Instruction { get; set; }

    /// <summary>Durée normale indicative, affichée pendant l'attente.</summary>
    public int ExpectedSeconds { get; set; } = 60;

    /// <summary>
    /// Au-delà, l'étape est signalée en avertissement dans l'écran d'exécution
    /// (FR-004) sans être interrompue — c'est un signal précoce, distinct du
    /// timeout qui, lui, met fin à l'étape.
    /// </summary>
    public int WarningThresholdSeconds { get; set; } = 120;

    /// <summary>Au-delà, l'étape est en échec ou passe la main, selon la politique.</summary>
    public int TimeoutSeconds { get; set; } = 1800;

    public int MaxRetries { get; set; }

    /// <summary>Attente entre deux tentatives, automatiques ou décidées par l'opérateur (FR-004).</summary>
    public int RetryDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Nouvelle tentative automatique. INTERDITE sur une action mutative sauf
    /// autorisation explicite : réessayer un arrêt en boucle sans comprendre
    /// pourquoi il a échoué est le meilleur moyen d'aggraver un incident.
    /// </summary>
    public bool AutomaticRetry { get; set; }

    public StepFailurePolicy FailurePolicy { get; set; } = StepFailurePolicy.Bloquer;

    /// <summary>
    /// Étape contournable. Un contrôle non déclaré contournable ne peut jamais
    /// être ignoré, même par un habilité (FR-022).
    /// </summary>
    public bool IsSkippable { get; set; }

    /// <summary>Confirmation explicite de l'opérateur avant exécution.</summary>
    public bool RequiresConfirmation { get; set; }

    /// <summary>
    /// Preuve jointe obligatoire pour confirmer une intervention manuelle
    /// (FR-026). Ne s'applique qu'aux étapes <see cref="StepAction.InterventionManuelle"/> ;
    /// sans elle, la confirmation est refusée tant qu'aucun fichier n'est joint.
    /// </summary>
    public bool RequiresEvidenceFile { get; set; }

    /// <summary>
    /// Étape déclarée indépendante, donc parallélisable. Par défaut faux : les
    /// dépendances N4 — nœuds Cluster un par un, XPS après Bridge — doivent
    /// rester séquentielles (FR-023).
    /// </summary>
    public bool CanRunInParallel { get; set; }

    public string? Notes { get; set; }
}
