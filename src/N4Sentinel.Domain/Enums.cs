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
    Desactive = 4,
    Revoque = 5
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
    ArretForce = 10,

    /// <summary>FR-094 : Approbation d'un objet en revue (SOP, document, workflow).</summary>
    Approbation = 11,

    /// <summary>FR-094 : Révocation d'un objet précédemment publié (SOP, workflow, signature).</summary>
    Revocation = 12,

    /// <summary>P6 : Création d'une programmation récurrente (planification maintenance N4).</summary>
    PlanificationCreee = 13,

    /// <summary>P6 : Modification d'une programmation existante (changement horaire, motif, suspension…).</summary>
    PlanificationModifiee = 14,

    /// <summary>P6 : Déclenchement effectif d'une exécution lancée par le planificateur.</summary>
    PlanificationDeclenchee = 15,

    /// <summary>P6 : Mise en pause ou reprise d'une programmation récurrente.</summary>
    PlanificationSuspendue = 16,

    /// <summary>S3-09 : Création d'un ticket d'incident (Ticketing ou diagnostic auto P1).</summary>
    TicketCree = 17,

    /// <summary>S3-09 : Changement de statut, assignation, priorité ou ajout de commentaire.</summary>
    TicketModifie = 18,

    /// <summary>S3-09 : Clôture d'un ticket (Résolu / Fermé) avec motif.</summary>
    TicketCloture = 19,

    /// <summary>S3-09 : Ouverture d'une session JMX contre un broker ActiveMQ ou un serveur applicatif.</summary>
    JmxSessionOuverte = 20,

    /// <summary>S3-09 : Appel d'un MBean JMX (purge queue, reset, etc.).</summary>
    JmxMBeanAppel = 21,

    /// <summary>S3-09 : Échec d'une opération JMX (timeout, endpoint injoignable, credential erroné).</summary>
    JmxEchec = 22,

    /// <summary>PB-25 : Modification / poussée ticket hors de Sentinel vers outil DSI.</summary>
    TicketSynchroExterne = 23,

    /// <summary>PB-25 : Échec push d'un ticket vers l'outil externe (ServiceNow/Jira/GLPI).</summary>
    TicketSynchroExterneEchec = 24,

    /// <summary>PB-23 · Validation Palier 2 du connecteur technique d'une étape destructive (autorisé).</summary>
    Palier2ConnecteurValide = 25,

    /// <summary>PB-23 · Échec : connecteur technique PALIER 2 interdit sur cet environnement.</summary>
    Palier2ConnecteurInterdit = 26
}

// ---------------------------------------------------------------------------
// S3-09 — Ticketing & Intégration JMX ActiveMQ
// ---------------------------------------------------------------------------

/// <summary>
/// Cycle de vie d'un ticket d'incident ouvert par Sentinel,
/// soit par diagnostic automatique P1, soit par l'opérateur.
/// </summary>
public enum TicketStatus : byte
{
    Nouveau = 0,
    EnCours = 1,
    EnAttenteFournisseur = 2,
    Resolu = 3,
    Ferme = 4,
    Annule = 5
}

public enum TicketPriority : byte
{
    P1Critique = 1,  // Arrêt service, impact client
    P2Majeur = 2,
    P3Normal = 3,
    P4Mineur = 4,
    P5Information = 5
}

public enum TicketSeverity : byte
{
    Critique = 1,
    Majeur = 2,
    Mineur = 3,
    Informatif = 4
}

/// <summary>
/// Origine du ticket. Permet de distinguer les tickets automatiques
/// (diagnostic, APR) des tickets saisis à la main par l'équipe exploitation.
/// </summary>
public enum TicketSource : byte
{
    Manuel = 0,
    DiagnosticP1 = 1,
    ActionPreventive = 2,
    SupervisionComposant = 3,
    EdiEchecRecurrent = 4
}

