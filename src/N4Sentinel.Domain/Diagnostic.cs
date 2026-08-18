namespace N4Sentinel.Domain;

/// <summary>
/// Verdict d'une session de diagnostic (FR-074, recette AC-09).
///
/// QUATRE VALEURS, PARCE QUE TROIS NE SUFFISENT PAS. La distinction qui compte
/// est celle entre « je n'ai rien trouvé » et « tout va bien » : ce sont deux
/// affirmations totalement différentes, et les confondre est la façon la plus
/// sûre de faire perdre une heure à quelqu'un qui cherche une panne réelle.
/// </summary>
public enum DiagnosticVerdict
{
    /// <summary>
    /// Une signature connue a été reconnue et sa cause est documentée.
    /// C'est le seul cas où l'application nomme une cause.
    /// </summary>
    CauseCaracterisee = 0,

    /// <summary>
    /// Des anomalies convergent vers un domaine, sans qu'aucune signature
    /// connue ne l'établisse. On propose une piste, on ne conclut pas.
    /// </summary>
    PisteSerieuse = 1,

    /// <summary>
    /// Des anomalies ont été relevées, mais elles ne dessinent aucune cause.
    /// Dire « anomalies sans cause identifiée » est honnête ; inventer un lien
    /// entre elles ne le serait pas.
    /// </summary>
    AnomaliesSansCause = 2,

    /// <summary>
    /// Rien n'a été trouvé DANS CE QUI A ÉTÉ ANALYSÉ, et ce qui devait être lu
    /// l'a bien été (aucune source en échec). Ce n'est PAS un certificat de
    /// bonne santé : la panne peut être hors de la fenêtre examinée.
    /// FR-069 : « Aucune anomalie détectée sur le périmètre analysé ».
    /// </summary>
    RienDeConcluant = 3,

    /// <summary>
    /// FR-069 : « Cause confirmée » — au-delà de CauseCaracterisee, réservé au
    /// cas exceptionnel d'une signature de poids maximal, seule concluante,
    /// sans aucune autre hypothèse concurrente. Distinct de CauseCaracterisee
    /// (« très probable ») pour ne jamais gonfler une conviction ordinaire au
    /// rang de certitude.
    /// </summary>
    CauseConfirmee = 4,

    /// <summary>
    /// FR-069 : « Informations insuffisantes » — distinct de RienDeConcluant :
    /// ici, ce qui devait être lu n'a PAS pu l'être (source en échec, aucune
    /// source lue) ; on ne sait pas si un signal existait, contrairement à
    /// RienDeConcluant où l'analyse a bien eu lieu et n'a rien trouvé.
    /// </summary>
    InformationsInsuffisantes = 5,

    /// <summary>
    /// FR-069 : verdict PlusieursCausesPossibles. Des hypothèses concurrentes 
    /// ont une confiance trop proche pour en désigner une seule comme certaine.
    /// </summary>
    PlusieursCausesPossibles = 6
}

/// <summary>
/// Domaine technique d'une anomalie. Sert à regrouper les constats : trois
/// erreurs de connexion à la base valent mieux dites ensemble que dispersées
/// parmi quarante lignes.
/// </summary>
public enum DiagnosticDomain
{
    BaseDeDonnees = 0,
    Reseau = 1,
    Memoire = 2,
    Configuration = 3,
    Licence = 4,
    Cluster = 5,
    Stockage = 6,
    Integration = 7,
    Securite = 8,
    Horloge = 9,
    Applicatif = 10,

    // FR-062 : domaines propres à l'écosystème N4, ajoutés sans toucher aux
    // valeurs existantes (déjà citées par des signatures et des tests).
    Systeme = 11,
    Services = 12,
    N4Cluster = 13,
    CenterStandby = 14,
    ActiveMqKahaDb = 15,
    BridgeXps = 16,
    Ecn4Ecn4Web = 17,
    SharedFolders = 18,
    EdiInterfaces = 19,

    Indetermine = 99
}

public enum SignatureSeverity
{
    Information = 0,
    Avertissement = 1,
    Erreur = 2,
    Critique = 3
}

/// <summary>Provenance d'une signature : documentation éditeur, ou relevé du site.</summary>
public enum SignatureOrigin
{
    /// <summary>Tirée de la documentation Navis/Kaleris. Ne se modifie qu'en connaissance de cause.</summary>
    Editeur = 0,

