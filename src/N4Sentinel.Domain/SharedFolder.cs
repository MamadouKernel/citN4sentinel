namespace N4Sentinel.Domain;

/// <summary>
/// Relevé ponctuel d'un dossier partagé supervisé (FR-059B/C/D).
///
/// CONSTAT, PAS CONCLUSION. Ce relevé donne des faits — accessibilité,
/// volumétrie, présence des fichiers attendus, indices trouvés — jamais un
/// verdict de corruption. Une corruption ne se déclare "confirmée" que
/// lorsqu'un opérateur habilité en décide ainsi, en lançant la procédure de
/// reconstitution guidée (FR-059D/E) ; jusque-là, tout indice reste une
/// suspicion, quel que soit le nombre d'indices trouvés.
/// </summary>
public class SharedFolderSnapshot : AuditableEntity
{
    public Guid ComponentId { get; set; }
    public N4Component? Component { get; set; }

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Null = non mesuré (contrôle non exécuté), pas "faux".</summary>
    public bool? Reachable { get; set; }
    public string? UnreachableReason { get; set; }

    public int TotalFileCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTimeOffset? OldestFileAt { get; set; }
    public DateTimeOffset? NewestFileAt { get; set; }

    // --- FR-059C : classification, uniquement si les sous-dossiers sont
    // déclarés sur le composant (SharedFolderProfile). Null = non classifié.
    public int? PendingCount { get; set; }
    public int? ConsumedCount { get; set; }
    public int? BlockedCount { get; set; }
    public int? ErrorCount { get; set; }
    public double? OldestPendingAgeHours { get; set; }

    // --- FR-059B : contrôles de santé
    public bool? CanWrite { get; set; }

    /// <summary>Null = sans objet (aucun fichier obligatoire connu pour cette catégorie).</summary>
    public bool? MandatoryFilesPresent { get; set; }
    public List<string> MissingMandatoryFiles { get; set; } = [];

    // --- FR-059D : indices de corruption (jamais un verdict)
    public List<string> CorruptionIndicators { get; set; } = [];
    public bool SuspectedCorruption => CorruptionIndicators.Count > 0;
}

/// <summary>Statut d'un fichier EDI suivi (FR-059H), déduit du sous-dossier où il a été relevé.</summary>
public enum EdiFileStatus
{
    /// <summary>Trouvé dans le sous-dossier de réception, pas encore intégré.</summary>
    EnAttente = 0,

    /// <summary>Trouvé dans le sous-dossier « consommé ».</summary>
    Integre = 1,

    /// <summary>Trouvé dans le sous-dossier « erreur ».</summary>
    Rejete = 2
}

/// <summary>
/// Fichier EDI suivi individuellement (FR-059H à FR-059J) — contrairement à
/// <see cref="SharedFolderSnapshot"/>, qui ne porte que des comptes agrégés,
/// chaque fichier garde son identité d'un relevé à l'autre : c'est ce qui
/// permet d'afficher un tableau par fichier, pas seulement un total.
///
/// TYPE DE MESSAGE ET PARTENAIRE NE SONT JAMAIS DEVINÉS. Aucune convention de
/// nommage des partenaires CIT n'est documentée nulle part : ces deux champs
/// restent null tant qu'un motif d'extraction n'a pas été déclaré sur le
/// composant (<see cref="SharedFolderProfile.EdiFileNamingPattern"/>).
/// </summary>
public class EdiFile : AuditableEntity
{
    public Guid ComponentId { get; set; }
    public N4Component? Component { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string? MessageType { get; set; }
    public string? Partner { get; set; }

    public EdiFileStatus Status { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? IntegratedAt { get; set; }

    /// <summary>Nombre de relevés consécutifs où ce fichier a été vu en état Rejeté.</summary>
    public int ConsecutiveRejections { get; set; }

    public TimeSpan Age => (Status == EdiFileStatus.Integre ? IntegratedAt ?? LastSeenAt : DateTimeOffset.UtcNow) - FirstSeenAt;
}