/// <summary>
/// Type d'exposé JMX — typiquement : Broker ActiveMQ (file d'attentes Bridge)
/// ou serveur applicatif (N4 Center / XPS / ECN4 / WEB).
/// </summary>
public enum JmxEndpointType : byte
{
    ActiveMQBroker = 0,
    N4Center = 1,
    N4Web = 2,
    N4Xps = 3,
    N4Ecn4 = 4,
    Autre = 99
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

/// <summary>
/// MERISE — Statut d'un service Windows associé à un composant N4
/// (ENT_SVCASSOC / ComponentAssociatedServices).
/// </summary>
public enum AssociatedServiceStatus
{
    Inconnu = 0,
    Arrete = 1,
    EnCoursDeDemarrage = 2,
    EnMarche = 3,
    EnCoursDArret = 4,
    EnPanne = 5,
    Maintenance = 6
}

/// <summary>
/// MERISE — Catégorie d'un contrôle préalable Workflow (ENT_VRF / WorkflowPreChecks).
/// Chaque contrôle appartient à une des 10 familles TOP causes P1 Navis.
/// </summary>
public enum WorkflowPreCheckCategory
{
    HorlogesNtp = 0,
    GestionDonneesEtIndex = 1,
    ConnectiviteReseau = 2,
    EtatAmqKahaDb = 3,
    EtatFilesBridge = 4,
    OrdreArretDemarrage = 5,
    SnapshotVirtuel = 6,
    CompatibiliteTiers = 7,
    VerrouillageEnvironnement = 8,
    Autre = 99
}

/// <summary>FR-090 : Niveau de qualité global attribué à un SOP d'après son rapport qualité.</summary>
public enum SopQualityGrade
{
    /// <summary>Au moins un axe est en dessous du niveau minimum publiable (score global &lt; 50).</summary>
    Insuffisant = 0,
    /// <summary>Passable, peut être validé mais nécessite encore des retouches (50–69).</summary>
    Passable = 1,
    /// <summary>Bon, standard attendu pour la publication Actif (70–89).</summary>
    Bon = 2,
    /// <summary>Excellence, SOP de référence : 90+ et TOUS les axes ≥ Bon.</summary>
    Excellence = 3
}

/// <summary>
/// MERISE — Statut d'une Action Préventive Recommandée
/// (ENT_APR / PreventiveRecommendedActions).
/// </summary>
public enum AprStatus
{
    Detectee = 0,
    Proposee = 1,
    Programmee = 2,
    EnCoursDExecution = 3,
    Executee = 4,
    Ignoree = 5,
    Resolue = 6,
    Expiree = 7
}

/// <summary>
/// MERISE — Niveau de compétence requis / ayant résolu un incident capitalisé
/// (ENT_INC / CapitalizedIncidents).
/// </summary>
public enum ResolutionSkillLevel
{
    N1Support = 0,
    N2Specialiste = 1,
    N3ExpertInterne = 2,
    ExpertNavisEditeur = 3
}

/// <summary>
/// MERISE — Mode d'agrégation pour une métrique business
/// (ENT_METBUS / BusinessSuccessMetrics).
/// </summary>
public enum BusinessMetricAggregation
{
    Moyenne = 0,
    Somme = 1,
    Mediane = 2,
    Pourcentage = 3
}

/// <summary>P6 — Récurrence d'une planification de maintenance N4.</summary>
public enum ScheduleKind
{
    /// <summary>Tous les jours à l'heure indiquée (y compris week-ends).</summary>
    Quotidien = 0,

    /// <summary>Jours ouvrés classiques : lundi → vendredi hors jours fériés.</summary>
    JoursOuvres = 1,

    /// <summary>Hebdomadaire, jours explicitement sélectionnés via WeeklyDayOfWeekMask.</summary>
    HebdoJoursChoisis = 2,

    /// <summary>Mensuel, jours numériques listés (1..31) via MonthlyDays.</summary>
    MensuelJoursPrecis = 3,

    /// <summary>Expression cron standard (réservé à des planifications avancées).</summary>
    CronExpression = 4
}

/// <summary>P6 — État d'une planification.</summary>
public enum ScheduleStatus
{
    Actif = 0,
    Suspendu = 1,
    Expire = 2
}

/// <summary>P6 — Origine du lancement d'une exécution Workflow (complète la chaîne de traçabilité).</summary>
public enum ExecutionTriggeredBy
{
    /// <summary>Déclenché par un humain depuis un écran (par défaut pour les versions avant P6).</summary>
    Manuel = 0,

    /// <summary>Déclenché automatiquement par le planificateur récurrent P6.</summary>
    Planification = 1,

    /// <summary>Reprise après crash/recyclage applicatif (NFR-002).</summary>
    RepriseApresCrash = 2,

    /// <summary>Lancé automatiquement depuis une APR validée (P2 Merise).</summary>
    APRAutomatique = 3
}