    /// <summary>Ajoutée par l'exploitation du site à partir de son propre vécu.</summary>
    Site = 1
}

/// <summary>Comment le journal est arrivé jusqu'à l'application.</summary>
public enum LogOriginKind
{
    /// <summary>Lu directement sur le serveur, via le connecteur.</summary>
    CollecteCiblee = 0,

    /// <summary>Déposé par un opérateur, typiquement quand le serveur est inaccessible.</summary>
    ImportManuel = 1
}

/// <summary>
/// Signature d'anomalie, administrable (FR-075, FR-065).
///
/// Le catalogue livré est amorcé de la documentation éditeur, mais il est
/// modifiable : chaque site voit passer des erreurs que personne n'avait
/// prévues, et un catalogue figé cesserait d'être utile au bout de six mois.
/// </summary>
public class DiagnosticSignature : AuditableEntity
{
    /// <summary>Identifiant stable, cité dans les constats et les rapports.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Expression régulière recherchée dans les lignes de journal.</summary>
    public string Pattern { get; set; } = string.Empty;

    public DiagnosticDomain Domain { get; set; } = DiagnosticDomain.Indetermine;
    public SignatureSeverity Severity { get; set; } = SignatureSeverity.Erreur;
    public SignatureOrigin Origin { get; set; } = SignatureOrigin.Site;

    /// <summary>
    /// Ce que cette ligne signifie réellement. C'est la valeur ajoutée du
    /// catalogue : une pile d'exception ne dit rien à qui ne la connaît pas.
    /// </summary>
    public string? Meaning { get; set; }

    /// <summary>Ce qu'il convient de faire. Un conseil, jamais une action automatique.</summary>
    public string? Remediation { get; set; }

    /// <summary>Référence documentaire, pour que le conseil soit vérifiable.</summary>
    public string? DocumentReference { get; set; }

    /// <summary>
    /// Ce qui contredirait cette cause (FR-063). Une signature ne prouve rien
    /// seule ; documenter ce qui l'infirmerait évite de la traiter comme un
    /// verdict acquis dès qu'elle apparaît dans un journal.
    /// </summary>
    public string? CounterEvidence { get; set; }

    /// <summary>
    /// Incrémentée à chaque modification (FR-065) : une signature livrée par
    /// l'éditeur puis corrigée sur site n'est plus la même version que celle
    /// du guide cité en <see cref="DocumentReference"/>.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Poids dans le calcul de confiance, de 1 à 100. Une signature très
    /// spécifique emporte la conviction ; une signature générique ne devrait
    /// jamais suffire à nommer une cause.
    /// </summary>
    public int ConfidenceWeight { get; set; } = 50;

    /// <summary>Rôle de composant concerné. Null = toutes natures.</summary>
    public ComponentRole? AppliesToRole { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// FR-065 : statut de validation de la règle. Seules Validé/Actif
    /// participent à l'évaluation (<see cref="Diagnostic.SignatureCatalogue.GetActiveAsync"/>) —
    /// une règle en Brouillon reste visible et testable sans influencer
    /// encore un diagnostic réel.
    /// </summary>
    public LifecycleStatus ValidationStatus { get; set; } = LifecycleStatus.Valide;

    /// <summary>
    /// Une signature ne nomme une cause que si elle est assez spécifique.
    /// En dessous, elle contribue au faisceau sans le conclure.
    /// </summary>
    public bool EstConcluante => ConfidenceWeight >= 70
                                 && Severity >= SignatureSeverity.Erreur
                                 && !string.IsNullOrWhiteSpace(Meaning);
}

/// <summary>
/// FR-065 : Règle de corrélation multi-signaux.
/// Permet de lier plusieurs signatures ou événements distincts dans une fenêtre de temps 
/// pour formuler une hypothèse plus précise qu'un signal isolé.
/// </summary>
public class CorrelationRule : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DiagnosticDomain Domain { get; set; }
    
    /// <summary>La conclusion ou l'hypothèse soulevée si toutes les conditions sont remplies.</summary>
    public string HypothesisStatement { get; set; } = string.Empty;
    
    /// <summary>Niveau de confiance attribué à l'hypothèse (0-100).</summary>
    public int Confidence { get; set; }
    
