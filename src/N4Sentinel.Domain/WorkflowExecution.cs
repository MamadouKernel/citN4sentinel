namespace N4Sentinel.Domain;

/// <summary>
/// §3.19 : classification exacte d'un échec d'étape, distincte du message
/// libre porté par <see cref="ExecutionStep.Error"/>. Un texte libre ne peut
/// ni se filtrer, ni se compter, ni déclencher une réponse différenciée
/// (ex. proposer l'arrêt forcé seulement pour le cas StopPending connu).
/// </summary>
public enum StepErrorType
{
    /// <summary>Le connecteur a refusé d'émettre la commande (accès, connectivité).</summary>
    CommandeRefusee = 0,

    /// <summary>Le délai imparti a expiré avant d'atteindre l'état attendu.</summary>
    TimeoutAttente = 1,

    /// <summary>
    /// Comportement N4 connu et documenté : le Standby Center Node (et
    /// d'autres composants occupés à vider leurs files) reste en StopPending
    /// sans jamais rendre la main au gestionnaire de services.
    /// </summary>
    ComportementConnuStopPending = 2,

    /// <summary>Le référentiel est incomplet (nom de service, chemin de journal...).</summary>
    ComposantNonConfigure = 3,

    /// <summary>Un prérequis déclaré n'est pas prouvé opérationnel (FR-044).</summary>
    PrerequisNonSatisfait = 4,

