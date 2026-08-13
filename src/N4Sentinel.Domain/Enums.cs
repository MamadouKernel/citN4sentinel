namespace N4Sentinel.Domain;

/// <summary>
/// Cycle de validation impose par le cahier des charges (FR-006).
/// Seules les versions Validee puis Active peuvent servir a une operation
/// en Production.
/// </summary>
public enum LifecycleStatus
{
    Brouillon = 0,
    AValider = 1,
    Valide = 2,
    Actif = 3,
    Desactive = 4
}

/// <summary>Type d'environnement N4 (FR-001).</summary>
public enum EnvironmentKind
{
    Production = 0,
    UAT = 1,
    Test = 2,
    Developpement = 3,
    Formation = 4
}

/// <summary>Niveau de criticite, utilise pour graduer les confirmations exigees.</summary>
public enum CriticalityLevel
{
    Faible = 0,
    Moyenne = 1,
    Elevee = 2,
    Critique = 3
}

/// <summary>
/// Role applicatif d'un composant dans l'ecosysteme N4.
/// Determine le workflow applicable : une action sur XPS, un Cluster Node ou
/// le Center ne se traite jamais comme un redemarrage Windows generique (FR-045).
/// </summary>
public enum ComponentRole
{
    ClusterNode = 0,
    CenterNode = 1,
    StandbyCenterNode = 2,
    BridgeDaemon = 3,
    Xps = 4,
    Ecn4 = 5,
    Ecn4Web = 6,
    BaseDeDonnees = 7,
    ActiveMq = 8,
    DossierPartage = 9,
    InterfaceEdi = 10,
    SystemeExterne = 11,
    JobConditionnel = 12,
    Autre = 99
}

/// <summary>
/// Distingue les composants pilotables de ceux qui sont uniquement observes.
/// Le rattachement d'un systeme a l'ecosysteme N4 ne vaut PAS autorisation
/// de demarrer ou d'arreter ses services (CdC 2.4).
/// </summary>
public enum ControlMode
{
    /// <summary>Supervise et pilotable : demarrage, arret, redemarrage autorises.</summary>
    Pilotable = 0,
    /// <summary>Etat collecte, mais aucune action mutative possible.</summary>
    SuperviseSeulement = 1,
    /// <summary>Declare dans la cartographie pour ses dependances, sans collecte.</summary>
    NonSupervise = 2
}

/// <summary>Nature d'une dependance entre composants.</summary>
public enum DependencyKind
{
    /// <summary>La cible doit etre operationnelle AVANT de demarrer ce composant.</summary>
    RequisAuDemarrage = 0,
    /// <summary>La cible doit rester operationnelle pendant le fonctionnement.</summary>
    RequisEnFonctionnement = 1,
    /// <summary>Dependance connue mais non bloquante.</summary>
    Informative = 2
}

/// <summary>
/// Nature d'un controle de sante. Un composant est confirme operationnel par
/// PLUSIEURS signaux, jamais par le seul statut de son service (CdC, principe
/// "Preuve technique").
/// </summary>
public enum HealthCheckKind
{
    ServiceWindows = 0,
    Processus = 1,
    PortTcp = 2,
    EndpointHttp = 3,
    MarqueurDeLog = 4,
    EspaceDisque = 5,
    EcartHorloge = 6,
    DossierPartage = 7,
    ConnectiviteBase = 8,
    Heartbeat = 9
}

/// <summary>
/// Etat consolide d'un composant (FR-052). Inconnu est un etat de premiere
/// classe : quand les signaux manquent ou se contredisent, la solution
/// l'affiche au lieu de conclure.
/// </summary>
public enum ComponentState
{
    Inconnu = 0,
    Disponible = 1,
    Degrade = 2,
    Indisponible = 3,
    Demarrage = 4,
    Arret = 5,
    Maintenance = 6,
    NonSupervise = 7
}

/// <summary>Resultat d'un controle unitaire (FR-012).</summary>
public enum CheckOutcome
{
    Satisfait = 0,
    Avertissement = 1,
    Bloquant = 2,
    NonApplicable = 3,
    ImpossibleAVerifier = 4
}

/// <summary>Issue d'une action tracee au journal d'audit (SEC-008).</summary>
public enum AuditOutcome
{
    Succes = 0,
    Refuse = 1,
    Echec = 2
}

/// <summary>Categories d'actions auditees.</summary>
public enum AuditAction
{
    Connexion = 0,
    DeconnexionOuEchecAuthentification = 1,
    Creation = 2,
    Modification = 3,
    Suppression = 4,
    ChangementDeStatut = 5,
    TestDeConfiguration = 6,
    TentativeNonAutorisee = 7,
    ExecutionOperation = 8,
    Contournement = 9
}
