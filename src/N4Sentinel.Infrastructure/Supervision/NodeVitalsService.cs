using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Supervision;

/// <summary>
/// Vitalité des nœuds : processeur, mémoire, disques, conformité horaire et
/// mises à jour en attente.
///
/// DEUX CADENCES, ET C'EST DÉLIBÉRÉ.
///
/// Les métriques et la synchronisation horaire se relèvent en moins d'une
/// seconde : elles peuvent être rafraîchies en continu, et elles le sont.
///
/// Les mises à jour Windows, non. L'agent interroge WSUS ou le service en
/// ligne, ce qui prend de trente secondes à deux minutes par serveur. Les
/// afficher comme si elles étaient temps réel serait un mensonge d'interface :
/// l'opérateur croirait voir l'instant présent alors qu'il verrait une mesure
/// d'il y a une heure. Elles portent donc TOUJOURS leur date de relevé, et se
/// rafraîchissent sur demande ou selon une cadence lente.
///
/// Rien n'est écrit en base : ce sont des mesures d'instant, pas un historique.
/// </summary>
public sealed class NodeVitalsService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector,
    UpdateReadingCache cache,
    ILogger<NodeVitalsService> logger)
{
    /// <summary>Au-delà, une mesure de mise à jour est présentée comme périmée.</summary>
    public static readonly TimeSpan FraicheurMisesAJour = TimeSpan.FromHours(6);

    // -----------------------------------------------------------------------
    // Relevé instantané
    // -----------------------------------------------------------------------
    /// <summary>
    /// Relève les métriques et l'état horaire d'un serveur. Appelable à
    /// quelques secondes d'intervalle.
    /// </summary>
    public async Task<NodeVitals> ReadAsync(Guid serverId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var serveur = await db.Servers
            .AsNoTracking()
            .Include(s => s.Environment)
            .FirstOrDefaultAsync(s => s.Id == serverId, ct);

        if (serveur is null)
            return NodeVitals.Injoignable(serverId, "?", "Serveur introuvable au référentiel.");

        var resolution = await targetFactory.CreateAsync(serveur, ct);
        if (!resolution.Succeeded)
            return NodeVitals.Injoignable(serverId, serveur.HostName, resolution.Error!);

        var cible = resolution.Target!;

        // Les deux releves sont lances ensemble : ils ne dependent pas l'un de
        // l'autre, et les enchainer doublerait le temps d'affichage.
        var tacheMetriques = connector.GetLiveMetricsAsync(cible, ct);
        var tacheHorloge = connector.GetTimeSyncAsync(cible, ct);

        await Task.WhenAll(tacheMetriques, tacheHorloge);

        var metriques = await tacheMetriques;
        var horloge = await tacheHorloge;

        if (!metriques.Succeeded)
            return NodeVitals.Injoignable(serverId, serveur.HostName, metriques.Error!);

        var conformite = EvaluerHorloge(
            horloge.Succeeded ? horloge.Value : null,
            metriques.Value?.ClockSkewSeconds,
            serveur.Environment,
            horloge.Succeeded ? null : horloge.Error);

        var misesAJour = cache.Get(serverId);

        return new NodeVitals
        {
            ServerId = serverId,
            HostName = serveur.HostName,
            Reachable = true,
            Metrics = metriques.Value,
            TimeSync = horloge.Succeeded ? horloge.Value : null,
            Clock = conformite,
            Updates = misesAJour,
            MeasuredAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Relève tous les serveurs d'un environnement, en parallèle.</summary>
    public async Task<List<NodeVitals>> ReadEnvironmentAsync(
        Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var ids = await db.Servers
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId)
            .OrderBy(s => s.HostName)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var taches = ids.Select(id => ReadAsync(id, ct));
        var resultats = await Task.WhenAll(taches);

        return resultats.OrderBy(v => v.HostName).ToList();
    }

    // -----------------------------------------------------------------------
    // Mises à jour Windows — cadence lente
    // -----------------------------------------------------------------------
    /// <summary>
    /// Interroge l'agent Windows Update. LENT : de trente secondes à deux
    /// minutes. À n'appeler que sur demande explicite, ou depuis une tâche de
    /// fond à cadence lente.
    /// </summary>
    public async Task<UpdateReading> RefreshUpdatesAsync(Guid serverId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var serveur = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId, ct);
        if (serveur is null)
            return cache.Set(serverId, UpdateReading.EnEchec("Serveur introuvable."));

        var resolution = await targetFactory.CreateAsync(serveur, ct);
        if (!resolution.Succeeded)
            return cache.Set(serverId, UpdateReading.EnEchec(resolution.Error!));

        logger.LogInformation(
            "Interrogation de l'agent Windows Update sur {Hote} — opération lente.", serveur.HostName);

        var resultat = await connector.GetPendingUpdatesAsync(resolution.Target!, ct);

        return cache.Set(serverId, resultat.Succeeded && resultat.Value is not null
            ? UpdateReading.Reussi(resultat.Value)
            : UpdateReading.EnEchec(resultat.Error ?? "Relevé impossible."));
    }

    /// <summary>Dernière mesure connue, sans déclencher de relevé.</summary>
    public UpdateReading? LastUpdateReading(Guid serverId) => cache.Get(serverId);

    // -----------------------------------------------------------------------
    // Conformité horaire
    // -----------------------------------------------------------------------
    /// <summary>
    /// Tranche la conformité de l'horloge.
    ///
    /// TROIS RÉPONSES, PAS DEUX. « Conforme » et « non conforme » ne suffisent
    /// pas : quand la source attendue n'est pas renseignée au référentiel,
    /// l'application peut encore détecter les cas franchement anormaux, mais
    /// elle ne peut pas affirmer que la source EST celle du domaine. Répondre
    /// OK dans ce cas serait affirmer ce qui n'a pas été vérifié.
    /// </summary>
    public static ClockConformity EvaluerHorloge(
        TimeSyncSnapshot? horloge, double? ecart, N4Environment? environnement, string? erreur)
    {
        var tolerance = environnement?.ClockToleranceSeconds ?? 5;
        var attendue = environnement?.ExpectedTimeSource;

        if (horloge is null)
            return new ClockConformity
            {
                State = ClockState.Inconnu,
                Summary = "État de synchronisation non relevé.",
                Detail = erreur ?? "Le service de temps n'a pas pu être interrogé sur ce serveur."
            };

        // 1. Le service ne tourne pas : rien n'est synchronise, quel que soit
        //    le reste de la configuration.
        if (!string.Equals(horloge.ServiceStatus, "Running", StringComparison.OrdinalIgnoreCase))
            return new ClockConformity
            {
                State = ClockState.NonConforme,
                Summary = $"Service de temps {horloge.ServiceStatus}",
                Detail = "Le service W32Time ne tourne pas : l'horloge de ce serveur dérive librement. "
                       + "C'est une cause documentée de statuts N4 trompeurs.",
                Remedy = "Démarrer le service W32Time, puis forcer une resynchronisation."
            };

        // 2. Aucune source : horloge materielle locale ou synchronisation
        //    desactivee. Detectable sans connaitre la source attendue.
        if (horloge.IsUnsynchronised)
            return new ClockConformity
            {
                State = ClockState.NonConforme,
                Summary = "Aucune source de temps",
                Detail = $"Ce serveur ne se synchronise sur rien (mode « {horloge.SyncType ?? "inconnu"} », "
                       + $"source « {horloge.ActualSource ?? "aucune"} »). Son horloge dérivera.",
                Remedy = horloge.IsDomainMember
                    ? "Repasser en synchronisation par la hiérarchie du domaine : w32tm /config /syncfromflags:domhier /update"
                    : "Configurer une source NTP explicite : w32tm /config /syncfromflags:manual /manualpeerlist:\"<serveur>\" /update"
            };

        // 3. L'ecart mesure depasse la tolerance. C'est le constat le plus
        //    direct : quelle que soit la configuration, l'heure est fausse.
        if (ecart is { } e && Math.Abs(e) > tolerance)
            return new ClockConformity
            {
                State = ClockState.NonConforme,
                SkewSeconds = e,
                Summary = $"Écart de {e:0.0} s",
                Detail = $"L'horloge de ce serveur s'écarte de {Math.Abs(e):0.0} seconde(s) de celle du "
                       + $"serveur N4 Sentinel, pour une tolérance de {tolerance} s. "
                       + "Au-delà de ce seuil, N4 produit des statuts DISCONNECTED sans cause apparente.",
                Remedy = "Forcer une resynchronisation : w32tm /resync /force, puis vérifier la source."
            };

        // 4. Source attendue renseignee : on peut trancher.
        if (!string.IsNullOrWhiteSpace(attendue))
        {
            var source = horloge.ActualSource ?? string.Empty;
            var pairs = horloge.ConfiguredPeers ?? string.Empty;

            var correspond = source.Contains(attendue, StringComparison.OrdinalIgnoreCase)
                          || pairs.Contains(attendue, StringComparison.OrdinalIgnoreCase);

            // La hierarchie du domaine vaut correspondance quand la source
            // attendue est le domaine lui-meme : NT5DS ne nomme pas de serveur,
            // il suit le controleur de domaine.
            if (!correspond
                && string.Equals(horloge.SyncType, "NT5DS", StringComparison.OrdinalIgnoreCase)
                && horloge.DomainName is { } domaine
                && (domaine.Contains(attendue, StringComparison.OrdinalIgnoreCase)
                    || attendue.Contains(domaine, StringComparison.OrdinalIgnoreCase)))
                correspond = true;

            if (!correspond)
                return new ClockConformity
                {
                    State = ClockState.NonConforme,
                    SkewSeconds = ecart,
                    Summary = "Source hors domaine",
                    Detail = $"Ce serveur se synchronise sur « {horloge.ActualSource ?? "source inconnue"} », "
                           + $"alors que l'environnement attend « {attendue} ». "
                           + "Deux sources de temps différentes dans un même écosystème finissent par diverger.",
                    Remedy = $"Reconfigurer la source de temps vers « {attendue} »."
                };

            return new ClockConformity
            {
                State = ClockState.Conforme,
                SkewSeconds = ecart,
                Summary = "Conforme au domaine",
                Detail = $"Synchronisé sur « {horloge.ActualSource ?? attendue} »"
                       + (ecart is { } ok ? $", écart de {ok:0.0} s." : ".")
            };
        }

        // 5. Pas de source attendue au referentiel : on constate, on n'affirme
        //    pas. C'est exactement le "a confirmer" du reste du produit.
        return new ClockConformity
        {
            State = ClockState.AConfirmer,
            SkewSeconds = ecart,
            Summary = "Synchronisé, conformité à confirmer",
            Detail = $"Ce serveur se synchronise sur « {horloge.ActualSource ?? "une source non identifiée"} » "
                   + (ecart is { } a ? $"avec un écart de {a:0.0} s. " : ". ")
                   + "Aucune source de temps attendue n'est renseignée pour cet environnement : "
                   + "l'application ne peut pas confirmer qu'il s'agit bien de celle du domaine.",
            Remedy = "Renseigner la source de temps attendue sur la fiche de l'environnement "
                   + "pour obtenir une réponse ferme."
        };
    }
}

