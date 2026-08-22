namespace N4Sentinel.Domain;

/// <summary>
/// Mode d'acces technique a un serveur.
/// </summary>
public enum CredentialMode
{
    /// <summary>
    /// L'application se connecte sous sa propre identite Windows.
    ///
    /// MODE RECOMMANDE. Quand N4 Sentinel tourne sous un compte de service du
    /// domaine, il n'y a aucun mot de passe a stocker, chiffrer, renouveler ni
    /// proteger. C'est strictement superieur a n'importe quel coffre : le
    /// secret qui n'existe pas ne peut pas fuir.
    /// </summary>
    IdentiteDuProcessus = 0,

    /// <summary>
    /// Compte explicite, avec mot de passe chiffre en base.
    ///
    /// Repli pour un serveur hors domaine, ou lorsque l'Infrastructure impose
    /// un compte distinct par environnement - ce que la separation des droits
    /// entre UAT et Production peut justifier (SEC-004).
    /// </summary>
    CompteExplicite = 1
}

/// <summary>
/// Compte technique utilise par les connecteurs.
///
/// LE MOT DE PASSE NE SORT JAMAIS D'ICI EN CLAIR. Il est chiffre au repos et
/// n'est dechiffre qu'au moment d'ouvrir une session distante. Aucun ecran,
/// aucun export, aucune API ne le restitue - y compris a un administrateur de
/// la solution. Le cahier des charges est explicite : l'Administrateur
/// Infrastructure configure les references aux secrets "sans exposer les
/// informations d'authentification" (SEC-003).
///
/// Le champ ne porte donc que le chiffre. La classe n'expose deliberement
/// aucune propriete permettant de relire le clair : le dechiffrement passe
/// par le magasin, qui journalise chaque acces.
/// </summary>
public class TechnicalCredential : AuditableEntity
{
    /// <summary>
    /// Cle de reference, unique. C'est elle que portent les serveurs et les
    /// environnements - jamais le compte lui-meme, afin qu'un changement de
    /// mot de passe n'oblige pas a rouvrir chaque fiche serveur.
    /// </summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>Libelle lisible, affiche dans les listes de selection.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Environnement du compte PARTAGE. Nul pour un compte nominatif : le
    /// compte d'administration d'une personne est le meme partout, et le lier a
    /// un environnement obligerait chacun a le ressaisir autant de fois qu'il y
    /// a d'environnements, sans rien apporter.
    /// </summary>
    public Guid? EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    public CredentialMode Mode { get; set; } = CredentialMode.IdentiteDuProcessus;

    /// <summary>Compte, au format DOMAINE\\utilisateur. Vide en identite du processus.</summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Mot de passe chiffre. Jamais affiche, jamais journalise, jamais exporte.
    /// Null en identite du processus.
    /// </summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>
    /// Date de dernier changement du mot de passe. Sert a reperer un compte
    /// dont le secret n'a pas tourne depuis longtemps, sans rien reveler de
    /// sa valeur.
    /// </summary>
    public DateTimeOffset? PasswordSetAt { get; set; }

    /// <summary>Horodatage du dernier test de connexion reussi.</summary>
    public DateTimeOffset? LastVerifiedAt { get; set; }

    /// <summary>Resultat du dernier test, pour affichage sans re-tester.</summary>
    public string? LastVerificationResult { get; set; }

    public string? Description { get; set; }

    public LifecycleStatus Status { get; set; } = LifecycleStatus.Brouillon;

    // -----------------------------------------------------------------------
    // Compte nominatif
    // -----------------------------------------------------------------------
    /// <summary>
    /// Utilisateur de l'application proprietaire de ce compte. Null pour un
    /// compte partage - le cas historique, qui reste celui du travail non
    /// surveille.
    ///
    /// POURQUOI CE CHAMP EXISTE. Un compte de service partage rend toutes les
    /// actions anonymes : le journal de securite du serveur N4 nomme le compte,
    /// jamais la personne. Un compte nominatif par operateur retablit
    /// l'attribution la ou elle est opposable - sur le serveur cible, pas
    /// seulement dans notre propre base.
    ///
    /// Un compte nominatif n'est JAMAIS selectionnable sur une fiche serveur :
    /// il n'est pas un choix de configuration, il se resout a l'execution
    /// depuis l'identite de celui qui a lance l'action.
    /// </summary>
    public string? OwnerUserId { get; set; }

    /// <summary>
    /// Identifiant de connexion applicatif du proprietaire.
    ///
    /// C'est par lui que le moteur d'orchestration retrouve le compte : une
    /// execution enregistre le nom de son demandeur, pas son identifiant
    /// technique. Le conserver ici evite de trainer la gestion des identites
    /// jusque dans le moteur, qui n'a rien a y faire.
    /// </summary>
    public string? OwnerLogin { get; set; }

    /// <summary>
    /// Nom affichable du proprietaire, copie ici pour que les ecrans et les
    /// journaux n'aient pas a joindre la table des utilisateurs - notamment
    /// apres la suppression d'un compte applicatif.
    /// </summary>
    public string? OwnerDisplayName { get; set; }

    public bool IsNominative => !string.IsNullOrWhiteSpace(OwnerUserId);

    /// <summary>
    /// Le secret enregistre a cesse de fonctionner : il doit etre ressaisi.
    ///
    /// REGLE NON NEGOCIABLE. Des qu'une authentification est refusee, le compte
    /// bascule ici et n'est PLUS JAMAIS reessaye tant qu'il n'a pas ete
    /// ressaisi. Reessayer un mot de passe perime sur plusieurs serveurs, avec
    /// les reprises d'une sequence, verrouille le compte d'administration de la
    /// personne dans le domaine - l'outil cense aider mettrait l'operateur
    /// dehors en pleine escale.
    ///
    /// A ne pas confondre avec un acces refuse : la, le mot de passe est bon et
    /// ce sont les droits qui manquent. Le ressaisir n'y changerait rien.
    /// </summary>
    public bool RequiresReentry { get; set; }

    public DateTimeOffset? InvalidatedAt { get; set; }

    /// <summary>Pourquoi le compte a ete invalide, en clair, sans secret.</summary>
    public string? InvalidationReason { get; set; }

    /// <summary>
    /// Un compte explicite sans mot de passe enregistre est inutilisable :
    /// l'interface doit le signaler plutot que de laisser echouer la connexion.
    /// </summary>
    public bool IsUsable => !RequiresReentry
                            && (Mode == CredentialMode.IdentiteDuProcessus
                                || (!string.IsNullOrWhiteSpace(UserName)
                                    && !string.IsNullOrWhiteSpace(ProtectedPassword)));

    /// <summary>Etat du secret, formule sans rien en reveler.</summary>
    public string SecretState => this switch
    {
        { RequiresReentry: true } => "A ressaisir",
        { Mode: CredentialMode.IdentiteDuProcessus } => "Sans objet - identite du processus",
        _ when string.IsNullOrWhiteSpace(ProtectedPassword) => "Non defini",
        _ => "Defini"
    };
}
