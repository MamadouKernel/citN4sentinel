using System.Text.Json.Nodes;

namespace N4Sentinel.Domain.Interfaces;

/// <summary>
/// Contrat « interfaces externes · Ticketing » (S1-09 + PB-25).
///
/// <para>Flux bidirectionnel couvert :
/// <list type="number">
///   <item>SORTANT — création d'un ticket dans l'outil DSI (ServiceNow / Jira /
///         générique Basic Auth), mise à jour du statut, ajout de commentaire.</item>
///   <item>ENTRANT — webhook de la plateforme DSI : réception d'un événement
///         (création / commentaire / résolution), résolution de l'incident
///         côté Sentinel.</item>
/// </list>
/// </para>
///
/// <para><b>SEC-001 · Double-garde</b> : aucune implémentation ne doit émettre
/// d'appel HTTP tant que <c>TicketingExternalOptions.Enabled</c> est à
/// <c>false</c> ou que <c>BaseUrl</c> est absent. Le contrat accepte des
/// valeurs <c>null</c> de retour pour signaler « flux désactivé ».</para>
/// </summary>
public interface ITicketingConnector
{
    /// <summary>
    /// FLUX SORTANT · Crée l'incident correspondant côté DSI et retourne
    /// l'identifiant externe. Double-garde activée : retourne <c>null</c>
    /// <b>sans appel HTTP</b> si les options sont désactivées.
    /// </summary>
    Task<string?> PousserIncidentExterneAsync(Ticket ticket, CancellationToken ct = default);

    /// <summary>
    /// FLUX SORTANT · Transfère <b>les commentaires internes</b> d'un ticket
    /// vers le système DSI. Les commentaires marqués
    /// <see cref="TicketCommentSource.Externe"/> (provenant du DSI) ne sont
    /// <b>pas</b> renvoyés pour éviter la boucle infinie.
    /// </summary>
    /// <returns>Nombre de commentaires effectivement poussés.</returns>
    Task<int> PousserCommentairesExternesAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>
    /// FLUX SORTANT · Interroge la plateforme DSI pour rafraîchir le statut
    /// du ticket dans Sentinel (ex. P1 escaladé → Ferme). Retourne le nouveau
    /// statut, ou null si le ticket n'existe plus côté DSI ou que le flux est
    /// désactivé (SEC-001).
    /// </summary>
    Task<TicketStatus?> RecupererStatutExterneAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>
    /// FLUX ENTRANT · Point d'entrée du webhook : reçoit un objet JSON brut
    /// (payload + en-têtes éventuels) et applique la bonne stratégie selon
    /// le système déclaré. Les événements non reconnus produisent une entrée
    /// d'audit « EchecSynchro » sans exception.
    /// </summary>
    /// <param name="systemeExterne">Système émetteur (ex. <c>servicenow</c>,
    /// <c>jira</c>, <c>generique</c>). Peut provenir de la route Webhook.</param>
    /// <param name="payload">JSON brut du corps de requête, désérialisé en
    /// arbre JsonNode.</param>
    /// <param name="correlationId">Corrélation à reporter dans l'audit.</param>
    /// <param name="ct">Jeton d'annulation.</param>
    /// <returns>Tuple (accepté, statutSentinel, externalId).</returns>
    Task<(bool Accepte, string? StatutSentinel, string? ExternalId)>
        RecevoirWebhookExterneAsync(string systemeExterne, JsonNode payload,
                                     string? correlationId, CancellationToken ct = default);
}
