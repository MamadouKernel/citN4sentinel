namespace N4Sentinel.Domain;

/// <summary>
/// Habilitation d'un utilisateur sur un environnement (SEC-004, audit SEC-A1).
///
/// LE RÔLE DIT CE QU'ON SAIT FAIRE, L'HABILITATION DIT OÙ ON A LE DROIT DE LE
/// FAIRE. Sans cette seconde dimension, un opérateur à qui l'on confie le
/// redémarrage d'un nœud en recette peut, avec exactement les mêmes droits,
/// arrêter la Production : il lui suffit de changer d'environnement dans la
/// liste déroulante.
///
/// Le bandeau « PROD » de l'interface AVERTIT l'opérateur ; il ne l'EMPÊCHE de
/// rien. L'écart entre les deux est précisément ce que cette table ferme.
/// </summary>
public class EnvironmentGrant : AuditableEntity
{
    /// <summary>Identifiant Identity de l'utilisateur habilité.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Recopié pour que le journal reste lisible après suppression du compte.</summary>
    public string UserName { get; set; } = string.Empty;

    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    /// <summary>
    /// Consultation seule, ou action.
    ///
    /// Un même utilisateur peut être habilité à CONSULTER la Production sans
    /// être habilité à y AGIR — c'est le cas le plus fréquent d'un support
    /// N1, et il n'était pas exprimable jusqu'ici.
    /// </summary>
    public EnvironmentGrantLevel Level { get; set; } = EnvironmentGrantLevel.Consultation;

    /// <summary>Justification de l'habilitation, pour la revue de droits.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Fin de validité facultative. Une habilitation temporaire — une
    /// intervention de prestataire, une astreinte — doit pouvoir s'éteindre
    /// d'elle-même : les droits que personne ne retire sont ceux qui s'accumulent.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsExpired => ExpiresAt is not null && DateTimeOffset.UtcNow > ExpiresAt;

    public bool IsActive => !IsExpired;

    public bool AllowsAction => IsActive && Level == EnvironmentGrantLevel.Action;
}

public enum EnvironmentGrantLevel
{
    /// <summary>Voir l'état, les journaux, l'historique. Ne rien changer.</summary>
    Consultation = 0,

    /// <summary>Lancer, approuver, contourner — tout ce qui touche l'écosystème.</summary>
    Action = 1
}
