using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Supervision;

/// <summary>
/// Moteur de supervision de l'état réel des composants et des serveurs (FR-052).
///
/// APPLIQUE LE PRINCIPE DIRECTEUR : "Un service Windows Running ne prouve rien".
/// La preuve de disponibilité repose sur la combinaison de 4 signaux :
///   1. Statut du service Windows et du processus
///   2. Joignabilité du port applicatif
///   3. Marqueur de démarrage dans le journal applicatif (ReadinessProfile)
///   4. Métriques système (écart d'horloge < tolérance de l'environnement, espace disque > 10%)
/// </summary>
public sealed class SupervisionService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector,
    ILogger<SupervisionService> logger)
{
    /// <summary>
    /// Évalue l'état réel d'un composant.
    ///
    /// <paramref name="previous"/> est l'instantané précédent, si l'appelant
    /// en a un (le cache de supervision, typiquement). Sert uniquement au
    /// suivi de synchronisation N4-XPS (FR-056) : un relevé ne lit qu'un
    /// DELTA de journal, donc un échange normal peut être confirmé à un
    /// passage puis absent du delta suivant sans que la synchronisation ait
    /// réellement cessé — la dernière confirmation doit survivre d'un
    /// relevé à l'autre, pas être perdue faute de la revoir à chaque fois.
    /// </summary>
    public async Task<ComponentHealthSnapshot> EvaluateComponentAsync(
        Guid componentId, CancellationToken ct = default, ComponentHealthSnapshot? previous = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var component = await db.Components
            .AsNoTracking()
            .Include(c => c.Server)
            .Include(c => c.Environment)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct);

        if (component is null)
            return ComponentHealthSnapshot.Unknown(componentId, "Composant introuvable.");

        logger.LogDebug("Évaluation de la santé du composant {Composant} ({Env})", component.LogicalName, component.Environment?.Code);

        if (component.ControlMode == ControlMode.NonSupervise)
        {
            return new ComponentHealthSnapshot
            {
                ComponentId = component.Id,
                LogicalName = component.LogicalName,
                EnvironmentCode = component.Environment?.Code ?? "INCONNU",
                Role = component.Role,
                State = ComponentState.NonSupervise,
                Verdict = "Composant non supervisé (déclaré uniquement pour ses dépendances)."
            };
        }

        if (component.Server is null)
        {
            return new ComponentHealthSnapshot
            {
                ComponentId = component.Id,
                LogicalName = component.LogicalName,
                EnvironmentCode = component.Environment?.Code ?? "INCONNU",
                Role = component.Role,
                State = ComponentState.Inconnu,
                Verdict = "Aucun serveur associatif rattaché à ce composant."
            };
        }

        var resolution = await targetFactory.CreateAsync(component.Server, ct);
        if (!resolution.Succeeded)
        {
            return new ComponentHealthSnapshot
            {
                ComponentId = component.Id,
                LogicalName = component.LogicalName,
                EnvironmentCode = component.Environment?.Code ?? "INCONNU",
                HostName = component.Server.HostName,
                Role = component.Role,
                State = ComponentState.Inconnu,
                Verdict = $"Impossible de joindre le serveur : {resolution.Error}"
            };
        }

        var target = resolution.Target!;
        var snapshot = new ComponentHealthSnapshot
        {
            ComponentId = component.Id,
            LogicalName = component.LogicalName,
            EnvironmentCode = component.Environment?.Code ?? "INCONNU",
            HostName = component.Server.HostName,
            Role = component.Role,
            StartOrder = component.StartOrder,
            WindowsServiceName = component.WindowsServiceName
        };

        // 1. Contrôle du Service Windows
        if (!string.IsNullOrWhiteSpace(component.WindowsServiceName))
        {
            var svcRes = await connector.GetServiceAsync(target, component.WindowsServiceName, ct);

            // FR-057/058 : le temps de ce premier aller-retour est le signal de
            // lenteur le moins cher qui soit — il est deja mesure par le
            // connecteur pour chaque appel, jusqu'ici jete apres usage.
            snapshot.ResponseTimeMs = svcRes.Duration.TotalMilliseconds;

            if (svcRes.Succeeded && svcRes.Value is not null)
            {
                snapshot.ServiceStatus = svcRes.Value.Status;
                snapshot.ProcessId = svcRes.Value.ProcessId;
                snapshot.WorkingSetBytes = svcRes.Value.WorkingSetBytes;
            }
            else
            {
                snapshot.ServiceStatus = "Inaccessible";
                snapshot.State = ComponentState.Inconnu;
                snapshot.Verdict = $"Échec d'interrogation du service : {svcRes.Error}";
                return snapshot;
            }
        }
        else
        {
            snapshot.ServiceStatus = "NonApplicable";
        }

        // Si le service est à l'arrêt ou en erreur
        if (string.Equals(snapshot.ServiceStatus, "Stopped", StringComparison.OrdinalIgnoreCase))
        {
            snapshot.State = ComponentState.Arret;
            snapshot.Verdict = "Le service Windows est à l'arrêt.";
            return snapshot;
        }

        if (string.Equals(snapshot.ServiceStatus, "StopPending", StringComparison.OrdinalIgnoreCase))
        {
            snapshot.State = ComponentState.Degrade;
            snapshot.Verdict = "Le service est bloqué en cours d'arrêt (StopPending).";
            return snapshot;
        }

        if (string.Equals(snapshot.ServiceStatus, "Introuvable", StringComparison.OrdinalIgnoreCase))
        {
            snapshot.State = ComponentState.Indisponible;
            snapshot.Verdict = $"Le service Windows '{component.WindowsServiceName}' n'existe pas sur {target.HostName}.";
            return snapshot;
        }

        // 2. Preuve applicative (Readiness Log Proof)
        if (component.Readiness.IsProvable)
        {
            var logPath = component.Readiness.LogPath!;
            var deltaRes = await connector.ReadLogDeltaAsync(target, logPath, 0, 262_144, ct);

            if (deltaRes.Succeeded && deltaRes.Value is { Exists: true })
            {
                var text = deltaRes.Value.Text;
                snapshot.LogPathResolved = deltaRes.Value.ResolvedPath;

                // Évaluation des motifs d'erreur d'abord
                var errorMatch = FindMatch(text, component.Readiness.ErrorPatterns);
                if (errorMatch is not null)
                {
                    snapshot.State = ComponentState.Indisponible;
                    snapshot.LogProofStatus = LogProofState.ErrorDetected;
                    snapshot.MatchedPattern = errorMatch;
                    snapshot.Verdict = $"Erreur caractérisée détectée dans le journal : « {errorMatch} »";
                    return snapshot;
                }

                // Évaluation du marqueur de démarrage
                var readyMatch = FindMatch(text, component.Readiness.ReadyPatterns);
                if (readyMatch is not null)
                {
                    snapshot.State = ComponentState.Disponible;
                    snapshot.LogProofStatus = LogProofState.Proved;
                    snapshot.MatchedPattern = readyMatch;
                    snapshot.Verdict = "Composant opérationnel (marqueur d'initialisation confirmé dans le journal).";
                }
                else
                {
                    // Le service tourne mais la JVM n'a pas encore écrit son marqueur
                    snapshot.State = ComponentState.Demarrage;
                    snapshot.LogProofStatus = LogProofState.WaitingForProof;
                    snapshot.Verdict = "Service Windows actif, initialisation applicative en cours (attente du marqueur de journal).";
                }

                // FR-032/033 : le service demarre ne dit pas qui detient le role
                // actif sur un cluster Center/Standby. Cherche dans le meme
                // journal, independamment du marqueur d'initialisation ci-dessus.
                if (component.Role is ComponentRole.CenterNode or ComponentRole.StandbyCenterNode
                    && component.Readiness.ActiveRolePatterns.Count > 0)
                {
                    snapshot.HoldsActiveRole = FindMatch(text, component.Readiness.ActiveRolePatterns) is not null;
                }

                // FR-056 : un echange N4-XPS normal vu dans CE delta vaut
                // confirmation fraiche. Absent de ce delta ne veut pas dire
                // absent tout court — le report de la derniere confirmation
                // connue se fait plus bas, une fois ce bloc termine.
                if (component.Role is ComponentRole.BridgeDaemon or ComponentRole.Xps
                    && component.Readiness.SyncPatterns.Count > 0
                    && FindMatch(text, component.Readiness.SyncPatterns) is not null)
                {
                    snapshot.LastSyncConfirmedAt = DateTimeOffset.UtcNow;
                }
            }
            else
            {
                snapshot.State = ComponentState.Degrade;
                snapshot.LogProofStatus = LogProofState.LogNotFound;
                snapshot.Verdict = $"Service actif, mais impossible de lire le journal à l'emplacement « {logPath} ».";
            }
        }
        else
        {
            // Composant sans preuve par le journal (ex: base de données ou composant à marqueur non renseigné)
            if (string.Equals(snapshot.ServiceStatus, "Running", StringComparison.OrdinalIgnoreCase) || snapshot.ServiceStatus == "NonApplicable")
            {
                snapshot.State = ComponentState.Disponible;
                snapshot.LogProofStatus = LogProofState.Unprovable;
                snapshot.Verdict = "Disponible (statut service Running — aucun marqueur de journal configuré : à confirmer).";
            }
            else
            {
                snapshot.State = ComponentState.Inconnu;
                snapshot.Verdict = "État indéterminé.";
            }
        }

        // FR-056 (suite) : reporte la derniere confirmation connue si ce
        // relevé n'en a pas trouvé de nouvelle, puis juge le retard — apres
        // le bloc de lecture de journal quel que soit son issue, pour ne
        // jamais perdre l'information faute d'avoir pu relire le journal.
        if (component.Role is ComponentRole.BridgeDaemon or ComponentRole.Xps
            && component.Readiness.SyncPatterns.Count > 0)
        {
            snapshot.LastSyncConfirmedAt ??= previous?.LastSyncConfirmedAt;

            if (snapshot.LastSyncConfirmedAt is { } confirmeLe)
            {
                var seuil = TimeSpan.FromMinutes(component.Readiness.SyncDelayThresholdMinutes);
                snapshot.SyncDelayed = DateTimeOffset.UtcNow - confirmeLe > seuil;
            }
        }

        // 3. Métriques système (Contrôle d'écart d'horloge et espace disque)
        var sysRes = await connector.GetSystemAsync(target, ct);
        if (sysRes.Succeeded && sysRes.Value is not null)
        {
            snapshot.ClockSkewSeconds = sysRes.Value.ClockSkewSeconds;

            var tolerance = component.Environment?.ClockToleranceSeconds ?? 1;
            if (sysRes.Value.ClockSkewSeconds.HasValue && Math.Abs(sysRes.Value.ClockSkewSeconds.Value) > tolerance)
            {
                if (snapshot.State == ComponentState.Disponible)
                    snapshot.State = ComponentState.Degrade;

                snapshot.Verdict += $" [ATTENTION : Écart d'horloge de {sysRes.Value.ClockSkewSeconds.Value:0.0}s avec le serveur Sentinel]";
            }

            // Espace disque (FR-054) : le plus critique des disques du
            // serveur, meme seuil que le pre-check (< 10 % libre) pour que
            // supervision continue et pre-check disent la meme chose.
            var pireDisque = sysRes.Value.Disks
                .OrderBy(d => d.FreePercent)
                .FirstOrDefault();

            if (pireDisque is not null)
            {
                snapshot.DiskFreePercentMin = pireDisque.FreePercent;
                snapshot.DiskCriticalDrive = pireDisque.Drive;

                if (pireDisque.FreePercent < 10)
                {
                    if (snapshot.State == ComponentState.Disponible)
                        snapshot.State = ComponentState.Degrade;

                    snapshot.Verdict += $" [ATTENTION : disque {pireDisque.Drive} à {pireDisque.FreePercent:0.0}% libre]";
                }
            }
        }

        return snapshot;
    }

    private static string? FindMatch(string text, List<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                if (match.Success) return match.Value;
            }
            catch (Exception)
            {
                // Expression invalide ou timeout ignoré
            }
        }
        return null;
    }
}

