using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Connectivity;

/// <summary>
/// Interroge reellement un serveur declare au referentiel, en LECTURE SEULE.
///
/// C'est le test unitaire de la fiche serveur : il repond aux trois questions
/// qu'un operateur se pose au moment de la saisie.
///   1. Le serveur repond-il, et sous quelle identite me connecte-je ?
///   2. Dans quel etat est-il - ressources, horloge ?
///   3. Les noms de services que j'ai saisis existent-ils vraiment ?
///
/// La troisieme est celle qui rend le plus de service. Une faute de frappe
/// dans un nom de service ne se voit pas : elle attend le premier arret de
/// Production pour se manifester. La detecter a la saisie coute une seconde,
/// la detecter en exploitation coute une fenetre de maintenance.
/// </summary>
public sealed class ServerProbe(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector)
{
    public async Task<ServerProbeResult> ProbeAsync(Guid serverId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var server = await db.Servers
            .AsNoTracking()
            .Include(s => s.Environment)
            .FirstOrDefaultAsync(s => s.Id == serverId, ct);

        if (server is null)
            return ServerProbeResult.Failed("Serveur introuvable dans le referentiel.");

        // 1. Resolution de la cible : c'est aussi ce qui valide la coherence
        //    du compte technique reference par la fiche.
        var resolution = await targetFactory.CreateAsync(server, ct);
        if (!resolution.Succeeded)
            return ServerProbeResult.Failed(resolution.Error!);

        var target = resolution.Target!;
        var result = new ServerProbeResult
        {
            HostName = server.HostName,
            IdentityDescription = resolution.IdentityDescription,
            IsLocal = target.IsLocal
        };

        // 2. Le serveur repond-il ?
        var ping = await connector.PingAsync(target, ct);
        result.Reachable = ping.Succeeded;
        result.RoundTrip = ping.Duration;

        if (!ping.Succeeded)
        {
            result.Error = ping.Error;
            result.Failure = ping.Failure;
            return result;
        }
        result.RemoteIdentity = ping.Value;

        // 3. Etat du systeme. Un echec ici n'invalide pas le test : le serveur
        //    a repondu, c'est l'essentiel.
        var system = await connector.GetSystemAsync(target, ct);
        if (system.Succeeded) result.System = system.Value;
        else result.SystemError = system.Error;

        // 4. Les services declares existent-ils ? (FR-007, verification reelle)
        var components = await db.Components
            .AsNoTracking()
            .Where(c => c.ServerId == serverId
                        && c.WindowsServiceName != null
                        && c.WindowsServiceName != "")
            .OrderBy(c => c.StartOrder).ThenBy(c => c.LogicalName)
            .Select(c => new { c.LogicalName, c.WindowsServiceName, c.ControlMode })
            .ToListAsync(ct);

        if (components.Count == 0)
        {
            result.ServiceNote = "Aucun composant declare sur ce serveur avec un nom de service.";
            return result;
        }

        var names = components.Select(c => c.WindowsServiceName!).Distinct().ToList();
        var services = await connector.GetServicesAsync(target, names, ct);

        if (!services.Succeeded)
        {
            result.ServiceNote = $"Interrogation des services impossible : {services.Error}";
            return result;
        }

        var byName = services.Value!
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var c in components)
        {
            // Le connecteur renvoie le nom demande quand le service est
            // introuvable, et le nom court reel sinon : on retrouve donc
            // l'entree par l'un ou par l'autre.
            var snapshot = byName.GetValueOrDefault(c.WindowsServiceName!)
                           ?? services.Value!.FirstOrDefault(s =>
                                  string.Equals(s.DisplayName, c.WindowsServiceName, StringComparison.OrdinalIgnoreCase));

            result.Services.Add(new ServiceCheck
            {
                ComponentName = c.LogicalName,
                DeclaredServiceName = c.WindowsServiceName!,
                Exists = snapshot?.Exists ?? false,
                Status = snapshot?.Status ?? "Introuvable",
                ResolvedName = snapshot?.Name,
                DisplayName = snapshot?.DisplayName,
                StartMode = snapshot?.StartMode,
                ProcessId = snapshot?.ProcessId,
                IsControllable = c.ControlMode == ControlMode.Pilotable
            });
        }

        return result;
    }
}

/// <summary>Resultat d'une interrogation de serveur.</summary>
public sealed class ServerProbeResult
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string HostName { get; init; } = string.Empty;

    /// <summary>Identite employee pour la connexion, sans aucun secret.</summary>
    public string IdentityDescription { get; init; } = string.Empty;

    public bool IsLocal { get; init; }
    public bool Reachable { get; set; }
    public TimeSpan RoundTrip { get; set; }

    /// <summary>Ce que le serveur distant a repondu : machine, version, compte effectif.</summary>
    public string? RemoteIdentity { get; set; }

    public SystemSnapshot? System { get; set; }
    public string? SystemError { get; set; }

    public List<ServiceCheck> Services { get; } = [];
    public string? ServiceNote { get; set; }

    public string? Error { get; set; }
    public ConnectorFailure Failure { get; set; }

    public int ServicesFound => Services.Count(s => s.Exists);
    public int ServicesMissing => Services.Count(s => !s.Exists);

    /// <summary>
    /// Un service declare pilotable mais introuvable est bloquant : le
    /// composant ne pourra jamais etre pilote, et l'erreur ne se verrait
    /// qu'au moment de l'operation.
    /// </summary>
    public bool HasBlockingIssue => Services.Any(s => !s.Exists && s.IsControllable);

    public static ServerProbeResult Failed(string error) =>
        new() { Reachable = false, Error = error };
}

public sealed class ServiceCheck
{
    public required string ComponentName { get; init; }
    public required string DeclaredServiceName { get; init; }
    public bool Exists { get; init; }
    public required string Status { get; init; }

    /// <summary>Nom court reel, quand la saisie portait le nom d'affichage.</summary>
    public string? ResolvedName { get; init; }
    public string? DisplayName { get; init; }
    public string? StartMode { get; init; }
    public int? ProcessId { get; init; }
    public bool IsControllable { get; init; }

    /// <summary>
    /// Le nom saisi n'est pas celui sous lequel Windows connait le service.
    /// Ce n'est pas une erreur - le connecteur accepte les deux - mais le
    /// signaler evite des interrogations lors d'un diagnostic.
    /// </summary>
    public bool NameDiffers => Exists
                               && ResolvedName is not null
                               && !string.Equals(ResolvedName, DeclaredServiceName, StringComparison.OrdinalIgnoreCase);
}
