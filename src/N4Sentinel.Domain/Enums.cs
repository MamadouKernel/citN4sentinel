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
/// Catégorisation d'un dossier partagé supervisé (FR-059A). Un même
/// <see cref="ComponentRole.DossierPartage"/> peut recevoir plusieurs
/// catégories réelles selon le site (configuration, échange ActiveMQ,
/// intégrations EDI, archives, erreurs) — la catégorie n'est jamais déduite
/// du rôle du composant, elle est déclarée.
/// </summary>
public enum SharedFolderCategory
{
    ConfigurationN4 = 0,
    ActiveMqKahaDb = 1,
    EchangeEdi = 2,
    Archives = 3,
    DossiersErreur = 4,
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
    Contournement = 9,

    /// <summary>Arrêt forcé d'une étape bloquée en StopPending (FR-029B). Jamais automatique.</summary>
    ArretForce = 10
}

/// <summary>
/// FR-046/047 : choix explicite exige avant toute action mutative (arret,
/// redemarrage) sur le Center primaire. Ni l'un ni l'autre n'est un defaut :
/// c'est precisement le choix qu'un service Windows "Running" ne permet pas
/// de deduire tout seul (FR-032/033).
/// </summary>
public enum CenterContinuityChoice
{
    /// <summary>Le Center reste le noeud actif : le Standby ne doit rien prendre en charge.</summary>
    ResterActif = 0,

    /// <summary>Le role actif doit passer au Standby pendant l'operation.</summary>
    Basculer = 1
}