/// <summary>Instantané de santé d'un composant.</summary>
public sealed class ComponentHealthSnapshot
{
    public Guid ComponentId { get; init; }
    public string LogicalName { get; init; } = string.Empty;
    public string EnvironmentCode { get; init; } = string.Empty;
    public string HostName { get; init; } = string.Empty;
    public ComponentRole Role { get; init; }
    public int StartOrder { get; init; }
    public string? WindowsServiceName { get; init; }

    public ComponentState State { get; set; } = ComponentState.Inconnu;
    public string ServiceStatus { get; set; } = "Inconnu";
    public int? ProcessId { get; set; }
    public long? WorkingSetBytes { get; set; }

    public LogProofState LogProofStatus { get; set; } = LogProofState.Unprovable;
    public string? LogPathResolved { get; set; }
    public string? MatchedPattern { get; set; }

    /// <summary>
    /// FR-032/033. Null : non applicable (composant hors Center/Standby) ou
    /// non prouvable (aucun marqueur de role configure) — jamais suppose.
    /// Vrai/faux seulement quand un marqueur d'ActiveRolePatterns a ete
    /// cherche dans le journal.
    /// </summary>
    public bool? HoldsActiveRole { get; set; }

    /// <summary>
    /// FR-057/058 : temps de l'aller-retour d'interrogation du service —
    /// le signal de lenteur le moins cher, déjà mesuré par le connecteur pour
    /// chaque appel. Un nœud sous tension répond mesurablement plus lentement
    /// avant même qu'une alerte d'état ne se déclenche.
    /// </summary>
    public double? ResponseTimeMs { get; set; }