    /// <summary>Recommandation d'action si la règle est vérifiée.</summary>
    public string? Recommendation { get; set; }
    
    /// <summary>Fenêtre de temps en secondes dans laquelle tous les signaux doivent se produire.</summary>
    public int TimeWindowSeconds { get; set; }

    /// <summary>Les conditions requises pour déclencher cette règle.</summary>
    public ICollection<CorrelationCondition> Conditions { get; set; } = [];
}

/// <summary>
/// FR-065 : Condition individuelle au sein d'une règle de corrélation.
/// </summary>
public class CorrelationCondition : AuditableEntity
{
    public Guid RuleId { get; set; }
    public CorrelationRule? Rule { get; set; }

    /// <summary>Identifiant de la signature (DiagnosticSignature.Id) ou code d'événement attendu.</summary>
    public string SignalSourceId { get; set; } = string.Empty;

    /// <summary>Description de ce qui est recherché par cette condition.</summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>Vrai si l'absence du signal (dans la fenêtre de temps) est la condition de déclenchement.</summary>
    public bool IsNegation { get; set; }
}

/// <summary>
/// Phase du cycle de traitement d'un incident (§3.10.1).
///
/// LE CYCLE N'EST PAS STRICTEMENT LINÉAIRE : le texte du cahier des charges
/// autorise explicitement de revenir à la collecte ou au diagnostic quand une
/// vérification apporte un élément nouveau ou invalide une hypothèse. La
/// valeur de cet enum n'est donc qu'un ÉTAT COURANT ; l'historique complet des
/// passages vit dans <see cref="DiagnosticPhaseTransition"/>, jamais écrasé.
/// </summary>
public enum DiagnosticPhase
{
    DetectionEtEnregistrement = 0,
    QualificationEtCollecte = 1,
    Securisation = 2,
    DiagnosticEtCorrelation = 3,
    ChoixDuPlanDAction = 4,
    ValidationEtExecution = 5,
    RemiseEnServiceEtVerification = 6,
    ClotureEtCapitalisation = 7
}

/// <summary>
/// FR-066 : les 4 façons de comparer un incident à une référence, telles que
/// littéralement énumérées par le cahier des charges (« une période saine
/// validée ; une exécution précédente réussie ; les valeurs habituelles du
/// même composant ; un autre nœud comparable du même environnement »).
/// </summary>
public enum ReferenceKind
{
    /// <summary>Une autre session de diagnostic, marquée saine par un opérateur habilité.</summary>
    PeriodeSaine = 0,

    /// <summary>Une exécution d'opération antérieure, terminée avec succès (jamais « avec avertissements »).</summary>
    ExecutionReussie = 1,

    /// <summary>L'historique de signaux de supervision du même composant, hors de la fenêtre de l'incident.</summary>
    ValeursHabituellesComposant = 2,

    /// <summary>Un autre composant du même rôle, dans le même environnement, sur la même fenêtre.</summary>
    NoeudPair = 3
}

