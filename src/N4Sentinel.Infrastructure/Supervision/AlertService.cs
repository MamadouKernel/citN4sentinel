using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Supervision;

/// <summary>
/// Ouvre, met à jour et résout les alertes de supervision (FR-054).
///
/// Trois règles gouvernent ce service, et elles comptent plus que la liste des
/// conditions détectées :
///
/// 1. UNE CONDITION, UNE ALERTE. La collecte tourne toutes les trente
///    secondes. Créer une alerte à chaque passage rendrait la liste illisible
///    en une heure et garantirait que plus personne ne la consulte.
///
/// 2. RÉSOLUTION AUTOMATIQUE. Une alerte dont la condition a cessé se ferme
///    seule. Exiger un acquittement manuel pour refermer ce qui est déjà
///    réglé produit une liste encombrée d'alertes mortes, et l'opérateur finit
///    par tout acquitter sans lire.
///
/// 3. AUCUNE ALERTE SANS CONDUITE À TENIR. Une alerte qui signale un problème
///    sans dire quoi vérifier reporte le travail sur celui qui la lit.
/// </summary>
public sealed class AlertService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<AlertService> logger)
{
    /// <summary>Au-delà d'une seconde, N4 produit des statuts DISCONNECTED trompeurs.</summary>
    private const double SeuilEcartHorlogeSecondes = 1.0;

