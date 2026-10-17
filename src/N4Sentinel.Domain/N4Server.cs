namespace N4Sentinel.Domain;

/// <summary>
/// Serveur ou machine virtuelle hebergeant un ou plusieurs composants N4 (FR-002).
///
/// Un serveur peut porter plusieurs composants : chez CIT, le Bridge et XPS
/// tournent sur la meme machine physique. La cartographie doit le representer
/// tel quel, sans le dedoubler.
/// </summary>
public class N4Server : AuditableEntity, ISoftDeletable
{
    // --- Retrait du service (voir ISoftDeletable) ------------------------
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public Guid? DeletionBatchId { get; set; }
    public string? DeletionReason { get; set; }

    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    /// <summary>Nom d'hote tel qu'utilise pour joindre la machine (WinRM).</summary>
    public string HostName { get; set; } = string.Empty;

    public string? IpAddress { get; set; }

    public string? DnsName { get; set; }

    public string? OperatingSystem { get; set; }

    /// <summary>Port WinRM. 5985 en HTTP, 5986 en HTTPS.</summary>
    public int WinRmPort { get; set; } = 5985;

    /// <summary>
    /// Impose HTTPS pour la session WinRM (SEC-004). Défaut FAUX (HTTP/5985)
    /// pour cet environnement : le parc N4 est sur un domaine Active Directory
    /// isolé, où WinRM authentifie en Kerberos et chiffre déjà la charge utile
    /// au niveau message même en HTTP ; HTTPS (5986) exigerait un certificat
    /// serveur sur chaque nœud, absent ici. Un serveur en HTTPS reste possible
    /// par fiche (cocher SSL + port 5986). Les serveurs déjà déclarés en base
    /// gardent la valeur qu'ils avaient — ce changement de défaut n'affecte
    /// que les fiches créées à partir de maintenant.
    /// </summary>
    public bool UseSsl { get; set; } = false;

    public CriticalityLevel Criticality { get; set; } = CriticalityLevel.Moyenne;

    public LifecycleStatus Status { get; set; } = LifecycleStatus.Brouillon;

    public string? Description { get; set; }

    /// <summary>Responsable technique a contacter pour cette machine.</summary>
    public string? TechnicalOwner { get; set; }

    /// <summary>
    /// Reference du secret permettant de se connecter a ce serveur : nom de
    /// l'entree dans le coffre, jamais l'identifiant ni le mot de passe.
    /// Le cahier des charges interdit d'afficher, de journaliser ou
    /// d'integrer un secret au code (SEC-003).
    /// </summary>
    public string? CredentialReference { get; set; }

    public ICollection<N4Component> Components { get; set; } = [];
}
