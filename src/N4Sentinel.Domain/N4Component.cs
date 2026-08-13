namespace N4Sentinel.Domain;

/// <summary>
/// Composant de l'ecosysteme N4 : noeud Cluster, Center, Standby, Bridge, XPS,
/// ECN4, base de donnees, dossier partage, interface EDI... (FR-002)
///
/// Regle non negociable du cahier des charges : aucune action technique n'est
/// autorisee sur un composant qui n'a pas ete prealablement enregistre et
/// valide dans ce referentiel.
/// </summary>
public class N4Component : AuditableEntity
{
    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    /// <summary>Serveur hote. Null pour un composant non heberge (systeme externe).</summary>
    public Guid? ServerId { get; set; }
    public N4Server? Server { get; set; }

    /// <summary>Nom logique affiche a l'operateur (ex. "Cluster Node 2").</summary>
    public string LogicalName { get; set; } = string.Empty;

    public ComponentRole Role { get; set; } = ComponentRole.Autre;

    /// <summary>Nom EXACT du service Windows. Sa justesse conditionne tout le pilotage.</summary>
    public string? WindowsServiceName { get; set; }

    /// <summary>Nom du processus attendu, pour confirmer l'etat autrement que par le service.</summary>
    public string? ProcessName { get; set; }

    /// <summary>Endpoint applicatif autorise (URL de sante, console d'administration).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Port applicatif principal, teste lors des controles.</summary>
    public int? Port { get; set; }

    public CriticalityLevel Criticality { get; set; } = CriticalityLevel.Moyenne;

    public ControlMode ControlMode { get; set; } = ControlMode.SuperviseSeulement;

    public LifecycleStatus Status { get; set; } = LifecycleStatus.Brouillon;

    /// <summary>
    /// Rang dans la sequence de demarrage. L'arret ne se deduit PAS en
    /// inversant ce rang : il suit sa propre sequence, definie au workflow.
    /// </summary>
    public int StartOrder { get; set; }

    public string? Description { get; set; }

    public string? TechnicalOwner { get; set; }

    /// <summary>
    /// Profil de preuve de demarrage. Repris de la section Readiness des
    /// scripts PowerShell d'exploitation, dont la logique est deja eprouvee.
    /// </summary>
    public ReadinessProfile Readiness { get; set; } = new();

    public ICollection<ComponentDependency> Dependencies { get; set; } = [];
    public ICollection<ComponentHealthCheck> HealthChecks { get; set; } = [];

    /// <summary>
    /// Un composant n'est pilotable que s'il est declare comme tel ET valide.
    /// Les deux conditions sont necessaires : un composant pilotable encore
    /// en brouillon ne doit recevoir aucune commande.
    /// </summary>
    public bool CanBeControlled =>
        ControlMode == ControlMode.Pilotable &&
        Status is LifecycleStatus.Valide or LifecycleStatus.Actif;
}

/// <summary>
/// Comment prouver qu'un composant a reellement fini de demarrer.
///
/// Un service Windows "Running" ne prouve rien : il signale que Windows a
/// lance le processus, pas que la JVM N4 a charge sa configuration, ouvert la
/// base, rejoint le cluster Hazelcast et initialise son tier web. La preuve
/// est le marqueur ecrit dans le journal applicatif.
///
/// Pour les noeuds Cluster et le Center, la documentation editeur designe
/// explicitement ce marqueur : "Web tier servlet 'action' initialized".
/// </summary>
public class ReadinessProfile
{
    /// <summary>
    /// Chemin du journal, tel que vu PAR LE SERVEUR lui-meme (chemin local,
    /// jamais UNC). Accepte un caractere generique : plusieurs composants N4
    /// horodatent le nom de leur fichier, et le journal XPS repart a zero a
    /// chaque demarrage.
    /// </summary>
    public string? LogPath { get; set; }

    /// <summary>
    /// Expressions regulieres marquant la fin d'initialisation. La premiere
    /// qui apparait APRES le lancement vaut preuve. Serialise en JSON.
    /// </summary>
    public List<string> ReadyPatterns { get; set; } = [];

    /// <summary>
    /// Signatures d'echec caracterise. Leur interet est le gain de temps :
    /// des qu'une apparait, on cesse d'attendre au lieu de consommer tout
    /// le delai pour rien.
    /// </summary>
    public List<string> ErrorPatterns { get; set; } = [];

    /// <summary>Lignes ecartees avant evaluation (erreurs connues et benignes).</summary>
    public List<string> IgnorePatterns { get; set; } = [];

    /// <summary>Delai laisse au service Windows pour atteindre Running.</summary>
    public int ServiceRunningTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Delai laisse a l'application pour ecrire son marqueur. Genereux par
    /// defaut : un delai trop court transforme un demarrage lent mais normal
    /// en faux incident, et pousse a relancer un composant qui chargeait.
    /// </summary>
    public int LogReadyTimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// Delai d'arret propre. Un composant qui vide ses files ActiveMQ ou
    /// ecrit KahaDB est occupe, pas bloque.
    /// </summary>
    public int StopTimeoutSeconds { get; set; } = 600;

    public int PollIntervalSeconds { get; set; } = 10;

    public int ProgressEverySeconds { get; set; } = 60;

    /// <summary>
    /// Duree d'observation apres le marqueur, pour detecter une erreur
    /// survenant juste apres l'initialisation. 0 desactive.
    /// </summary>
    public int PostReadySettleSeconds { get; set; }

    /// <summary>
    /// Vrai si la preuve par le journal est exploitable. Faux, l'orchestrateur
    /// ne pourra conclure qu'a "A confirmer" et devra s'en remettre a
    /// l'operateur - ce qui est le comportement voulu, pas un defaut.
    /// </summary>
    public bool IsProvable => !string.IsNullOrWhiteSpace(LogPath) && ReadyPatterns.Count > 0;
}

/// <summary>
/// Dependance entre deux composants (FR-002). Le graphe sert a determiner
/// l'ordre des workflows, verifier les prerequis, analyser l'impact d'une
/// action unitaire et empecher une sequence incompatible - typiquement
/// demarrer XPS avant que le Bridge soit confirme operationnel (FR-044).
/// </summary>
public class ComponentDependency : AuditableEntity
{
    public Guid ComponentId { get; set; }
    public N4Component? Component { get; set; }

    public Guid DependsOnComponentId { get; set; }
    public N4Component? DependsOnComponent { get; set; }

    public DependencyKind Kind { get; set; } = DependencyKind.RequisAuDemarrage;

    public string? Notes { get; set; }
}

/// <summary>
/// Controle de sante attache a un composant. Plusieurs controles de nature
/// differente concourent a un meme etat consolide : c'est le principe de
/// preuve technique du cahier des charges.
/// </summary>
public class ComponentHealthCheck : AuditableEntity
{
    public Guid ComponentId { get; set; }
    public N4Component? Component { get; set; }

    public HealthCheckKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Cible du controle : nom de service, port, URL, chemin, selon la nature.</summary>
    public string? Target { get; set; }

    /// <summary>Valeur attendue ou seuil, interprete selon la nature du controle.</summary>
    public string? ExpectedValue { get; set; }

    /// <summary>
    /// Un controle bloquant en echec interdit l'execution, sauf contournement
    /// explicitement prevu, autorise et audite (FR-012).
    /// </summary>
    public bool IsBlocking { get; set; }

    public bool IsEnabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 30;
}