/// <summary>
/// Retient la dernière mesure des mises à jour Windows, par serveur.
///
/// Elle vit hors du service parce qu'elle doit survivre aux requêtes : une
/// interrogation de l'agent Windows Update coûte de trente secondes à deux
/// minutes, il est hors de question de la refaire à chaque affichage d'écran.
/// </summary>
public sealed class UpdateReadingCache
{
    private readonly ConcurrentDictionary<Guid, UpdateReading> _lectures = new();

    public UpdateReading? Get(Guid serverId) =>
        _lectures.TryGetValue(serverId, out var lecture) ? lecture : null;

    public UpdateReading Set(Guid serverId, UpdateReading lecture)
    {
        _lectures[serverId] = lecture;
        return lecture;
    }

    public IReadOnlyDictionary<Guid, UpdateReading> All => _lectures;
}

public enum ClockState
{
    Inconnu = 0,
    Conforme = 1,

    /// <summary>Synchronisé, mais on ne peut pas prouver que c'est sur la bonne source.</summary>
    AConfirmer = 2,

    NonConforme = 3
}

public sealed record ClockConformity
{
    public ClockState State { get; init; }
    public double? SkewSeconds { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string? Remedy { get; init; }

    /// <summary>Réponse courte OK/KO, pour un tableau de bord.</summary>
    public string ShortVerdict => State switch
    {
        ClockState.Conforme => "OK",
        ClockState.NonConforme => "KO",
        ClockState.AConfirmer => "?",
        _ => "—"
    };
}

/// <summary>Dernière mesure des mises à jour, avec son âge.</summary>
public sealed record UpdateReading
{
    public UpdateSnapshot? Snapshot { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset MeasuredAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Succeeded => Snapshot is not null;
    public TimeSpan Age => DateTimeOffset.UtcNow - MeasuredAt;

    /// <summary>Vrai si la mesure est trop ancienne pour être présentée sans réserve.</summary>
    public bool IsStale => Age > NodeVitalsService.FraicheurMisesAJour;

    public static UpdateReading Reussi(UpdateSnapshot s) => new() { Snapshot = s };
    public static UpdateReading EnEchec(string erreur) => new() { Error = erreur };
}

/// <summary>Vitalité d'un nœud à un instant donné.</summary>
public sealed record NodeVitals
{
    public Guid ServerId { get; init; }
    public string HostName { get; init; } = string.Empty;
    public bool Reachable { get; init; }
    public string? Error { get; init; }

    public LiveMetrics? Metrics { get; init; }
    public TimeSyncSnapshot? TimeSync { get; init; }
    public ClockConformity? Clock { get; init; }

    /// <summary>Null tant qu'aucun relevé de mises à jour n'a été demandé.</summary>
    public UpdateReading? Updates { get; init; }

    public DateTimeOffset MeasuredAt { get; init; } = DateTimeOffset.UtcNow;

    public static NodeVitals Injoignable(Guid id, string hote, string erreur) =>
        new() { ServerId = id, HostName = hote, Reachable = false, Error = erreur };
}
