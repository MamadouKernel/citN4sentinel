namespace N4Sentinel.Domain;

/// <summary>
/// Objet du referentiel qui se retire du service sans etre efface.
///
/// POURQUOI. Deux exigences s'opposaient : pouvoir nettoyer un referentiel qui
/// accumule les saisies erronees, et ne jamais rendre illisible l'historique
/// d'exploitation. La suppression franche ne peut satisfaire que l'une des
/// deux - elle se heurte aux executions passees, qui referencent l'objet et
/// doivent rester interpretables.
///
/// La suppression douce les satisfait ensemble : la ligne demeure, les
/// executions continuent de la designer, et l'objet disparait de tout ce que
/// l'application montre et supervise. Le geste est de surcroit REVERSIBLE, ce
/// qui compte pour une action declenchee a la main sur un referentiel de
/// production.
///
/// L'invisibilite n'est pas laissee a la vigilance de chaque requete : elle est
/// posee une fois pour toutes par un filtre global au niveau du contexte. Un
/// oubli quelque part ferait reapparaitre un objet retire, ou pire, le ferait
/// piloter par le moteur.
/// </summary>
public interface ISoftDeletable
{
    /// <summary>Date de retrait. Null tant que l'objet est en service.</summary>
    DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Qui l'a retire.</summary>
    string? DeletedBy { get; set; }

    /// <summary>
    /// Identifiant du RETRAIT qui a emporte cet objet.
    ///
    /// Retirer un environnement emporte ses serveurs et ses composants. Sans ce
    /// marqueur, le retablir ressusciterait aussi ce qui avait ete retire
    /// separement, des semaines plus tot, et volontairement. Le lot rend le
    /// retablissement exact : il ne rend que ce que ce geste-la avait pris.
    /// </summary>
    Guid? DeletionBatchId { get; set; }

    /// <summary>Motif saisi au retrait, pour que la corbeille se relise.</summary>
    string? DeletionReason { get; set; }
}