    /// <summary>FR-056 : dernière confirmation d'échange normal N4-XPS trouvée dans le journal, si un marqueur est configuré.</summary>
    public DateTimeOffset? LastSyncConfirmedAt { get; set; }

    /// <summary>
    /// FR-056. Null : non applicable (hors Bridge/XPS) ou non prouvable
    /// (aucun marqueur configuré). Vrai si le dernier échange confirmé
    /// dépasse le délai de retard toléré.
    /// </summary>
    public bool? SyncDelayed { get; set; }

    public double? ClockSkewSeconds { get; set; }

    /// <summary>
    /// FR-054/AC-15 : pourcentage libre du disque le plus critique du serveur
    /// hébergeant ce composant. Null tant qu'aucun relevé système n'a réussi —
    /// jamais supposé sain par défaut.
    /// </summary>
    public double? DiskFreePercentMin { get; set; }
    public string? DiskCriticalDrive { get; set; }

    public string Verdict { get; set; } = string.Empty;
    public DateTimeOffset EvaluatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static ComponentHealthSnapshot Unknown(Guid componentId, string error) => new()
    {
        ComponentId = componentId,
        State = ComponentState.Inconnu,
        Verdict = error
    };
}

public enum LogProofState
{
    Unprovable = 0,
    WaitingForProof = 1,
    Proved = 2,
    ErrorDetected = 3,
    LogNotFound = 4
}