    /// <summary>
    /// Confronte un instantané de santé aux conditions d'alerte, puis ouvre,
    /// met à jour ou résout ce qui doit l'être.
    /// </summary>
    public async Task EvaluateAsync(
        ComponentHealthSnapshot snapshot, Guid environmentId, CancellationToken ct = default)
    {
        var conditions = Detecter(snapshot).ToList();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existantes = await db.Alerts
            .Where(a => a.ComponentId == snapshot.ComponentId
                        && (a.Status == AlertStatus.Ouverte || a.Status == AlertStatus.Acquittee))
            .ToListAsync(ct);

        var signaturesActives = conditions.Select(c => c.Signature).ToHashSet();

        // --- Conditions vraies : ouvrir ou mettre a jour ---------------------
        foreach (var condition in conditions)
        {
            var ouverte = existantes.FirstOrDefault(a => a.Signature == condition.Signature);

            if (ouverte is null)
            {
                db.Alerts.Add(new Alert
                {
                    Signature = condition.Signature,
                    EnvironmentId = environmentId,
                    ComponentId = snapshot.ComponentId,
                    ComponentName = snapshot.LogicalName,
                    Kind = condition.Kind,
                    Severity = condition.Severity,
                    Title = condition.Title,
                    Detail = condition.Detail,
                    Recommendation = condition.Recommendation
                });

                logger.LogWarning(
                    "Alerte ouverte — {Composant} ({Env}) : {Titre}",
                    snapshot.LogicalName, snapshot.EnvironmentCode, condition.Title);
            }
            else
            {
                // La condition dure : on n'ouvre rien de neuf, on enregistre
                // qu'elle a ete revue. Le detail est rafraichi, un ecart
                // d'horloge ou un pourcentage de disque ayant pu evoluer.
                ouverte.LastOccurredAt = DateTimeOffset.UtcNow;
                ouverte.OccurrenceCount++;
                ouverte.Detail = condition.Detail;
                ouverte.Severity = condition.Severity;
            }
        }

        // --- Conditions disparues : resolution automatique -------------------
        foreach (var obsolete in existantes.Where(a => !signaturesActives.Contains(a.Signature)))
        {
            obsolete.Status = AlertStatus.Resolue;
            obsolete.ResolvedAt = DateTimeOffset.UtcNow;

            logger.LogInformation(
                "Alerte résolue — {Composant} : {Titre} (durée {Duree})",
                snapshot.LogicalName, obsolete.Title, obsolete.Duration);
        }

        await db.SaveChangesAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Détection
    // -----------------------------------------------------------------------
    private static IEnumerable<AlertCondition> Detecter(ComponentHealthSnapshot s)
    {
        // 1. Incoherence d'etat. La condition la plus importante : le service
        //    tourne, mais le journal dit qu'il a echoue. Un composant dans cet
        //    etat donne l'illusion du fonctionnement, ce qui est pire qu'une
        //    panne franche - on ne le redemarre pas, et on cherche la cause
        //    ailleurs pendant des heures.
        if (s.LogProofStatus == LogProofState.ErrorDetected)
        {
            yield return new AlertCondition(
                AlertKind.IncoherenceEtat, AlertSeverity.Critique,
                $"{s.LogicalName} : le service tourne mais son journal signale un échec",
                $"Service Windows « {s.ServiceStatus} », alors qu'une signature d'échec a été relevée "
                + $"dans {s.LogPathResolved}. L'état affiché ailleurs est trompeur.",
                "Lisez le journal autour de l'erreur signalée avant toute action. Ne redémarrez pas "
                + "en boucle : traitez la cause. Si la signature évoque une corruption ActiveMQ ou KahaDB, "
                + "appliquez la procédure de reconstitution, pas un simple redémarrage.",
                s.ComponentId);
        }

        // 2. Indisponibilite franche.
        if (s.State == ComponentState.Indisponible)
        {
            yield return new AlertCondition(
                AlertKind.Indisponibilite, AlertSeverity.Critique,
                $"{s.LogicalName} est indisponible",
                $"État consolidé « Indisponible ». Service Windows : {s.ServiceStatus}. {s.Verdict}",
                "Vérifiez que le serveur répond, puis l'état du service dans l'Observateur d'événements. "
                + "Si ce composant en conditionne d'autres, ne démarrez rien en aval tant qu'il n'est pas rétabli.",
                s.ComponentId);
        }

        // 3. Collecte impossible. Un etat inconnu n'est pas une panne : c'est
        //    une absence d'information, et il faut le dire comme tel.
        if (s.State == ComponentState.Inconnu)
        {
            yield return new AlertCondition(
                AlertKind.CollecteImpossible, AlertSeverity.Avertissement,
                $"{s.LogicalName} : état non collecté",
                s.Verdict is { Length: > 0 } v ? v : "La collecte n'a rien retourné.",
                "L'absence d'information n'est pas l'absence de problème. Vérifiez la joignabilité du "
                + "serveur, le port WinRM et les droits du compte d'accès.",
                s.ComponentId);
        }

        // 4. Preuve indisponible sur un composant qui devrait en avoir une.
        if (s.LogProofStatus is LogProofState.Unprovable or LogProofState.LogNotFound
            && s.State != ComponentState.NonSupervise
            && s.State != ComponentState.Arret)
        {
            var introuvable = s.LogProofStatus == LogProofState.LogNotFound;

            yield return new AlertCondition(
                AlertKind.PreuveIndisponible, AlertSeverity.Avertissement,
                $"{s.LogicalName} : état non prouvable",
                introuvable
                    ? $"Le journal {s.LogPathResolved} est introuvable ou illisible."
                    : "Aucun marqueur de démarrage n'est configuré pour ce composant.",
                introuvable
                    ? "Vérifiez le chemin du journal et les droits de lecture du compte d'accès."
                    : "Relevez le marqueur sur un démarrage réussi avec l'assistant. Sans lui, l'état de "
                      + "ce composant restera « à confirmer » et aucune séquence ne pourra conclure.",
                s.ComponentId);
        }

        // 5. Ecart d'horloge.
        if (s.ClockSkewSeconds is { } skew && Math.Abs(skew) >= SeuilEcartHorlogeSecondes)
        {
            var grave = Math.Abs(skew) >= 5;

            yield return new AlertCondition(
                AlertKind.EcartHorloge,
                grave ? AlertSeverity.Critique : AlertSeverity.Avertissement,
                $"{s.HostName} : écart d'horloge de {skew:0.00} s",
                $"L'horloge du serveur diffère de {skew:0.00} seconde(s) de celle de N4 Sentinel.",
                "Au-delà d'une seconde, N4 produit des statuts DISCONNECTED trompeurs — c'est une cause "
                + "documentée d'incident majeur. Vérifiez la synchronisation NTP de ce serveur avant de "
                + "diagnostiquer quoi que ce soit d'autre : plusieurs symptômes en découlent.",
                s.ComponentId);
        }

        // 6. Demarrage anormalement long. Un composant qui reste en attente de
        //    preuve n'est pas en panne, mais il finit par le devenir.
        if (s.LogProofStatus == LogProofState.WaitingForProof
            && s.State == ComponentState.Demarrage)
        {
            yield return new AlertCondition(
                AlertKind.DemarrageAnormalementLong, AlertSeverity.Information,
                $"{s.LogicalName} est en cours d'initialisation",
                "Le service tourne, le marqueur de démarrage n'est pas encore apparu.",
                "Comportement normal pendant plusieurs minutes sur un démarrage à froid. "
                + "Si la situation persiste au-delà du délai configuré, consultez le journal du composant.",
                s.ComponentId);
        }
    }

    private sealed record AlertCondition(
        AlertKind Kind,
        AlertSeverity Severity,
        string Title,
        string Detail,
        string Recommendation,
        Guid ComponentId)
    {
        public string Signature => Alert.BuildSignature(Kind, ComponentId);
    }

    // -----------------------------------------------------------------------
    // Consultation
    // -----------------------------------------------------------------------
    public async Task<List<Alert>> GetOpenAsync(Guid? environmentId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.Alerts.AsNoTracking()
            .Where(a => a.Status == AlertStatus.Ouverte || a.Status == AlertStatus.Acquittee);

        if (environmentId.HasValue)
            query = query.Where(a => a.EnvironmentId == environmentId.Value);

        return await query
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.LastOccurredAt)
            .ToListAsync(ct);
    }

    public async Task<List<Alert>> GetRecentlyResolvedAsync(int take = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Alerts.AsNoTracking()
            .Where(a => a.Status == AlertStatus.Resolue)
            .OrderByDescending(a => a.ResolvedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Acquitte une alerte : l'opérateur l'a vue et l'assume. Elle reste
    /// ouverte — acquitter n'est pas résoudre — mais sort du décompte des
    /// alertes réclamant une attention.
    /// </summary>
    public async Task AcknowledgeAsync(Guid alertId, string actor, string? note, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null || alert.Status != AlertStatus.Ouverte) return;

        alert.Status = AlertStatus.Acquittee;
        alert.AcknowledgedAt = DateTimeOffset.UtcNow;
        alert.AcknowledgedBy = actor;
        alert.AcknowledgementNote = note;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Alerte acquittée par {Acteur} — {Titre}. Motif : {Motif}",
            actor, alert.Title, note ?? "non précisé");
    }
}
