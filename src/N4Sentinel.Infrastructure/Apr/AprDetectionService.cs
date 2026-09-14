using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Infrastructure.Apr;

/// <summary>
/// Mini DTO interne pour les métadonnées de composants. On évite le dynamic
/// qui casse l'inférence de type générique entre méthodes anonymes et Dictionary.
/// </summary>
internal sealed class ComposantMeta(Guid id, string logicalName, Guid environmentId, ComponentRole role)
{
    public Guid Id { get; } = id;
    public string LogicalName { get; } = logicalName;
    public Guid EnvironmentId { get; } = environmentId;
    public ComponentRole Role { get; } = role;
}

/// <summary>
/// Moteur de détection APR P2 — Actions Préventives Recommandées.
/// Lit les signaux faibles 15 min glissants (RG-0902 : mini 3 échantillons
/// pour base tendance) et produit des candidats PreventiveRecommendedAction
/// pour chacun des 6 détecteurs couvrant 80% des TOP causes P1 Navis.
/// Pattern : scellé Scoped, retour List<T> vide si aucune APR.
/// </summary>
public sealed class AprDetectionService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    SupervisionStateCache supervisionCache)
{
    public static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(15);
    public const int MinimumSamplesForTrend = 3;

    // --- Seuils progressifs SOUS les bloquants P1 ---
    public const double ClockSkewAprThresholdSeconds = 0.6;
    public const double ClockSkewTrendDeltaPlus = 0.1;
    public const int BridgeQueueAprThreshold = 300;
    public const int BridgeQueueTrendDeltaPlus10Min = 100;
    public const int IndexFragAprThresholdPct = 25;
    public const int DiskFreeAprThresholdPct = 10;
    public const int SnapshotRecentHoursMax = 72;

    // --- Coûts / Risques par famille (heuristiques) ---
    public const int DefaultRisk_HorlogeNtp_Pct = 80;
    public const int DefaultDuration_HorlogeNtp_Min = 2;
    public const int DefaultRisk_BridgeQueue_Pct = 70;
    public const int DefaultDuration_BridgeQueue_Min = 5;
    public const int DefaultRisk_IndexFrag_Pct = 65;
    public const int DefaultDuration_IndexFrag_Min = 30;
    public const int DefaultRisk_DiskFree_Pct = 55;
    public const int DefaultDuration_DiskFree_Min = 10;
    public const int DefaultRisk_ServiceInstable_Pct = 40;
    public const int DefaultDuration_ServiceInstable_Min = 15;
    public const int DefaultRisk_SnapshotRecent_Pct = 75;
    public const int DefaultDuration_SnapshotRecent_Min = 3;

    public async Task<List<PreventiveRecommendedAction>> DetectAllAsync(
        Guid? singleEnvironmentId = null, CancellationToken ct = default)
    {
        var candidates = new List<PreventiveRecommendedAction>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var fenetre = DateTimeOffset.UtcNow.Subtract(TrendWindow);

        IQueryable<N4Component> composantsQuery = db.Components.AsNoTracking();
        if (singleEnvironmentId.HasValue)
            composantsQuery = composantsQuery.Where(c => c.EnvironmentId == singleEnvironmentId.Value);

        var composantsBruts = await composantsQuery
            .Select(c => new { c.Id, c.LogicalName, c.EnvironmentId, c.Role })
            .ToListAsync(ct);

        if (composantsBruts.Count == 0) return candidates;

        var metaParId = composantsBruts
            .ToDictionary(c => c.Id, c => new ComposantMeta(c.Id, c.LogicalName, c.EnvironmentId, c.Role));

        var ids = composantsBruts.Select(c => c.Id).ToList();

        var signaux = await db.ComponentSignals.AsNoTracking()
            .Where(s => ids.Contains(s.ComponentId) && s.CapturedAt >= fenetre)
            .OrderBy(s => s.ComponentId).ThenBy(s => s.CapturedAt)
            .ToListAsync(ct);

        var signauxParComposant = signaux
            .GroupBy(s => s.ComponentId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.CapturedAt).ToList());

