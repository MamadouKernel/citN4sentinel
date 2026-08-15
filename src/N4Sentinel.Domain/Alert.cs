namespace N4Sentinel.Domain;

/// <summary>Nature d'une alerte (FR-054).</summary>
public enum AlertKind
{
    /// <summary>Le composant ne répond plus, ou son service est absent.</summary>
    Indisponibilite = 0,

    /// <summary>
    /// Le service tourne mais le journal contredit cet état : c'est le cas que
    /// le cahier des charges vise en premier. Un service « Running » dont le
    /// journal signale une erreur d'initialisation est plus dangereux qu'un
    /// service arrêté, parce qu'il donne l'illusion du fonctionnement.
    /// </summary>
    IncoherenceEtat = 1,

    /// <summary>Aucun marqueur configuré ou journal introuvable : l'état ne peut pas être prouvé.</summary>
    PreuveIndisponible = 2,

    /// <summary>Écart d'horloge au-delà du seuil. Cause documentée de statuts DISCONNECTED trompeurs.</summary>
    EcartHorloge = 3,

    /// <summary>Ressource système en tension : disque, mémoire.</summary>
    RessourceCritique = 4,

    /// <summary>La collecte elle-même a échoué : serveur injoignable, accès refusé.</summary>
    CollecteImpossible = 5,

    /// <summary>Le composant reste en cours de démarrage bien au-delà de son délai.</summary>
    DemarrageAnormalementLong = 6,

    /// <summary>
    /// Center et Standby détiennent tous deux le rôle actif simultanément
    /// (FR-032/033) — un split-brain N4. Portée environnement, pas composant :
    /// aucun des deux nœuds n'est individuellement en cause.
    /// </summary>
    ConflitRoleActifCenter = 7,

    /// <summary>FR-058 : temps de réponse anormalement élevé — un nœud sous tension avant même de tomber.</summary>
    NoeudLent = 8,

    /// <summary>FR-056 : aucun échange N4-XPS normal confirmé depuis plus que le délai toléré.</summary>
    SynchronisationXpsRetardee = 9,

    /// <summary>FR-059I : un ou plusieurs fichiers EDI restent non consommés au-delà du délai déclaré.</summary>
    FichierEdiNonConsomme = 10,

    /// <summary>FR-059I : aucune intégration EDI réussie observée depuis plus que le délai déclaré.</summary>
    AucuneIntegrationEdiRecente = 11,

    /// <summary>
    /// FR-059I : un fichier EDI échoue plusieurs fois de suite. Distinct de
    /// <see cref="FichierEdiNonConsomme"/> — un fichier peut échouer et être
    /// retraité rapidement plusieurs fois sans jamais dépasser le seuil
    /// d'ancienneté, ce qui masquerait un partenaire ou un format qui pose
    /// systématiquement problème.
    /// </summary>
    EchecsEdiRepetes = 12
}

public enum AlertSeverity
{
    Information = 0,
    Avertissement = 1,
    Critique = 2
}

public enum AlertStatus
{
    /// <summary>La condition est toujours vraie.</summary>
    Ouverte = 0,

    /// <summary>Un opérateur l'a vue et assumée ; elle reste ouverte mais sort du décompte.</summary>
    Acquittee = 1,

    /// <summary>La condition a cessé d'être vraie. Résolution automatique.</summary>
    Resolue = 2
}

/// <summary>
/// Alerte de supervision.
///
/// N'HÉRITE PAS d'AuditableEntity, délibérément. Une alerte est une
/// observation produite par la machine, pas une modification de configuration
/// décidée par quelqu'un. L'inscrire au journal d'audit noierait les actions
/// humaines — les seules que ce journal doit permettre de retrouver — sous des
/// milliers d'écritures automatiques.
///
/// LE POINT CENTRAL EST LA SIGNATURE. La collecte tourne toutes les
/// trente secondes : sans déduplication, un composant en panne produirait
/// deux alertes par minute, et la liste deviendrait illisible en une heure.
/// Une alerte reste donc unique tant que sa condition dure ; seuls son
/// compteur d'occurrences et sa date de dernière observation évoluent.
/// </summary>
public class Alert
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Identité de la condition : type d'alerte et composant concerné.
    /// Deux observations de même signature sont la même alerte.
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    /// <summary>Null pour une alerte de portée environnement.</summary>
    public Guid? ComponentId { get; set; }
    public N4Component? Component { get; set; }

    public string ComponentName { get; set; } = string.Empty;

    public AlertKind Kind { get; set; }
    public AlertSeverity Severity { get; set; }
    public AlertStatus Status { get; set; } = AlertStatus.Ouverte;

    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;

    /// <summary>Ce qu'il convient de vérifier. Une alerte sans conduite à tenir est un bruit.</summary>
    public string? Recommendation { get; set; }

    public DateTimeOffset FirstOccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastOccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Nombre d'observations depuis l'ouverture. Mesure la persistance, pas la gravité.</summary>
    public int OccurrenceCount { get; set; } = 1;

    public DateTimeOffset? ResolvedAt { get; set; }

    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public string? AcknowledgementNote { get; set; }

    public bool IsOpen => Status is AlertStatus.Ouverte or AlertStatus.Acquittee;

    /// <summary>Durée pendant laquelle la condition a été observée.</summary>
    public TimeSpan Duration => (ResolvedAt ?? DateTimeOffset.UtcNow) - FirstOccurredAt;

    /// <summary>Construit la signature d'une condition. Unique par composant et par type.</summary>
    public static string BuildSignature(AlertKind kind, Guid? componentId) =>
        componentId.HasValue ? $"{kind}:{componentId.Value:N}" : $"{kind}:environnement";
}
