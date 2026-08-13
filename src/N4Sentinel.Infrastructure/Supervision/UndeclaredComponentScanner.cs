using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Supervision;

/// <summary>
/// Repère les services Navis présents sur les serveurs mais absents du
/// référentiel (SUP-06, recette AC-15).
///
/// Le cas visé est concret : quelqu'un installe un nœud Cluster supplémentaire
/// un vendredi soir. Personne ne pense à le déclarer. Il tourne, il participe
/// au cluster, et N4 Sentinel l'ignore — donc ne le supervise pas, ne l'arrête
/// pas dans les séquences, et le laisse en fonctionnement pendant une
/// maintenance censée tout arrêter.
///
/// CE SERVICE NE DÉCLARE RIEN AUTOMATIQUEMENT. Il signale, et c'est tout.
/// Le cahier des charges est explicite : un nouveau nœud « apparaît comme
/// composant à valider et aucune action n'est autorisée avant confirmation de
/// son rôle ». Créer la fiche à la place de l'opérateur reviendrait à
/// autoriser des actions sur une machine que personne n'a validée.
/// </summary>
public sealed class UndeclaredComponentScanner(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector)
{
    /// <summary>
    /// Motifs de noms de services considérés comme relevant de l'écosystème N4.
    /// Volontairement larges : mieux vaut proposer un service de trop, que
    /// l'opérateur écartera d'un regard, que d'en manquer un.
    /// </summary>
    private static readonly string[] Motifs = ["navis", "sparcs", "xps", "ecn4", "n4sim"];

    public async Task<ScanResult> ScanAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var serveurs = await db.Servers
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId)
            .ToListAsync(ct);

        if (serveurs.Count == 0)
            return ScanResult.Failed("Aucun serveur déclaré dans cet environnement.");

        // Noms deja declares, sous leurs deux formes possibles : le referentiel
        // peut porter le nom court comme le nom d'affichage.
        var declares = await db.Components
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId && c.WindowsServiceName != null)
            .Select(c => c.WindowsServiceName!)
            .ToListAsync(ct);

        var connus = new HashSet<string>(declares, StringComparer.OrdinalIgnoreCase);
        var resultat = new ScanResult();

        foreach (var serveur in serveurs)
        {
            var resolution = await targetFactory.CreateAsync(serveur, ct);
            if (!resolution.Succeeded)
            {
                resultat.Unreachable.Add(new UnreachableServer(serveur.HostName, resolution.Error!));
                continue;
            }

            var services = await connector.ListServicesAsync(resolution.Target!, Motifs, ct);
            if (!services.Succeeded)
            {
                resultat.Unreachable.Add(new UnreachableServer(serveur.HostName, services.Error!));
                continue;
            }

            resultat.ServersScanned++;

            foreach (var svc in services.Value!)
            {
                if (connus.Contains(svc.Name) ||
                    (svc.DisplayName is not null && connus.Contains(svc.DisplayName)))
                    continue;

                resultat.Undeclared.Add(new UndeclaredComponent
                {
                    ServerId = serveur.Id,
                    HostName = serveur.HostName,
                    ServiceName = svc.Name,
                    DisplayName = svc.DisplayName,
                    Status = svc.Status,
                    ProcessId = svc.ProcessId,
                    SuggestedRole = DeduireRole(svc.DisplayName ?? svc.Name)
                });
            }
        }

        return resultat;
    }

    /// <summary>
    /// Propose un rôle d'après le nom du service. Ce n'est qu'une suggestion :
    /// l'opérateur confirme ou corrige. Se tromper de rôle fausserait l'ordre
    /// des séquences, ce qui est précisément l'erreur que la solution existe
    /// pour empêcher.
    /// </summary>
    private static ComponentRole DeduireRole(string nom)
    {
        var n = nom.ToLowerInvariant();

        // L'ordre compte : "Standby Center Node" contient aussi "center".
        if (n.Contains("standby")) return ComponentRole.StandbyCenterNode;
        if (n.Contains("bridge")) return ComponentRole.BridgeDaemon;
        if (n.Contains("ecn4web") || n.Contains("ecn4 web")) return ComponentRole.Ecn4Web;
        if (n.Contains("ecn4")) return ComponentRole.Ecn4;
        if (n.Contains("xps")) return ComponentRole.Xps;
        if (n.Contains("cluster")) return ComponentRole.ClusterNode;
        if (n.Contains("center")) return ComponentRole.CenterNode;

        return ComponentRole.Autre;
    }

    /// <summary>
    /// Crée la fiche d'un composant repéré, en Brouillon.
    ///
    /// Appelée uniquement sur action explicite d'un opérateur. La fiche naît en
    /// Brouillon et en supervision seule : elle devra suivre le cycle de
    /// validation avant qu'une opération puisse la viser.
    /// </summary>
    public async Task<Guid?> DeclareAsync(
        Guid environmentId, UndeclaredComponent decouvert, ComponentRole role,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var nomLogique = decouvert.DisplayName ?? decouvert.ServiceName;

        // Le nom logique est unique par environnement : on suffixe par l'hôte
        // en cas de collision, plutôt que d'échouer sur une contrainte.
        if (await db.Components.AnyAsync(c => c.EnvironmentId == environmentId && c.LogicalName == nomLogique, ct))
            nomLogique = $"{nomLogique} ({decouvert.HostName})";

        var composant = new N4Component
        {
            EnvironmentId = environmentId,
            ServerId = decouvert.ServerId,
            LogicalName = nomLogique,
            Role = role,
            WindowsServiceName = decouvert.ServiceName,
            ControlMode = ControlMode.SuperviseSeulement,
            Status = LifecycleStatus.Brouillon,
            Description = $"Détecté sur {decouvert.HostName} le {DateTimeOffset.Now:dd/MM/yyyy}, "
                        + "non déclaré au référentiel. À compléter et valider avant toute opération."
        };

        db.Components.Add(composant);
        await db.SaveChangesAsync(ct);
        return composant.Id;
    }
}

public sealed class ScanResult
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public int ServersScanned { get; set; }
    public List<UndeclaredComponent> Undeclared { get; } = [];
    public List<UnreachableServer> Unreachable { get; } = [];
    public string? Error { get; init; }

    public bool Succeeded => Error is null;

    /// <summary>
    /// Un serveur injoignable n'est pas un serveur sans service non déclaré :
    /// c'est un serveur dont on ne sait rien. La distinction doit rester
    /// visible, sans quoi un balayage partiel passerait pour un balayage propre.
    /// </summary>
    public bool IsComplete => Unreachable.Count == 0;

    public static ScanResult Failed(string error) => new() { Error = error };
}

public sealed class UndeclaredComponent
{
    public Guid ServerId { get; init; }
    public required string HostName { get; init; }
    public required string ServiceName { get; init; }
    public string? DisplayName { get; init; }
    public required string Status { get; init; }
    public int? ProcessId { get; init; }
    public ComponentRole SuggestedRole { get; init; }

    /// <summary>Un service en fonctionnement non déclaré est le cas le plus préoccupant.</summary>
    public bool IsRunning => string.Equals(Status, "Running", StringComparison.OrdinalIgnoreCase);
}

public sealed record UnreachableServer(string HostName, string Reason);