        // ============ Détecteur 1 · Horloge NTP tendance ===========================
        candidates.AddRange(DetecterHorlogeNtp(signauxParComposant, metaParId));

        // ============ Détecteur 2 · Queue Bridge tendance =========================
        candidates.AddRange(DetecterQueueBridge(signauxParComposant, metaParId));

        // ============ Détecteur 3 · Fragmentation index ===========================
        candidates.AddRange(DetecterIndexFragmentation(signauxParComposant, metaParId));

        // ============ Détecteur 4 · Espace disque faible ==========================
        candidates.AddRange(DetecterEspaceDisque(signauxParComposant, metaParId));

        // ============ Détecteur 5 · Composants instables (State Degrade/Indisponible)
        candidates.AddRange(DetecterComposantInstable(metaParId, supervisionCache));

        // ============ Détecteur 6 · Snapshot VM < 72h =============================
        candidates.AddRange(DetecterSnapshotRecent(signauxParComposant, metaParId));

        // Post-traitement : PriorityScore + sérialisation JSON
        var now = DateTimeOffset.UtcNow;
        foreach (var apr in candidates)
        {
            apr.GeneratedAt = now;
            apr.PriorityScore = apr.EstimatedP1RiskPct / Math.Max(1, apr.EstimatedDurationMinutes);
            if (apr.RawSignalSnapshot is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(apr.RawSignalSnapshot);
                    apr.RawSignalSnapshot = JsonSerializer.Serialize(
                        doc.RootElement,
                        new JsonSerializerOptions { WriteIndented = false });
                }
                catch (JsonException)
                {
                    // Ignore si JSON déjà bien formé mais non parseable par surcharge
                }
            }
        }

        return candidates;
    }

    // ------------------------------------------------------------------
    // 1 · Horloge NTP
    // ------------------------------------------------------------------
    private List<PreventiveRecommendedAction> DetecterHorlogeNtp(
        Dictionary<Guid, List<ComponentSignal>> signauxParComposant,
        Dictionary<Guid, ComposantMeta> metaParId)
    {
        var resultats = new List<PreventiveRecommendedAction>();

        foreach (var kv in signauxParComposant)
        {
            var captures = kv.Value
                .Where(s => s.SignalType.Contains("horloge", StringComparison.OrdinalIgnoreCase)
                         || s.SignalType.Contains("ClockSkew", StringComparison.OrdinalIgnoreCase)
                         || s.Target.Contains("ClockSkew", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (captures.Count < MinimumSamplesForTrend) continue;

            var valeurs = new List<double>(captures.Count);
            foreach (var c in captures)
            {
                if (double.TryParse(c.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                    valeurs.Add(Math.Abs(v));
            }
            if (valeurs.Count < MinimumSamplesForTrend) continue;

            var maxAbs = valeurs.Max();
            var premier = valeurs[0];
            var dernier = valeurs[^1];
            bool seuilAtteint = maxAbs >= ClockSkewAprThresholdSeconds;
            bool tendanceHausse = (dernier - premier) >= ClockSkewTrendDeltaPlus;
            if (!seuilAtteint && !tendanceHausse) continue;

            var meta = metaParId[kv.Key];
            var evidence = new Dictionary<string, object>
            {
                ["sample_count"] = captures.Count,
                ["max_abs_skew_s"] = Math.Round(maxAbs, 3),
                ["first_skew_s"] = Math.Round(premier, 3),
                ["last_skew_s"] = Math.Round(dernier, 3),
                ["window_min"] = (int)TrendWindow.TotalMinutes,
            };
            resultats.Add(new PreventiveRecommendedAction
            {
                Id = Guid.NewGuid(),
                EnvironmentId = meta.EnvironmentId,
                SourceComponentId = meta.Id,
                Code = "APR-HORLOGE-" + meta.Id.ToString("N")[..8].ToUpperInvariant(),
                Title = $"Décalage d'horloge en hausse sur {meta.LogicalName}",
                Description =
                    $"Le décalage NTP sur {meta.LogicalName} est observé à {Math.Round(maxAbs, 2)} s "
                    + $"(fenêtre {TrendWindow.TotalMinutes} min, {captures.Count} relevés). "
                    + $"Tendance : +{Math.Round(dernier - premier, 3)} s. Risque de split-brain AMQ si on approche 1 s.",
                RootCauseFamily = WorkflowPreCheckCategory.HorlogesNtp,
                EstimatedP1RiskPct = DefaultRisk_HorlogeNtp_Pct,
                EstimatedDurationMinutes = DefaultDuration_HorlogeNtp_Min,
                IsBasedOnTrend = captures.Count >= MinimumSamplesForTrend,
                FirstWeakSignalAt = captures.Min(c => c.CapturedAt),
                EstimatedDeadlineForAction = DateTimeOffset.UtcNow.AddHours(2),
                EvidenceSummary =
                    $"{captures.Count} relevés · max={Math.Round(maxAbs, 2)}s · tendance +{Math.Round(dernier - premier, 3)}s",
                RawSignalSnapshot = JsonSerializer.Serialize(evidence)
            });
        }

        return resultats;
    }

    // ------------------------------------------------------------------
    // 2 · Queue Bridge ActiveMQ / KahaDB
    // ------------------------------------------------------------------
    private List<PreventiveRecommendedAction> DetecterQueueBridge(
        Dictionary<Guid, List<ComponentSignal>> signauxParComposant,
        Dictionary<Guid, ComposantMeta> metaParId)
    {
        var resultats = new List<PreventiveRecommendedAction>();

        foreach (var kv in signauxParComposant)
        {
            var captures = kv.Value
                .Where(s => s.SignalType.Contains("Queue", StringComparison.OrdinalIgnoreCase)
                         || s.SignalType.Contains("queue", StringComparison.OrdinalIgnoreCase)
                         || s.Target.Contains("KahaDB", StringComparison.OrdinalIgnoreCase)
                         || s.Target.Contains("ActiveMQ", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (captures.Count == 0) continue;

            var meta = metaParId[kv.Key];
            if (meta.Role != ComponentRole.BridgeDaemon
                && !meta.LogicalName.Contains("ridge", StringComparison.OrdinalIgnoreCase))
                continue;

            var valeurs = new List<int>(captures.Count);
            foreach (var c in captures)
            {
                if (int.TryParse(c.Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                    valeurs.Add(v);
            }
            if (valeurs.Count == 0) continue;

            var maxQueue = valeurs.Max();
            var premier = valeurs[0];
            var dernier = valeurs[^1];
            bool seuilAtteint = maxQueue >= BridgeQueueAprThreshold;
            bool tendance = valeurs.Count >= 2 && (dernier - premier >= BridgeQueueTrendDeltaPlus10Min);
            if (!seuilAtteint && !tendance) continue;

            var echantillons = new Dictionary<string, object>
            {
                ["sample_count"] = valeurs.Count,
                ["max_messages"] = maxQueue,
                ["first"] = premier,
                ["last"] = dernier,
                ["window_min"] = (int)TrendWindow.TotalMinutes,
            };

            resultats.Add(new PreventiveRecommendedAction
            {
                Id = Guid.NewGuid(),
                EnvironmentId = meta.EnvironmentId,
                SourceComponentId = meta.Id,
                Code = "APR-BRIDGEQ-" + meta.Id.ToString("N")[..8].ToUpperInvariant(),
                Title = $"Queue Bridge en croissance sur {meta.LogicalName}",
                Description =
                    $"Le Bridge {meta.LogicalName} accumule {maxQueue} messages en attente "
                    + $"({valeurs.Count} relevés). Tendance sur {TrendWindow.TotalMinutes} min : "
                    + $"+{dernier - premier} messages. Risque de perte en cas de redémarrage prématuré.",
                RootCauseFamily = WorkflowPreCheckCategory.EtatFilesBridge,
                EstimatedP1RiskPct = DefaultRisk_BridgeQueue_Pct,
                EstimatedDurationMinutes = DefaultDuration_BridgeQueue_Min,
                IsBasedOnTrend = valeurs.Count >= MinimumSamplesForTrend,
                FirstWeakSignalAt = captures.Min(c => c.CapturedAt),
                EstimatedDeadlineForAction = DateTimeOffset.UtcNow.AddHours(8),
                EvidenceSummary = $"{valeurs.Count} relevés · max={maxQueue} msg · tendance +{dernier - premier}",
                RawSignalSnapshot = JsonSerializer.Serialize(echantillons)
            });
        }

        return resultats;
    }

    // ------------------------------------------------------------------
    // 3 · Fragmentation d'index SGBD
    // ------------------------------------------------------------------
    private List<PreventiveRecommendedAction> DetecterIndexFragmentation(
        Dictionary<Guid, List<ComponentSignal>> signauxParComposant,
        Dictionary<Guid, ComposantMeta> metaParId)
    {
        var resultats = new List<PreventiveRecommendedAction>();

        foreach (var kv in signauxParComposant)
        {
            var captures = kv.Value
                .Where(s => s.SignalType.Contains("frag", StringComparison.OrdinalIgnoreCase)
                         || s.SignalType.Contains("index", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (captures.Count == 0) continue;

            foreach (var c in captures)
            {
                if (!int.TryParse(c.Value, out var pct)) continue;
                if (pct < IndexFragAprThresholdPct) continue;

                var meta = metaParId[kv.Key];
                resultats.Add(new PreventiveRecommendedAction
                {
                    Id = Guid.NewGuid(),
                    EnvironmentId = meta.EnvironmentId,
                    SourceComponentId = meta.Id,
                    Code = "APR-INDEX-" + meta.Id.ToString("N")[..8].ToUpperInvariant(),
                    Title = $"Fragmentation index élevée ({pct}%) sur {meta.LogicalName}",
                    Description =
                        $"Un index sur {meta.LogicalName} a {pct}% de fragmentation "
                        + $"(seuil APR {IndexFragAprThresholdPct}%). "
                        + $"Les requêtes EDI/SQL plan Navis peuvent ralentir de 3-10×.",
                    RootCauseFamily = WorkflowPreCheckCategory.GestionDonneesEtIndex,
                    EstimatedP1RiskPct = DefaultRisk_IndexFrag_Pct,
                    EstimatedDurationMinutes = DefaultDuration_IndexFrag_Min,
                    IsBasedOnTrend = captures.Count >= MinimumSamplesForTrend,
                    FirstWeakSignalAt = captures.Min(cc => cc.CapturedAt),
                    EstimatedDeadlineForAction = DateTimeOffset.UtcNow.AddDays(3),
                    EvidenceSummary =
                        $"Signal {c.SignalType} / {c.Target} · frag={pct}% · seuil={IndexFragAprThresholdPct}%",
                    RawSignalSnapshot = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["signal_type"] = c.SignalType,
                        ["target"] = c.Target,
                        ["frag_pct"] = pct,
                        ["threshold_pct"] = IndexFragAprThresholdPct,
                    })
                });
            }
        }

        return resultats;
    }

    // ------------------------------------------------------------------
    // 4 · Espace disque < 10 % libre
    // ------------------------------------------------------------------
    private List<PreventiveRecommendedAction> DetecterEspaceDisque(
        Dictionary<Guid, List<ComponentSignal>> signauxParComposant,
        Dictionary<Guid, ComposantMeta> metaParId)
    {
        var resultats = new List<PreventiveRecommendedAction>();

        foreach (var kv in signauxParComposant)
        {
            var captures = kv.Value
                .Where(s => s.SignalType.Contains("Disk", StringComparison.OrdinalIgnoreCase)
                         || s.SignalType.Contains("disk", StringComparison.OrdinalIgnoreCase)
                         || s.SignalType.Contains("Espace", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (captures.Count == 0) continue;

            foreach (var c in captures)
            {
                if (!int.TryParse(c.Value, out var freePct)) continue;
                if (freePct >= DiskFreeAprThresholdPct) continue;

                var meta = metaParId[kv.Key];
                resultats.Add(new PreventiveRecommendedAction
                {
                    Id = Guid.NewGuid(),
                    EnvironmentId = meta.EnvironmentId,
                    SourceComponentId = meta.Id,
                    Code = "APR-DISK-" + meta.Id.ToString("N")[..8].ToUpperInvariant(),
                    Title = $"Espace disque critique ({freePct}% libre) sur {meta.LogicalName}",
                    Description =
                        $"Volume sur {meta.LogicalName} n'a plus que {freePct}% d'espace libre "
                        + $"(seuil APR {DiskFreeAprThresholdPct}%). Risque immédiat: "
                        + $"KahaDB cesse d'écrire, logs Navis-bridged.log ne tournent plus.",
                    RootCauseFamily = WorkflowPreCheckCategory.GestionDonneesEtIndex,
                    EstimatedP1RiskPct = DefaultRisk_DiskFree_Pct,
                    EstimatedDurationMinutes = DefaultDuration_DiskFree_Min,
                    IsBasedOnTrend = captures.Count >= MinimumSamplesForTrend,
                    FirstWeakSignalAt = captures.Min(cc => cc.CapturedAt),
                    EstimatedDeadlineForAction = DateTimeOffset.UtcNow.AddHours(24),
                    EvidenceSummary = $"{c.Target} · libre={freePct}% · seuil={DiskFreeAprThresholdPct}%",
                    RawSignalSnapshot = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["target"] = c.Target,
                        ["free_pct"] = freePct,
                        ["threshold_pct"] = DiskFreeAprThresholdPct,
                    })
                });
            }
        }

        return resultats;
    }

    // ------------------------------------------------------------------
    // 5 · Composants dégradés / indisponibles depuis Supervision
    // ------------------------------------------------------------------
    private static List<PreventiveRecommendedAction> DetecterComposantInstable(
        Dictionary<Guid, ComposantMeta> metaParId,
        SupervisionStateCache cache)
    {
        var resultats = new List<PreventiveRecommendedAction>();
        var comparer = StringComparer.OrdinalIgnoreCase;

        foreach (var kv in metaParId)
        {
            var snap = cache.GetSnapshot(kv.Key);
            if (snap is null) continue;

            bool estDegrade = snap.State == ComponentState.Degrade;
            bool estIndispo = snap.State == ComponentState.Indisponible;
            if (!estDegrade && !estIndispo) continue;

            var verdict = snap.Verdict ?? string.Empty;
            var meta = kv.Value;

            WorkflowPreCheckCategory famille =
                verdict.Contains("réseau", StringComparison.OrdinalIgnoreCase)
                || verdict.Contains("network", StringComparison.OrdinalIgnoreCase)
                || verdict.Contains("connect", StringComparison.OrdinalIgnoreCase)
                    ? WorkflowPreCheckCategory.ConnectiviteReseau
                    : WorkflowPreCheckCategory.CompatibiliteTiers;

            resultats.Add(new PreventiveRecommendedAction
            {
                Id = Guid.NewGuid(),
                EnvironmentId = meta.EnvironmentId,
                SourceComponentId = meta.Id,
                Code = "APR-INSTABLE-" + meta.Id.ToString("N")[..8].ToUpperInvariant(),
                Title = $"Instabilité persistante sur {meta.LogicalName} ({snap.State})",
                Description =
                    $"Le composant {meta.LogicalName} est observé en état {snap.State} "
                    + $"sur le relevé de supervision courant. Diagnostic recommandé avant "
                    + "prochaine opération d'arrêt/démarrage.",
                RootCauseFamily = famille,
                EstimatedP1RiskPct = DefaultRisk_ServiceInstable_Pct,
                EstimatedDurationMinutes = DefaultDuration_ServiceInstable_Min,
                IsBasedOnTrend = false,
                FirstWeakSignalAt = snap.LastHeartbeatAt ?? DateTimeOffset.UtcNow.AddMinutes(-10),
                EstimatedDeadlineForAction = DateTimeOffset.UtcNow.AddHours(48),
                EvidenceSummary = $"ComponentState={snap.State} · verdict={snap.Verdict}",
                RawSignalSnapshot = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["component_state"] = snap.State.ToString(),
                    ["verdict"] = snap.Verdict ?? string.Empty,
                    ["last_heartbeat"] = snap.LastHeartbeatAt?.ToString("u") ?? string.Empty,
                })
            });
        }

        return resultats;
    }

    // ------------------------------------------------------------------
    // 6 · Snapshot VM < 72 heures
    // ------------------------------------------------------------------
    private List<PreventiveRecommendedAction> DetecterSnapshotRecent(
        Dictionary<Guid, List<ComponentSignal>> signauxParComposant,
        Dictionary<Guid, ComposantMeta> metaParId)
    {
        var resultats = new List<PreventiveRecommendedAction>();
        var maintenant = DateTimeOffset.UtcNow;

        foreach (var kv in signauxParComposant)
        {
            var captures = kv.Value
                .Where(s => s.Target.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
                         || s.SignalType.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.CapturedAt)
                .ToList();
            if (captures.Count == 0) continue;

            var dernier = captures[0];
            var heuresDepuis = (maintenant - dernier.CapturedAt).TotalHours;
            if (heuresDepuis >= SnapshotRecentHoursMax) continue;

            var meta = metaParId[kv.Key];
            resultats.Add(new PreventiveRecommendedAction
            {
                Id = Guid.NewGuid(),
                EnvironmentId = meta.EnvironmentId,
                SourceComponentId = meta.Id,
                Code = "APR-SNAP-" + meta.Id.ToString("N")[..8].ToUpperInvariant(),
                Title = $"Snapshot VM détecté il y a {Math.Round(heuresDepuis, 1)} h sur {meta.LogicalName}",
                Description =
                    $"Un snapshot Hyper-V / VMware date de seulement {Math.Round(heuresDepuis, 1)} heures sur "
                    + $"{meta.LogicalName} (seuil APR {SnapshotRecentHoursMax} h). Un snapshot récent ralentit "
                    + "les I/O disque de 20-50% et fige temporairement la base AMQ/KahaDB.",
                RootCauseFamily = WorkflowPreCheckCategory.SnapshotVirtuel,
                EstimatedP1RiskPct = DefaultRisk_SnapshotRecent_Pct,
                EstimatedDurationMinutes = DefaultDuration_SnapshotRecent_Min,
                IsBasedOnTrend = captures.Count >= MinimumSamplesForTrend,
                FirstWeakSignalAt = dernier.CapturedAt,
                EstimatedDeadlineForAction = DateTimeOffset.UtcNow.AddHours(24),
                EvidenceSummary =
                    $"Dernier snapshot : {dernier.CapturedAt:u} · il y a {Math.Round(heuresDepuis, 1)} h",
                RawSignalSnapshot = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["captured_at"] = dernier.CapturedAt,
                    ["hours_ago"] = Math.Round(heuresDepuis, 2),
                    ["threshold_hours"] = SnapshotRecentHoursMax,
                    ["signal_type"] = dernier.SignalType,
                    ["target"] = dernier.Target,
                })
            });
        }

        return resultats;
    }
}
