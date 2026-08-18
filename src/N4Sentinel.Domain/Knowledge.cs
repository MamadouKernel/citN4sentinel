namespace N4Sentinel.Domain;

/// <summary>Nature d'un document de la base documentaire.</summary>
public enum DocumentKind
{
    /// <summary>Documentation Navis/Kaleris. Fait autorité, ne se réécrit pas.</summary>
    GuideEditeur = 0,

    /// <summary>Procédure d'exploitation propre au site.</summary>
    ProcedureInterne = 1,

    /// <summary>Mode opératoire, SOP.</summary>
    ModeOperatoire = 2,

    /// <summary>Compte rendu d'incident, retour d'expérience.</summary>
    RetourExperience = 3,

    Autre = 99
}

/// <summary>
/// Document de référence indexé (FR-080, FR-086).
///
/// UN DOCUMENT NON VALIDÉ NE RÉPOND PAS. Il peut être versé et consulté, mais
/// il est exclu des réponses tant que quelqu'un ne l'a pas validé. Citer un
/// brouillon avec la même assurance qu'une procédure approuvée reviendrait à
/// donner à un texte de travail l'autorité d'une référence — et c'est ainsi
/// qu'une consigne périmée se retrouve appliquée un dimanche matin.
/// </summary>
public class KnowledgeDocument : AuditableEntity
{
    public string Title { get; set; } = string.Empty;

    /// <summary>Référence citable : « Guide d'exploitation 3.8.25 », « SOP-2 ».</summary>
    public string Reference { get; set; } = string.Empty;

    public DocumentKind Kind { get; set; } = DocumentKind.Autre;

    /// <summary>Version du document lui-même, telle que son éditeur la nomme.</summary>
    public string? DocumentVersion { get; set; }

    public LifecycleStatus Status { get; set; } = LifecycleStatus.Brouillon;

    public string? ValidatedBy { get; set; }
    public DateTimeOffset? ValidatedAt { get; set; }

    /// <summary>Fichier d'origine, pour retrouver la source sur pièce.</summary>
    public string? SourceFileName { get; set; }

    public int PageCount { get; set; }
    public int SectionCount { get; set; }

    /// <summary>
    /// Version applicable de N4. Un conseil tiré d'une documentation 3.7 peut
    /// être faux en 3.9 : l'indiquer permet à l'opérateur d'en juger.
    /// </summary>
    public string? AppliesToVersion { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Vrai si le fichier a réellement été analysé par un antivirus au
    /// versement (audit SEC-A6).
    ///
    /// Faux ne veut pas dire « infecté » : cela veut dire QUE PERSONNE N'EN
    /// SAIT RIEN. Sur un serveur sans moteur antivirus, ou quand celui-ci
    /// renvoie une sortie ambiguë, le document était auparavant indexé en
    /// silence. Le fait est désormais conservé, et affiché à qui doit valider
    /// le document — c'est le moment où quelqu'un décide de lui faire
    /// confiance.
    /// </summary>
    public bool AntivirusChecked { get; set; }

    /// <summary>Pourquoi l'analyse n'a pas conclu. Renseigné uniquement dans ce cas.</summary>
    public string? AntivirusNote { get; set; }

    public ICollection<DocumentSection> Sections { get; set; } = [];

    /// <summary>Seul un document validé alimente les réponses (FR-086).</summary>
    public bool IsCitable => Status is LifecycleStatus.Valide or LifecycleStatus.Actif
                             && SectionCount > 0;
}

/// <summary>
/// Fragment citable d'un document : une page, une section (FR-081 à FR-083).
///
/// LE GRAIN EST LA CITATION. Chaque réponse doit pouvoir désigner le document,
/// la section et la page — recette AC-10. Indexer un document entier comme un
/// bloc rendrait cette exigence impossible à tenir.
/// </summary>
public class DocumentSection : AuditableEntity
{
    public Guid DocumentId { get; set; }
    public KnowledgeDocument? Document { get; set; }

    /// <summary>Rang dans le document, pour restituer l'ordre de lecture.</summary>
    public int Ordinal { get; set; }

    /// <summary>Numéro de page dans le document d'origine. 0 si sans pagination.</summary>
    public int PageNumber { get; set; }

    /// <summary>Titre de section, quand il a pu être identifié.</summary>
    public string? Heading { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Contenu normalisé — sans accents ni ponctuation, en minuscules — servant
    /// à la recherche. Le stocker évite de le recalculer à chaque requête, et
    /// permet de retrouver « démarrage » en tapant « demarrage ».
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>Citation courte, telle qu'elle apparaîtra dans une réponse.</summary>
    public string Citation => PageNumber > 0
        ? $"{Document?.Reference ?? "document"}, page {PageNumber}"
          + (Heading is { Length: > 0 } ? $" — {Heading}" : string.Empty)
        : $"{Document?.Reference ?? "document"}"
          + (Heading is { Length: > 0 } ? $" — {Heading}" : string.Empty);
}
