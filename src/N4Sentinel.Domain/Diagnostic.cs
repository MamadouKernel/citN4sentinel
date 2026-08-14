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
    /// Rien n'a été trouvé DANS CE QUI A ÉTÉ ANALYSÉ. Ce n'est PAS un
    /// certificat de bonne santé : la panne peut être hors de la fenêtre
    /// examinée, dans un journal non collecté, ou ne rien écrire du tout.
    /// </summary>
    RienDeConcluant = 3
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
    /// Poids dans le calcul de confiance, de 1 à 100. Une signature très
    /// spécifique emporte la conviction ; une signature générique ne devrait
    /// jamais suffire à nommer une cause.
    /// </summary>
    public int ConfidenceWeight { get; set; } = 50;

    /// <summary>Rôle de composant concerné. Null = toutes natures.</summary>
    public ComponentRole? AppliesToRole { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Une signature ne nomme une cause que si elle est assez spécifique.
    /// En dessous, elle contribue au faisceau sans le conclure.
    /// </summary>
    public bool EstConcluante => ConfidenceWeight >= 70
                                 && Severity >= SignatureSeverity.Erreur
                                 && !string.IsNullOrWhiteSpace(Meaning);
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

    public ICollection<LogSource> Sources { get; set; } = [];
    public ICollection<LogFinding> Findings { get; set; } = [];
    public ICollection<DiagnosticHypothesis> Hypotheses { get; set; } = [];

    public bool HasBeenAnalysed => AnalysedAt is not null;
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

    public LogOriginKind Origin { get; set; }

    /// <summary>Chemin réellement lu, après résolution d'un éventuel générique.</summary>
    public string? ResolvedPath { get; set; }
    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public int LineCount { get; set; }

    /// <summary>
    /// Nombre de secrets masqués avant enregistrement. Affiché à l'opérateur :
    /// il doit savoir que le contenu qu'il consulte a été transformé.
    /// </summary>
    public int MaskedSecretCount { get; set; }

    /// <summary>Vrai si seule la fin du fichier a pu être lue.</summary>
    public bool Truncated { get; set; }

    public string? Error { get; set; }

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

    /// <summary>Ce qu'il conviendrait de vérifier ou de faire. Jamais exécuté automatiquement.</summary>
    public string? Recommendation { get; set; }

    /// <summary>Rang d'affichage : la plus probable en premier.</summary>
    public int Rank { get; set; }

    public bool EstEtablie => Confidence >= 70;
}
