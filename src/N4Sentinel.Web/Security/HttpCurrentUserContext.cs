using N4Sentinel.Domain;

namespace N4Sentinel.Web.Security;

/// <summary>
/// Fournit au journal d'audit l'identite reelle de l'acteur et son adresse IP.
///
/// L'adresse IP est celle vue par le serveur. Derriere un repartiteur de
/// charge ou un proxy, elle n'est exploitable que si le middleware d'en-tetes
/// transmis est configure - a verifier lors du deploiement, sans quoi toutes
/// les entrees porteront l'adresse du proxy.
/// </summary>
public sealed class HttpCurrentUserContext(IHttpContextAccessor accessor) : ICurrentUserContext
{
    public string Actor
    {
        get
        {
            var user = accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated == true)
                return user.Identity.Name ?? "utilisateur inconnu";

            // Hors requete authentifiee : tache de fond, migration, amorcage.
            return "systeme";
        }
    }

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// Identifiant de correlation de la requete. Reutilise ensuite pour relier
    /// entre elles toutes les entrees d'audit d'une meme operation.
    /// </summary>
    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;
}
