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
    /// Déclaré en maintenance par un administrateur (FR-052) : la supervision
    /// affiche l'état Maintenance sans relever d'échec, le temps d'une
    /// intervention planifiée. Distinct de ControlMode : un composant reste
    /// pilotable, on suspend seulement le jugement porté sur son état.
    /// </summary>
    public bool MaintenanceMode { get; set; }
    public string? MaintenanceNote { get; set; }

    /// <summary>FR-052 : date et heure de fin de la fenêtre de maintenance planifiée. Null = durée indéterminée.</summary>
    public DateTimeOffset? MaintenanceUntil { get; set; }

    /// <summary>FR-052 : motif explicite de la mise en maintenance (requis pour l'audit — un composant sans raison ne doit pas être mis en maintenance silencieusement).</summary>
    public string? MaintenanceReason { get; set; }

    /// <summary>FR-052 : compte ayant déclaré la maintenance — tracé pour l'audit.</summary>
    public string? MaintenanceBy { get; set; }

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

    /// <summary>
    /// Déclaration de dossier partagé supervisé (FR-059A/B/C). Non renseignée
    /// pour tout composant qui n'en est pas un.
    /// </summary>
    public SharedFolderProfile SharedFolder { get; set; } = new();

    public ICollection<ComponentDependency> Dependencies { get; set; } = [];

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
    ///
    /// EMPLACEMENTS DE REFERENCE, d'apres le guide Kaleris 3.8.25
    /// « Setup, Maintenance and System Diagnostics », section 1.11.28 :
    ///
    ///   Noeuds N4        C:\ProgramData\Navis\node{n}\logs\navis-apex.log
    ///                    (Linux : /opt/navis/configuration/node{n}/logs/)
    ///   Bridge daemon    C:\ProgramData\Navis\bridged\logs\
    ///   XPS              C:\ProgramData\Navis\xps\log\
    ///
    /// Le dossier du Bridge s'ecrit « bridged », avec un d final : c'est le nom
    /// du daemon, pas celui du composant. La faute de frappe est frequente et
    /// se traduit par un composant qui reste indefiniment « a confirmer ».
    ///
    /// Cas particulier : si XPS echoue AVANT d'avoir charge sa configuration de
    /// journalisation, il ecrit dans C:\Program Files\xps\. Un XPS qui ne
    /// produit rien a l'emplacement habituel doit donc etre cherche la.
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

    /// <summary>
    /// FR-032/033 : marqueurs distinguant un Center/Standby dont le SERVICE a
    /// demarre d'un Center/Standby qui detient REELLEMENT le role actif. Un
    /// service Windows "Running" ne le prouve pas plus qu'il ne prouve
    /// l'initialisation applicative : sur un cluster N4, le role actif peut se
    /// trouver sur l'un ou l'autre noeud independamment de qui est demarre en
    /// premier. Reserve aux composants de role CenterNode/StandbyCenterNode ;
    /// non renseigne, l'etat du role reste "a confirmer", jamais suppose.
    /// </summary>
    public List<string> ActiveRolePatterns { get; set; } = [];

    /// <summary>
    /// FR-056 : marqueurs d'un échange N4-XPS normal, réservés aux
    /// composants Bridge/XPS. Même principe que les autres marqueurs — non
    /// renseigné, l'état de synchronisation reste "à confirmer", jamais
    /// supposé sain par défaut.
    /// </summary>
    public List<string> SyncPatterns { get; set; } = [];

    /// <summary>
    /// Délai sans confirmation au-delà duquel la synchronisation est jugée
    /// en retard. Le tableau des règles de diagnostic du cahier des charges
    /// cite des symptômes (files qui augmentent, SocketTimeoutException)
    /// qui se manifestent en quelques minutes, pas en heures.
    /// </summary>
    public int SyncDelayThresholdMinutes { get; set; } = 15;

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
/// Déclaration d'un dossier partagé supervisé (FR-059A/B/C).
///
/// RootPath VIDE = « ce composant n'est pas un dossier partagé supervisé » :
/// c'est le cas par défaut, y compris pour un composant de rôle
/// <see cref="ComponentRole.DossierPartage"/> pas encore configuré.
///
/// La classification par sous-dossier (en attente/consommé/bloqué/erreur)
/// N'EST PAS DÉDUITE : aucune convention de nommage n'est documentée par
/// l'éditeur au-delà du dossier « amq » pour ActiveMQ/KahaDB. Un sous-dossier
/// non déclaré reste "non classifié" dans le relevé plutôt que supposé vide.
/// </summary>
public class SharedFolderProfile
{
    /// <summary>Chemin racine, tel que vu par le serveur qui exécute les contrôles (UNC ou local).</summary>
    public string? RootPath { get; set; }

    public SharedFolderCategory Category { get; set; } = SharedFolderCategory.Autre;

    public string? PendingSubfolder { get; set; }
    public string? ConsumedSubfolder { get; set; }
    public string? BlockedSubfolder { get; set; }
    public string? ErrorSubfolder { get; set; }

    /// <summary>Ancienneté au-delà de laquelle un fichier en attente est signalé (FR-059B).</summary>
    public int? MaxPendingAgeHours { get; set; }

    /// <summary>FR-059B : temps de réponse du test d'écriture au-delà duquel une latence est signalée. Null = non surveillé.</summary>
    public int? MaxWriteLatencyMs { get; set; }

    /// <summary>FR-059B : vitesse de croissance au-delà de laquelle elle est signalée comme anormale. Null = non surveillé.</summary>
    public long? MaxGrowthBytesPerHour { get; set; }

    /// <summary>
    /// FR-059H : expression régulière optionnelle, avec groupes nommés
    /// <c>partner</c> et/ou <c>type</c>, pour extraire le partenaire et le
    /// type de message d'un nom de fichier EDI. Non renseignée, ces deux
    /// informations restent non classifiées — aucune convention de nommage
    /// des partenaires CIT n'étant documentée, rien n'est deviné.
    /// </summary>
    public string? EdiFileNamingPattern { get; set; }

    /// <summary>
    /// FR-059I : durée au-delà de laquelle l'absence de toute intégration
    /// réussie sur ce dossier devient une alerte.
    /// </summary>
    public int? MaxHoursSinceLastIntegration { get; set; }

    /// <summary>
    /// FR-059G/§3.18 : dernière sauvegarde connue de ce dossier partagé —
    /// DÉCLARÉE par un opérateur, jamais déduite. La sauvegarde d'un dossier
    /// partagé se fait hors application (script, copie, outil de sauvegarde
    /// du site) ; ce champ n'enregistre qu'une attestation humaine, sur le
    /// même principe que <see cref="ExternalActionDeclaration"/>.
    /// </summary>
    public DateTimeOffset? LastBackupAt { get; set; }
    public string? LastBackupBy { get; set; }
    public string? LastBackupNote { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(RootPath);
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
