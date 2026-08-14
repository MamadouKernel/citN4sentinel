namespace N4Sentinel.Domain;

/// <summary>
/// Un environnement N4 autorise : Production, UAT, ou tout autre environnement
/// valide par la DSI (FR-001).
///
/// Chaque environnement possede sa propre cartographie, ses composants, ses
/// workflows, ses seuils et ses regles d'autorisation. L'environnement de
/// Production doit rester identifiable en permanence dans l'interface.
///
/// Nomme N4Environment et non Environment pour ne pas entrer en collision
/// avec System.Environment.
/// </summary>
public class N4Environment : AuditableEntity
{
    /// <summary>Code court affiche partout dans l'interface (ex. PROD, UAT).</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public EnvironmentKind Kind { get; set; } = EnvironmentKind.UAT;

    public CriticalityLevel Criticality { get; set; } = CriticalityLevel.Moyenne;

    public LifecycleStatus Status { get; set; } = LifecycleStatus.Brouillon;

    /// <summary>
    /// Fuseau horaire de reference des serveurs de cet environnement.
    /// Sert a aligner les horodatages collectes sur une chronologie commune :
    /// un ecart d'horloge non identifie est une cause connue de faux
    /// diagnostics (statuts DISCONNECTED trompeurs).
    /// </summary>
    public string TimeZoneId { get; set; } = "W. Central Africa Standard Time";

    public string? Description { get; set; }

    /// <summary>Responsable technique de l'environnement.</summary>
    public string? TechnicalOwner { get; set; }

    /// <summary>Responsable fonctionnel ou metier.</summary>
    public string? FunctionalOwner { get; set; }

    /// <summary>
    /// Compte technique employe par defaut sur cet environnement. Chaque
    /// serveur peut le surcharger via sa propre reference.
    ///
    /// Vide : connexion sous l'identite du processus applicatif - mode
    /// recommande, puisqu'il n'y a alors aucun secret a proteger.
    ///
    /// La reference est portee ici plutot que le compte lui-meme, afin qu'un
    /// changement de mot de passe n'oblige a rouvrir aucune fiche serveur.
    /// </summary>
    public string? DefaultCredentialReference { get; set; }

    /// <summary>
    /// Source de temps attendue sur les serveurs de cet environnement : nom du
    /// contrôleur de domaine, ou fragment reconnaissable (ex. « cit.local »).
    ///
    /// Renseignee, elle permet de repondre OK/KO a la question « ce serveur
    /// est-il synchronise sur l'horloge du domaine ». Vide, l'application sait
    /// encore detecter les cas franchement anormaux - horloge locale, service
    /// arrete, synchronisation desactivee - mais ne peut pas confirmer que la
    /// source EST celle du domaine : elle repondra « a confirmer » plutot que
    /// d'affirmer une conformite qu'elle n'a pas verifiee.
    /// </summary>
    public string? ExpectedTimeSource { get; set; }

    /// <summary>
    /// Ecart d'horloge tolere, en secondes. Au-dela, le serveur est declare non
    /// conforme. Cinq secondes par defaut : c'est le seuil au-dela duquel N4
    /// commence a produire des statuts DISCONNECTED trompeurs.
    /// </summary>
    public int ClockToleranceSeconds { get; set; } = 5;

    /// <summary>
    /// Vrai pour l'environnement de Production. Declenche l'affichage
    /// permanent de l'indicateur de Production et les regles renforcees :
    /// motif obligatoire, approbation prealable, double validation.
    /// </summary>
    public bool IsProduction => Kind == EnvironmentKind.Production;

    /// <summary>
    /// Un environnement doit etre Valide ou Actif pour qu'une operation
    /// puisse y etre lancee (FR-006).
    /// </summary>
    public bool IsOperable => Status is LifecycleStatus.Valide or LifecycleStatus.Actif;

    public ICollection<N4Server> Servers { get; set; } = [];
    public ICollection<N4Component> Components { get; set; } = [];
}
