namespace N4Sentinel.Domain;

/// <summary>
/// MERISE — ENT_SVCASSOC. Service Windows / processus associé à un composant N4
/// (ex. XPSGateService, XMLRDTService, BridgeService...) qui doit être contrôlé
/// en plus du service principal piloté.
///
/// VARIANTE TABULAIRE par rapport à <see cref="N4Component.CompanionServiceNames"/>
/// (qui reste la liste JSON simple pour compatibilité). Cette version permet :
/// - Un statut supervisé individuel par service
/// - Un port / compte de service / timeout dédié
/// - Historique d'indisponibilités par service
/// </summary>
public class ComponentAssociatedService : AuditableEntity
{
    public Guid ComponentId { get; set; }
    public N4Component? Component { get; set; }

    /// <summary>Nom exact du service Windows (clé de registre).</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>Nom affiché à l'opérateur.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public AssociatedServiceStatus ExpectedStatus { get; set; } = AssociatedServiceStatus.EnMarche;

    /// <summary>Port TCP associé, le cas échéant (XMLRDT, XPSGate...).</summary>
    public int? ExpectedPort { get; set; }

    /// <summary>Compte Windows sous lequel le service DOIT tourner (RG-0809).</summary>
    public string? ExpectedServiceAccount { get; set; }

    /// <summary>Timeout dédié pour démarrage / arrêt, en secondes.</summary>
    public int? TimeoutSecondsOverride { get; set; }

    /// <summary>
    /// Vrai : le service est obligatoirement requis. Faux : le composant peut
    /// être opérationnel sans lui (ex. service optionnel version 4.x).
    /// </summary>
    public bool IsMandatory { get; set; } = true;

    public int? LastObservedPid { get; set; }

    public DateTimeOffset? LastObservedAt { get; set; }

    public AssociatedServiceStatus? LastObservedStatus { get; set; }

    public string? LastObservationNote { get; set; }

    public int Order { get; set; }

    public bool IsEnabled { get; set; } = true;
}