    /// <summary>Classé sans plus de précision — le message libre reste la seule source.</summary>
    Inconnu = 5
}

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

    /// <summary>Niveau d'automatisation de cette exécution (Palier 1 ou Palier 2).</summary>
    public AutomationLevel AutomationLevel { get; set; } = AutomationLevel.SemiAutomatique;

    /// <summary>Vrai si le basculement d'urgence au mode semi-automatique (Palier 1) a été déclenché.</summary>
    public bool IsFallbackSemiAutoForced { get; set; }

    // --- Motif et rattachement (FR-011) ---------------------------------
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>
    /// Identifiant applicatif de l'operateur SOUS LEQUEL les commandes sont
    /// emises, fige au lancement.
    ///
    /// Distinct du demandeur : une operation peut etre preparee par l'un et
    /// lancee par l'autre, et c'est celui qui lance qui engage sa
    /// responsabilite en emettant reellement les commandes. Figer l'identite
    /// ici garantit qu'une reprise trois heures plus tard, par une autre
    /// personne, restera attribuee a celle qui a lance - et que la piste
    /// applicative et le journal de securite du serveur N4 ne pourront pas
    /// diverger.
    ///
    /// Vide sur les executions anterieures a ce mecanisme, et sur les sites qui
    /// n'emploient que des comptes partages.
    /// </summary>
    public string? OperatingIdentityLogin { get; set; }

    /// <summary>
    /// Etiquette de tracabilite figee au lancement, redigee par
    /// <see cref="ActingIdentity"/> : « N4Sentinel · DOMAINE\utilisateur ».
    ///
    /// Conservee telle quelle plutot que recalculee a l'affichage : le compte
    /// de l'operateur peut changer, etre efface, ou son proprietaire quitter
    /// l'entreprise. Un rapport d'execution doit continuer de dire sous quelle
    /// identite les commandes SONT PARTIES, des mois plus tard.
    /// </summary>
    public string? OperatingIdentityLabel { get; set; }

    public string? Reason { get; set; }
    public string? TicketReference { get; set; }
    public string? ExpectedImpact { get; set; }

    /// <summary>Début de la fenêtre d'intervention autorisée (FR-011).</summary>
    public DateTimeOffset? StartWindow { get; set; }

    /// <summary>Fin de la fenêtre d'intervention autorisée (FR-011).</summary>
    public DateTimeOffset? EndWindow { get; set; }

    /// <summary>Durée estimée totale agrégée des étapes (FR-016).</summary>
    public TimeSpan? EstimatedTotalDuration { get; set; }

    // --- Approbation (FR-013) -------------------------------------------
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>
    /// Recopié du workflow au moment de la préparation, pour que l'exigence
    /// reste exacte même si le workflow évolue avant que l'approbation ait
    /// lieu.
    /// </summary>
    public bool RequiresDoubleApproval { get; set; }

    /// <summary>Second approbateur, distinct du demandeur ET du premier approbateur.</summary>
    public string? SecondApprovedBy { get; set; }
    public DateTimeOffset? SecondApprovedAt { get; set; }

    // --- Continuite Center (FR-046/047) ----------------------------------
    /// <summary>
    /// Recopié au moment de la préparation : vrai si cette exécution comporte
    /// une action d'arrêt ou de redémarrage visant le Center. Évite de
    /// redériver la même condition à chaque écran ou contrôle.
    /// </summary>
    public bool ContinuityChoiceRequired { get; set; }

    /// <summary>
    /// Null tant qu'un choix n'a pas été fait explicitement. Le pré-check
    /// bloque le lancement en son absence quand ContinuityChoiceRequired est
    /// vrai.
    /// </summary>
    public CenterContinuityChoice? ContinuityChoice { get; set; }
    public string? ContinuityChoiceBy { get; set; }
    public DateTimeOffset? ContinuityChoiceAt { get; set; }

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

    /// <summary>
    /// État réel des composants touchés, recollecté après une annulation
    /// (FR-025) — ce que l'opérateur doit constater avant toute reprise, pas
    /// une phrase générique.
    /// </summary>
    public string? PostCancellationReport { get; set; }

    /// <summary>
    /// FR-025 : vrai si l'annulation laisse au moins un composant dans un état
    /// que l'application ne peut pas confirmer stable — auquel cas une
    /// intervention manuelle ou une escalade est requise, jamais un retour
    /// automatique silencieux à un état présumé sain.
    /// </summary>
    public bool RequiresManualInterventionAfterCancel { get; set; }

    /// <summary>Identifiant de corrélation, repris dans les journaux applicatifs.</summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N")[..12];

    // --- Pré-check (FR-012) ---------------------------------------------
    /// <summary>
    /// Rapport des contrôles préalables, conservé avec l'exécution. Trois mois
    /// plus tard, on doit pouvoir dire ce qui avait été vérifié AVANT de
    /// lancer, pas seulement ce qui s'est passé pendant.
    /// </summary>
    public string? PreflightJson { get; set; }
    public DateTimeOffset? PreflightAt { get; set; }

    /// <summary>Vrai si au moins un contrôle bloquant a échoué au dernier passage.</summary>
    public bool PreflightBlocked { get; set; }

    /// <summary>
    /// Le pré-check a été passé et n'a rien bloqué. Une opération mutative ne
    /// se lance pas sans cela : découvrir un serveur injoignable à la septième
    /// étape d'un arrêt complet est le pire moment possible.
    /// </summary>
    public bool PreflightCleared => PreflightAt is not null && !PreflightBlocked;

    public ICollection<ExecutionStep> Steps { get; set; } = [];

    /// <summary>Actions manuelles hors N4 Sentinel déclarées pendant cette exécution (§3.19).</summary>
    public ICollection<ExternalActionDeclaration> ExternalActions { get; set; } = [];

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

    /// <summary>
    /// La commande technique réelle (script) exécutée en arrière-plan,
    /// expurgée des éventuels secrets (FR-028).
    /// </summary>
    public string? ExecutedCommand { get; set; }

    public string? Error { get; set; }

    /// <summary>§3.19 : classification exacte de <see cref="Error"/>, jamais devinée après coup.</summary>
    public StepErrorType? ErrorType { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Recopiés du modèle, pour que le rapport reste exact si le workflow évolue.</summary>
    public int MaxRetries { get; set; }
    public bool AutomaticRetry { get; set; }
    public int RetryDelaySeconds { get; set; } = 30;

    /// <summary>Horodatage à partir duquel une nouvelle tentative automatique peut repartir (FR-004).</summary>
    public DateTimeOffset? RetryNotBeforeAt { get; set; }

    /// <summary>Contournement : qui, pourquoi, et le risque accepté (FR-027).</summary>
    public string? SkippedBy { get; set; }
    public string? SkipReason { get; set; }

    /// <summary>
    /// Second regard sur un contournement, exigé en Production quand la
    /// matrice de criticité impose une double approbation (FR-013/FR-027).
    /// Tant que non renseigné et exigé, le contournement reste en attente —
    /// l'étape n'est PAS marquée Ignorée par le seul demandeur.
    /// </summary>
    public string? SkipCoApprovedBy { get; set; }
    public DateTimeOffset? SkipCoApprovedAt { get; set; }

    /// <summary>
    /// Arrêt forcé (FR-029B) : décidé par un opérateur sur une étape d'arrêt
    /// restée bloquée (StopPending), jamais déclenché automatiquement par le
    /// moteur. La commande d'arrêt est réémise, mais l'absence de preuve
    /// applicative reste dite explicitement — l'arrêt forcé n'est pas une
    /// fabrication de succès.
    /// </summary>
    public string? ForcedStopBy { get; set; }
    public string? ForcedStopReason { get; set; }

    /// <summary>Confirmation d'une intervention manuelle (FR-026).</summary>
    public string? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? OperatorNote { get; set; }

    /// <summary>Preuve jointe obligatoire (FR-026), recopiée du modèle.</summary>
    public bool RequiresEvidenceFile { get; set; }
    public string? EvidenceFileName { get; set; }
    public string? EvidenceFileContentType { get; set; }
    public byte[]? EvidenceFileContent { get; set; }

    /// <summary>
    /// Session de diagnostic ouverte pour aider à la décision sur cette étape
    /// bloquée (FR-029). Reliée, jamais recomposée : les hypothèses affichées
    /// à l'écran d'exécution sont celles, réelles, de cette session.
    /// </summary>
    public Guid? DiagnosticSessionId { get; set; }

    public int TimeoutSeconds { get; set; } = 1800;
    public int ExpectedSeconds { get; set; } = 60;
    public int WarningThresholdSeconds { get; set; } = 120;
    public bool IsSkippable { get; set; }
    public bool RequiresConfirmation { get; set; }

    /// <summary>
    /// Recopié du modèle (FR-023). Le moteur ne parallélise que ce que le
    /// validateur de séquence a jugé indépendant ; voir <c>OrchestrationEngine</c>.
    /// </summary>
    public bool CanRunInParallel { get; set; }
    public StepFailurePolicy FailurePolicy { get; set; } = StepFailurePolicy.Bloquer;
    public string? Instruction { get; set; }

    public bool IsTerminal => State is ExecutionStepState.Reussi
                                    or ExecutionStepState.Avertissement
                                    or ExecutionStepState.Echec
                                    or ExecutionStepState.Ignore
                                    or ExecutionStepState.Annule;

    /// <summary>Indique si l'action est destructrice et nécessite un contournement explicite en cas d'échec (FR-004).</summary>
    public bool IsDestructive => Action is StepAction.Arreter or StepAction.Redemarrer or StepAction.ArretForce;

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