/// <summary>
/// Session de diagnostic : une investigation, ses sources, ses constats et son
/// verdict (FR-062 à FR-069).
/// </summary>
public class DiagnosticSession : AuditableEntity
{
    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }
    public string EnvironmentCode { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Ce que l'on cherche. Sans cela, un diagnostic n'est qu'une lecture.</summary>
    public string? Reason { get; set; }

    public string? TicketReference { get; set; }
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>
    /// Fenêtre d'analyse. Elle borne le verdict : ce qui est dit ne vaut que
    /// pour cette plage, et le rapport doit le rappeler.
    /// </summary>
    public DateTimeOffset? WindowStart { get; set; }
    public DateTimeOffset? WindowEnd { get; set; }

    public DiagnosticVerdict Verdict { get; set; } = DiagnosticVerdict.RienDeConcluant;

    /// <summary>Formulation du verdict, avec ses limites explicitées.</summary>
    public string? VerdictExplanation { get; set; }

    public DateTimeOffset? AnalysedAt { get; set; }

    /// <summary>FR-068 : alerte à l'origine de cette session, si elle a été ouverte automatiquement.</summary>
    public Guid? SourceAlertId { get; set; }

    /// <summary>
    /// FR-066 : session marquée comme référence saine par un utilisateur
    /// habilité — jamais par déduction automatique. C'est une affirmation
    /// humaine ("j'ai vérifié, ceci était normal"), pas une propriété que
    /// l'application pourrait établir seule.
    /// </summary>
    public bool IsReferenceBaseline { get; set; }

    /// <summary>FR-066 : session de référence choisie pour la comparaison, s'il y en a une (mode PeriodeSaine).</summary>
    public Guid? ReferenceSessionId { get; set; }
    public DiagnosticSession? ReferenceSession { get; set; }

    /// <summary>FR-066 : mode de comparaison actif pour cette session. Un seul à la fois, jamais combinés implicitement.</summary>
    public ReferenceKind ReferenceKind { get; set; } = ReferenceKind.PeriodeSaine;

    /// <summary>FR-066 : exécution antérieure choisie comme référence (mode ExecutionReussie) — jamais déduite.</summary>
    public Guid? ReferenceExecutionId { get; set; }

    /// <summary>
    /// FR-066 : composant de référence. Son sens dépend de <see cref="ReferenceKind"/> —
    /// le composant dont on regarde le propre historique (ValeursHabituellesComposant),
    /// ou le nœud pair choisi (NoeudPair). Jamais utilisé pour les deux autres modes.
    /// </summary>
    public Guid? ReferenceComponentId { get; set; }

    /// <summary>
    /// Phase courante du cycle §3.10.1. Le détail des passages — qui, quand,
    /// pourquoi — vit dans <see cref="PhaseTransitions"/> ; ce champ n'est
    /// qu'un raccourci de lecture sur la dernière entrée.
    /// </summary>
    public DiagnosticPhase Phase { get; set; } = DiagnosticPhase.DetectionEtEnregistrement;

    public ICollection<LogSource> Sources { get; set; } = [];
    public ICollection<LogFinding> Findings { get; set; } = [];
    public ICollection<DiagnosticHypothesis> Hypotheses { get; set; } = [];
    public ICollection<DiagnosticPhaseTransition> PhaseTransitions { get; set; } = [];
    public ICollection<ExternalActionDeclaration> ExternalActions { get; set; } = [];

    public bool HasBeenAnalysed => AnalysedAt is not null;

    /// <summary>
    /// §3.10.1 : à qui/quoi ce diagnostic a été escaladé, quand son verdict
    /// n'est pas concluant. Renseigné avant de pouvoir clôturer une session
    /// inconcluante — jamais déduit, une escalade est une décision humaine.
    /// </summary>
    public string? EscalatedTo { get; set; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public string? EscalatedBy { get; set; }

    /// <summary>Vrai pour tout verdict qui ne nomme pas de cause établie (§3.10.1, « attente/escalade »).</summary>
    public bool VerdictEstInconcluant => Verdict is DiagnosticVerdict.PisteSerieuse
        or DiagnosticVerdict.AnomaliesSansCause or DiagnosticVerdict.RienDeConcluant
        or DiagnosticVerdict.InformationsInsuffisantes;
}

/// <summary>
/// Horodatage d'un passage de phase (§3.10.1). Une nouvelle entrée s'ajoute à
/// chaque transition, y compris un retour vers une phase déjà visitée — ce
/// n'est jamais une erreur à corriger, c'est une reprise à tracer.
/// </summary>
public class DiagnosticPhaseTransition : AuditableEntity
{
    public Guid SessionId { get; set; }
    public DiagnosticSession? Session { get; set; }

    public DiagnosticPhase Phase { get; set; }
    public string EnteredBy { get; set; } = string.Empty;
    public DateTimeOffset EnteredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Ce qui justifie ce passage. Obligatoire pour
    /// <see cref="DiagnosticPhase.ClotureEtCapitalisation"/> : la disparition
    /// du symptôme ne suffit pas à clôturer un incident, il faut dire ce qui a
    /// été vérifié (§3.10.1, principes).
    /// </summary>
    public string? Note { get; set; }
}

/// <summary>
/// §3.18 : taxonomie exacte d'un échec de collecte de signal/journal. Ne
/// jamais laisser en texte libre seul — un texte libre ne peut pas être
/// filtré, compté, ni distingué automatiquement d'une absence d'anomalie.
/// </summary>
public enum LogCollectionFailureReason
{
    AccesRefuse = 0,
    ConnecteurIndisponible = 1,
    Timeout = 2,
    SourceAbsente = 3,
    FormatNonReconnu = 4,
    ControleNonConfigure = 5
}

/// <summary>Un journal versé à une session (FR-070, FR-079B).</summary>
public class LogSource : AuditableEntity
{
    public Guid SessionId { get; set; }
    public DiagnosticSession? Session { get; set; }

    public Guid? ComponentId { get; set; }
    public string? ComponentName { get; set; }
    public ComponentRole? ComponentRole { get; set; }
    public string? HostName { get; set; }

    /// <summary>
    /// FR-071 : vrai si le composant a été deviné (nom de fichier ou
    /// contenu) plutôt que désigné par l'opérateur — une collecte ciblée ou
    /// un import avec composant choisi n'a jamais ce champ à vrai. Une
    /// identification automatique reste une hypothèse, affichée comme telle.
    /// </summary>
    public bool ComponentAutoDetected { get; set; }

    /// <summary>FR-071 : version repérée dans le contenu, si un motif connu l'indique.</summary>
    public string? DetectedVersion { get; set; }

    /// <summary>FR-071 : nature du journal repérée par son format (ex. « log4j/Apex », « IIS », « Windows Event »).</summary>
    public string? DetectedLogType { get; set; }

    /// <summary>FR-071 : fuseau horaire repéré dans les horodatages du journal (ex. « UTC+00:00 »), quand détectable.</summary>
    public string? DetectedTimeZone { get; set; }

    /// <summary>
    /// §3.18/FR-077/NFR-008 : identifiant de l'incident ou de l'opération à
    /// l'origine de cette collecte, quand elle en découle une — permet de
    /// filtrer et de relier un journal à ce qui l'a fait collecter, jamais
    /// déduit après coup.
    /// </summary>
    public string? CorrelationId { get; set; }

    public LogOriginKind Origin { get; set; }

    /// <summary>Chemin réellement lu, après résolution d'un éventuel générique.</summary>
    public string? ResolvedPath { get; set; }
    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public int LineCount { get; set; }

    /// <summary>
    /// §3.18/FR-067 : empreinte SHA-256 du contenu MASQUÉ (jamais du brut).
    /// Permet de vérifier qu'un extrait cité dans un paquet d'escalade
    /// correspond bien à ce qui a été collecté, sans conserver le contenu
    /// lui-même.
    /// </summary>
    public string? ContentHash { get; set; }

    // --- Résumé (FR-073) --------------------------------------------------
    /// <summary>Horodatage de la première ligne datée du fichier — pas seulement des anomalies.</summary>
    public DateTimeOffset? EarliestEntryAt { get; set; }
    public DateTimeOffset? LatestEntryAt { get; set; }

    public int InfoCount { get; set; }
    public int WarningCount { get; set; }
    public int ErrorCount { get; set; }

    /// <summary>
    /// FR-061 : écart d'horloge du serveur source, mesuré au moment de la
    /// collecte (collecte ciblée uniquement — non mesurable sur un import
    /// manuel). Les horodatages tirés de ce journal peuvent être décalés
    /// d'autant ; la chronologie multi-sources en tient compte et le signale.
    /// </summary>
    public double? ClockSkewSecondsAtCollection { get; set; }

    /// <summary>
    /// Nombre de secrets masqués avant enregistrement. Affiché à l'opérateur :
    /// il doit savoir que le contenu qu'il consulte a été transformé.
    /// </summary>
    public int MaskedSecretCount { get; set; }

    /// <summary>Vrai si seule la fin du fichier a pu être lue.</summary>
    public bool Truncated { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// §3.18 : classification exacte de l'échec, distincte du message libre
    /// ci-dessus — l'absence d'un signal ne doit jamais être interprétée
    /// comme une absence d'anomalie, encore faut-il pouvoir dire POURQUOI il
    /// manque, filtrable et comptable.
    /// </summary>
    public LogCollectionFailureReason? FailureReason { get; set; }

    public bool Succeeded => Error is null;
}

/// <summary>
/// Constat : une signature reconnue, ou une erreur répétée non cataloguée
/// (FR-072, FR-076, FR-077).
///
/// Les occurrences identiques sont REGROUPÉES. Quarante fois la même exception
/// n'est pas quarante problèmes, c'est un problème vu quarante fois — et la
/// liste devient illisible si on ne le dit pas ainsi.
/// </summary>
public class LogFinding : AuditableEntity
{
    public Guid SessionId { get; set; }
    public DiagnosticSession? Session { get; set; }

    public Guid SourceId { get; set; }
    public LogSource? Source { get; set; }

    /// <summary>Signature reconnue. Null pour une erreur répétée non cataloguée.</summary>
    public Guid? SignatureId { get; set; }
    public string? SignatureCode { get; set; }

    public DiagnosticDomain Domain { get; set; } = DiagnosticDomain.Indetermine;
    public SignatureSeverity Severity { get; set; } = SignatureSeverity.Erreur;

    /// <summary>Nom du constat : celui de la signature, ou le message normalisé.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Ligne représentative, DÉJÀ MASQUÉE.</summary>
    public string SampleLine { get; set; } = string.Empty;

    /// <summary>Lignes encadrant l'occurrence, DÉJÀ MASQUÉES (FR-077).</summary>
    public string? Context { get; set; }

    public int OccurrenceCount { get; set; } = 1;
    public DateTimeOffset? FirstSeenAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Numéro de la première ligne concernée, pour retrouver le passage.</summary>
    public int FirstLineNumber { get; set; }

    public string? Meaning { get; set; }
    public string? Remediation { get; set; }
    public string? DocumentReference { get; set; }

    /// <summary>
    /// Thread, classe et identifiant de transaction relevés sur la ligne
    /// représentative, quand le format du journal les porte (FR-072). Null
    /// quand le motif n'a pas été reconnu — jamais deviné.
    /// </summary>
    public string? ThreadName { get; set; }
    public string? LoggerClass { get; set; }
    public string? TransactionId { get; set; }

    /// <summary>Niveau de log (INFO, WARN, ERROR...) extrait de la ligne (FR-072).</summary>
    public string? Level { get; set; }

    /// <summary>Code d'erreur (ex: ORA-12154, HTTP 500) extrait de la ligne (FR-072).</summary>
    public string? ErrorCode { get; set; }
}

/// <summary>
/// Hypothèse : un domaine, une formulation, un niveau de confiance et les
/// preuves qui la soutiennent (FR-063, FR-064, FR-069).
///
/// UNE HYPOTHÈSE N'EST PAS UN DIAGNOSTIC. Elle est présentée comme telle, avec
/// ce sur quoi elle repose, pour que l'opérateur puisse la contester. Une
/// application qui affirme sans montrer ses preuves finit par être crue à tort,
/// puis par ne plus être crue du tout.
/// </summary>
public class DiagnosticHypothesis : AuditableEntity
{
    public Guid SessionId { get; set; }
    public DiagnosticSession? Session { get; set; }

    public DiagnosticDomain Domain { get; set; }

    /// <summary>Ce qui est supposé, formulé en une phrase.</summary>
    public string Statement { get; set; } = string.Empty;

    /// <summary>De 0 à 100. Au-dessus de 70, l'hypothèse est jugée établie.</summary>
    public int Confidence { get; set; }

    /// <summary>Constats qui la soutiennent, cités par leur intitulé.</summary>
    public string Evidence { get; set; } = string.Empty;

    /// <summary>
    /// FR-063 : ce qui, s'il était observé, contredirait cette hypothèse
    /// plutôt que de la confirmer — jamais laissé vide silencieusement : dit
    /// explicitement quand rien de tel n'a été identifié.
    /// </summary>
    public string? CounterEvidence { get; set; }

    /// <summary>FR-063 : dernière occurrence contribuant aux preuves, pour situer l'hypothèse dans le temps.</summary>
    public DateTimeOffset? EvidenceObservedAt { get; set; }

    /// <summary>FR-063/FR-065 : version de la règle de diagnostic la plus déterminante dans cette hypothèse.</summary>
    public string? RuleVersion { get; set; }

    /// <summary>Ce qu'il conviendrait de vérifier ou de faire. Jamais exécuté automatiquement.</summary>
    public string? Recommendation { get; set; }

    /// <summary>Rang d'affichage : la plus probable en premier.</summary>
    public int Rank { get; set; }

    /// <summary>
    /// FR-065 : le seuil n'est plus figé à 70 — il vient de
    /// <see cref="DiagnosticSettings.HypothesisEstablishedThreshold"/>, administrable.
    /// </summary>
    public bool EstEtablie(int seuil) => Confidence >= seuil;
}
