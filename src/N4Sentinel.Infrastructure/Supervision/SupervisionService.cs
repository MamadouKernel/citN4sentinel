using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Diagnostic;
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

        // FR-052 : la maintenance déclarée prime sur tout relevé — on ne
        // rapporte pas un échec pendant une intervention planifiée et connue.
        if (component.MaintenanceMode)
        {
            return new ComponentHealthSnapshot
            {
                ComponentId = component.Id,
                LogicalName = component.LogicalName,
                EnvironmentCode = component.Environment?.Code ?? "INCONNU",
                Role = component.Role,
                State = ComponentState.Maintenance,
                Verdict = "Composant déclaré en maintenance."
                        + (component.MaintenanceNote is { Length: > 0 } n ? $" {n}" : string.Empty)
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

        // Depuis quand le processus du service tourne : sert plus bas a
        // distinguer un demarrage recent d'un service up de longue date dont le
        // marqueur de demarrage a ete chasse du journal par la rotation.
        DateTimeOffset? debutProcessus = null;

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
                debutProcessus = svcRes.Value.ProcessStartTime;
            }
            else
            {
                snapshot.ServiceStatus = "Inaccessible";

                // WinRM injoignable. Pour un composant SANS agent mais joignable
                // par un port applicatif (typiquement un cluster SQL : on ne peut
                // pas interroger son service Windows, mais on peut voir si SQL
                // ÉCOUTE sur 1433), on bascule sur un contrôle TCP — le seul
                // signal possible sans agent. Les vitals (CPU/RAM/disque) restent
                // alors indisponibles, et le verdict le dit honnêtement.
                if (component.Port is { } portTcp)
                {
                    var ouvert = await CheckTcpPortAsync(target.HostName, portTcp, ct);
                    snapshot.State = ouvert ? ComponentState.Disponible : ComponentState.Indisponible;
                    snapshot.LogProofStatus = LogProofState.Unprovable;
                    snapshot.Verdict = ouvert
                        ? $"Service en écoute sur le port TCP {portTcp} (agent WinRM indisponible — CPU/RAM/disque non relevés)."
                        : $"Port TCP {portTcp} fermé ou filtré, et agent WinRM indisponible : {svcRes.Error}";
                    return snapshot;
                }

                snapshot.State = ComponentState.Inconnu;
                snapshot.Verdict = $"Échec d'interrogation du service : {svcRes.Error}";
                return snapshot;
            }

            // SERVICES ASSOCIÉS. N4 en démarre plusieurs par composant, et
            // l'éditeur note ce que coûte chacun : sans BridgeService, « XPS
            // cannot complete startup » ; sans XMLRDTService, « ECN4 does not
            // accept XMLRDT messages ». Le service piloté peut donc être
            // Running pendant qu'une fonction entière est morte — et c'est
            // exactement ce que la supervision doit refuser d'afficher comme
            // « disponible ».
            //
            // Ils ne sont interrogés que si le service piloté tourne : quand il
            // est arrêté, ses compagnons le sont aussi et le dire n'apporterait
            // qu'un bruit qui masquerait la cause.
            foreach (var associe in component.CompanionServiceNames
                         .Where(n => !string.IsNullOrWhiteSpace(n))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();

                var res = await connector.GetServiceAsync(target, associe, ct);

                var etat = res.Succeeded && res.Value is not null
                    ? res.Value.Status
                    : "Inaccessible";

                snapshot.CompanionServices.Add(new CompanionServiceState(associe, etat));
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
        //
        // RG-0201 : il SUFFIT qu'un CHEMIN de journal soit configuré. On ne
        // réclame plus un ReadyPattern saisi à la main (IsProvable) : Sentinel
        // lit le journal et DÉDUIT l'état via le catalogue de signatures. Le
        // ReadyPattern, quand il existe (Cluster/Center), reste un signal de
        // plus — pas un prérequis. Un composant sans chemin de journal (base,
        // AMQ, système externe) tombe dans le bloc « sans preuve » plus bas.
        if (!string.IsNullOrWhiteSpace(component.Readiness.LogPath))
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

                // RG-0201 : laisser le JOURNAL déduire l'état, pas seulement le
                // ReadyPattern configuré. On passe le texte au catalogue de
                // signatures de référence (celui tiré de la doc Kaleris) : les
                // signatures reconnues sont jointes au snapshot pour l'affichage,
                // et certaines (échec de démarrage, reprise, Bridge non ACTIVE…)
                // déduisent l'état sans que l'exploitant ait rien à configurer.
                // Les ErrorPatterns configurés ci-dessus restent prioritaires.
                snapshot.LogFindings.AddRange(ReconnaitreSignatures(text, component.Role));

                var etatDeduit = DeduireEtat(snapshot.LogFindings, text);
                if (etatDeduit is { } deduit)
                {
                    snapshot.State = deduit.State;
                    snapshot.LogProofStatus = deduit.Proof;
                    snapshot.MatchedPattern = deduit.Sample;
                    snapshot.Verdict = deduit.Verdict;
                }
                // Évaluation du marqueur de démarrage (logique historique, reprise
                // quand aucune signature décisive n'a tranché l'état).
                else if (FindMatch(text, component.Readiness.ReadyPatterns) is { } readyMatch)
                {
                    snapshot.State = ComponentState.Disponible;
                    snapshot.LogProofStatus = LogProofState.Proved;
                    snapshot.MatchedPattern = readyMatch;
                    snapshot.Verdict = "Composant opérationnel (marqueur d'initialisation confirmé dans le journal).";
                }
                else
                {
                    // Marqueur de demarrage absent du journal lisible. Deux cas
                    // radicalement differents selon depuis quand le service tourne :
                    //   - demarre il y a peu (dans la fenetre de preuve) : il
                    //     initialise encore, on attend legitimement le marqueur
                    //     -> Demarrage (ambre).
                    //   - up depuis bien plus longtemps que la fenetre : le
                    //     marqueur a ete ecrit une fois au demarrage puis chasse
                    //     par la rotation du journal. Un service up depuis des
                    //     jours n'est PAS « en cours de demarrage » -> Disponible,
                    //     dit honnetement (preuve non rejouable, pas preuve absente).
                    var fenetre = TimeSpan.FromSeconds(Math.Max(60, component.Readiness.LogReadyTimeoutSeconds));
                    var age = debutProcessus is { } debut ? DateTimeOffset.UtcNow - debut : (TimeSpan?)null;

                    if (age is { } duree && duree > fenetre)
                    {
                        snapshot.State = ComponentState.Disponible;
                        snapshot.LogProofStatus = LogProofState.Unprovable;
                        snapshot.Verdict = $"Service actif depuis {FormaterDuree(duree)} ; aucune erreur ni signature décisive dans le journal courant — considéré disponible.";
                    }
                    else
                    {
                        snapshot.State = ComponentState.Demarrage;
                        snapshot.LogProofStatus = LogProofState.WaitingForProof;
                        snapshot.Verdict = age is { } d
                            ? $"Service actif depuis {FormaterDuree(d)}, dans la fenêtre d'initialisation — journal lu, aucune preuve de disponibilité encore écrite."
                            : "Service Windows actif, initialisation applicative en cours (journal lu, aucune preuve encore écrite).";
                    }
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
            // Composant sans preuve par le journal (ex: base de données, AMQ ou système externe)
            if (component.Port.HasValue)
            {
                // FR-051 : Contrôle TCP réel si un port est configuré
                bool isPortOpen = await CheckTcpPortAsync(target.HostName, component.Port.Value, ct);
                if (isPortOpen)
                {
                    snapshot.State = ComponentState.Disponible;
                    snapshot.LogProofStatus = LogProofState.Unprovable;
                    snapshot.Verdict = $"Disponible (Port TCP {component.Port.Value} accessible).";
                }
                else
                {
                    snapshot.State = ComponentState.Indisponible;
                    snapshot.Verdict = $"Indisponible (Port TCP {component.Port.Value} fermé ou filtré).";
                }
            }
            else if (string.Equals(snapshot.ServiceStatus, "Running", StringComparison.OrdinalIgnoreCase) || snapshot.ServiceStatus == "NonApplicable")
            {
                snapshot.State = ComponentState.Disponible;
                snapshot.LogProofStatus = LogProofState.Unprovable;
                snapshot.Verdict = "Disponible (statut service Running — aucun marqueur de journal ni port configuré : à confirmer).";
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

        // SERVICES ASSOCIES DEFAILLANTS. Un composant dont le service pilote
        // tourne mais dont un ecouteur est arrete n'est PAS disponible : le
        // guide 3.8.25 le dit sans detour — « a service that is in states other
        // than ACTIVE is as useless as if it was not present ».
        //
        // Degrade et non indisponible : le composant repond encore, mais une
        // partie de ses fonctions est morte. Nommer le service manquant evite
        // la chasse au fantome — sans BridgeService, c'est le demarrage de XPS
        // qui echouera, et personne ne fera le lien.
        if (snapshot.CompanionServicesDefaillants.Count > 0)
        {
            if (snapshot.State == ComponentState.Disponible)
                snapshot.State = ComponentState.Degrade;

            var detail = string.Join(", ",
                snapshot.CompanionServicesDefaillants.Select(s => $"{s.Nom} ({s.Statut})"));

            snapshot.Verdict +=
                $" [ATTENTION : {snapshot.CompanionServicesDefaillants.Count} service(s) associé(s) "
                + $"non actif(s) : {detail}. Le composant répond, mais une partie de ses fonctions "
                + "ne rend plus service.]";
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

        // FR-051 : le heartbeat littéral — dernier relevé de supervision
        // persisté pour ce composant, distinct de « maintenant » (l'instant du
        // relevé courant). Absent tant qu'aucun cycle de fond n'a encore
        // écrit de signal pour ce composant.
        snapshot.LastHeartbeatAt = await db.ComponentSignals
            .AsNoTracking()
            .Where(s => s.ComponentId == componentId)
            .OrderByDescending(s => s.CapturedAt)
            .Select(s => (DateTimeOffset?)s.CapturedAt)
            .FirstOrDefaultAsync(ct);

        return snapshot;
    }

    // Catalogue de référence (doc Kaleris), construit une seule fois. On lit la
    // liste LIVRÉE plutôt que la base : la supervision reste déterministe et sans
    // requête SQL par relevé, et les signatures de statut N4 sont stables. La
    // base administrable sert au diagnostic approfondi (LogAnalysisService).
    private static readonly IReadOnlyList<DiagnosticSignature> CatalogueReference =
        SignatureCatalogue.Livrees();

    // Arrêt applicatif PROPRE. Le catalogue ne documente que l'arrêt INCORRECT
    // (SHUTDOWN-IMPROPER) ; un arrêt propre consigné alors que le service Windows
    // tourne encore signale que l'application s'éteint d'elle-même -> Arret.
    private static readonly List<string> ArretProprePatterns =
    [
        @"shutdown (?:complete|process complete|finished)",
        @"stopped cleanly", @"clean shutdown", @"server stopped",
        @"arr[eê]t (?:propre|termin[eé])"
    ];

    /// <summary>
    /// Reconnaît dans le texte du journal les signatures du catalogue de
    /// référence (au plus une occurrence par code, avec la ligne déclenchante).
    /// Regex bornée par un timeout et expression invalide ignorée, comme
    /// <see cref="FindMatch"/> — le relevé ne doit jamais être cassé par une
    /// signature mal formée.
    /// </summary>
    private static List<LogSignatureFinding> ReconnaitreSignatures(string text, ComponentRole role)
    {
        var findings = new List<LogSignatureFinding>();
        foreach (var sig in CatalogueReference)
        {
            if (sig.AppliesToRole is { } r && r != role) continue;
            if (string.IsNullOrWhiteSpace(sig.Pattern)) continue;
            try
            {
                var m = Regex.Match(text, sig.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                if (!m.Success) continue;
                findings.Add(new LogSignatureFinding(
                    sig.Code, sig.Name, ExtraireLigne(text, m.Index),
                    sig.Severity, sig.Meaning, sig.Remediation, sig.DocumentReference));
            }
            catch (RegexMatchTimeoutException) { /* texte pathologique : signature ignorée */ }
            catch (ArgumentException) { /* expression invalide : signature ignorée */ }
        }
        return findings.OrderByDescending(f => f.Severity).ToList();
    }

    /// <summary>Ligne complète (bornée à 300 car.) contenant la position d'une occurrence.</summary>
    private static string ExtraireLigne(string text, int index)
    {
        var pos = Math.Clamp(index, 0, Math.Max(0, text.Length - 1));
        var debut = text.LastIndexOf('\n', pos) + 1;
        var fin = text.IndexOf('\n', pos);
        if (fin < 0) fin = text.Length;
        var ligne = text[debut..fin].Trim();
        return ligne.Length <= 300 ? ligne : ligne[..299] + "…";
    }

    /// <summary>
    /// Déduit l'état du composant des signatures reconnues, selon la précédence
    /// documentée (RG-0201). Retourne null quand aucune signature décisive n'est
    /// présente : la logique du marqueur de démarrage reprend alors la main.
    /// </summary>
    private static (ComponentState State, LogProofState Proof, string Verdict, string Sample)? DeduireEtat(
        List<LogSignatureFinding> findings, string text)
    {
        LogSignatureFinding? Sig(string code) => findings.FirstOrDefault(f => f.Code == code);

        // (a) Échec d'initialisation caractérisé : le service peut afficher
        // Running, seul le journal dit que rien n'a démarré -> Indisponible.
        if (Sig("APP-STARTUP-FAILED") is { } echec)
            return (ComponentState.Indisponible, LogProofState.ErrorDetected,
                $"Échec d'initialisation applicative détecté dans le journal : « {echec.SampleLine} ».", echec.SampleLine);

        // (b) Arrêt propre consigné alors que le service Windows tourne encore :
        // l'application s'éteint d'elle-même -> Arret.
        if (FindMatch(text, ArretProprePatterns) is { } arret)
            return (ComponentState.Arret, LogProofState.Unprovable,
                $"Arrêt applicatif propre consigné dans le journal : « {arret} » — composant arrêté côté application.", arret);

        // (c) Reprise après incident -> Degrade : le service répond mais se
        // remet d'une erreur ; la cause est à chercher avant cette ligne.
        if (Sig("N4-STATUS-RECOVERING") is { } rec)
            return (ComponentState.Degrade, LogProofState.Proved,
                $"Service en reprise après incident (RECOVERING) : « {rec.SampleLine} ».", rec.SampleLine);

        // (d) Bridge pas encore ACTIVE -> Demarrage : WAITING puis LOADING sont
        // des étapes normales du démarrage du Bridge, pas une panne.
        if (Sig("BRIDGE-NOT-ACTIVE") is { } bridge)
            return (ComponentState.Demarrage, LogProofState.WaitingForProof,
                $"Bridge pas encore ACTIVE (WAITING/LOADING) : « {bridge.SampleLine} ». XPS ne doit pas démarrer avant l'état ACTIVE.", bridge.SampleLine);

        // (e) Battement de cœur perdu depuis plus de deux minutes -> Degrade.
        // DISCONNECTED est volontairement laissé en simple preuve affichée : il
        // est NORMAL pendant un démarrage et déclarerait un faux dégradé.
        if (Sig("N4-STATUS-INACTIVE") is { } inact)
            return (ComponentState.Degrade, LogProofState.Proved,
                $"Nœud INACTIVE — battement de cœur perdu : « {inact.SampleLine} ».", inact.SampleLine);

        return null;
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

    /// <summary>Durée lisible et courte : « 57 j », « 3 h », « 12 min ».</summary>
    private static string FormaterDuree(TimeSpan d) =>
        d.TotalDays >= 1 ? $"{(int)d.TotalDays} j"
        : d.TotalHours >= 1 ? $"{(int)d.TotalHours} h"
        : $"{Math.Max(1, (int)d.TotalMinutes)} min";

    private static async Task<bool> CheckTcpPortAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3)); // Timeout rapide pour ne pas bloquer
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// État d'un service associé, relevé en même temps que le service piloté.
/// </summary>
/// <param name="Nom">Nom du service Windows.</param>
/// <param name="Statut">Statut relevé, ou « Inaccessible ».</param>
public sealed record CompanionServiceState(string Nom, string Statut)
{
    public bool EstActif => string.Equals(Statut, "Running", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// État des services que N4 démarre en plus du service piloté. Vide quand
    /// le composant n'en déclare aucun.
    /// </summary>
    public List<CompanionServiceState> CompanionServices { get; } = [];

    /// <summary>Services associés qui ne tournent pas — ceux qui comptent.</summary>
    public IReadOnlyList<CompanionServiceState> CompanionServicesDefaillants =>
        CompanionServices.Where(s => !s.EstActif).ToList();

    public LogProofState LogProofStatus { get; set; } = LogProofState.Unprovable;
    public string? LogPathResolved { get; set; }
    public string? MatchedPattern { get; set; }

    /// <summary>
    /// RG-0201 : ce que DIT le journal. Signatures du catalogue reconnues dans
    /// le delta lu lors de ce relevé — preuves applicatives affichées à côté des
    /// vitals (CPU/RAM/horloge) et base de la déduction d'état. Vide si rien n'a
    /// été reconnu.
    /// </summary>
    public List<LogSignatureFinding> LogFindings { get; } = [];

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

    /// <summary>FR-051 : heartbeat littéral — horodatage du dernier relevé de supervision persisté pour ce composant.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; set; }

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

/// <summary>
/// Signature du catalogue reconnue dans le journal d'un composant lors d'un
/// relevé LIVE (RG-0201). Immuable : c'est une preuve affichée, pas un état
/// modifiable. <see cref="SampleLine"/> est le texte brut du journal, affiché
/// ENCODÉ côté UI — jamais interprété comme du balisage.
/// </summary>
public sealed record LogSignatureFinding(
    string Code,
    string Name,
    string SampleLine,
    SignatureSeverity Severity,
    string? Meaning,
    string? Remediation,
    string? DocumentReference);

public enum LogProofState
{
    Unprovable = 0,
    WaitingForProof = 1,
    Proved = 2,
    ErrorDetected = 3,
    LogNotFound = 4
}
