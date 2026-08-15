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

    /// <summary>
    /// FR-032/033 : Center et Standby actifs simultanément est un split-brain,
    /// pas une panne de composant — d'où une alerte de portée ENVIRONNEMENT
    /// (<see cref="Alert.ComponentId"/> nul), distincte du mécanisme par
    /// composant de <see cref="EvaluateAsync"/>. Ni <paramref name="center"/>
    /// ni <paramref name="standby"/> ne sont supposés présents : un
    /// environnement peut ne déclarer que l'un des deux, ou aucun.
    /// </summary>
    public async Task EvaluateCenterConflictAsync(
        Guid environmentId, ComponentHealthSnapshot? center, ComponentHealthSnapshot? standby,
        CancellationToken ct = default)
    {
        var enConflit = center?.HoldsActiveRole == true && standby?.HoldsActiveRole == true;
        var signature = Alert.BuildSignature(AlertKind.ConflitRoleActifCenter, null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existante = await db.Alerts.FirstOrDefaultAsync(a =>
            a.EnvironmentId == environmentId && a.Signature == signature
            && (a.Status == AlertStatus.Ouverte || a.Status == AlertStatus.Acquittee), ct);

        if (enConflit)
        {
            var detail = $"{center!.LogicalName} ET {standby!.LogicalName} détiennent tous deux le rôle actif "
                        + "d'après leurs marqueurs de journal respectifs.";

            if (existante is null)
            {
                db.Alerts.Add(new Alert
                {
                    Signature = signature,
                    EnvironmentId = environmentId,
                    ComponentId = null,
                    ComponentName = "Center / Standby",
                    Kind = AlertKind.ConflitRoleActifCenter,
                    Severity = AlertSeverity.Critique,
                    Title = "Center et Standby actifs simultanément",
                    Detail = detail,
                    Recommendation = "N'intervenez pas avant d'avoir confirmé lequel doit rester actif. "
                        + "Un split-brain corrompt l'état partagé si les deux continuent à écrire. "
                        + "Consultez la procédure de bascule Center avant toute action."
                });

                logger.LogWarning("Alerte ouverte — conflit de rôle Center/Standby sur l'environnement {Env}.", environmentId);
            }
            else
            {
                existante.LastOccurredAt = DateTimeOffset.UtcNow;
                existante.OccurrenceCount++;
                existante.Detail = detail;
            }
        }
        else if (existante is not null)
        {
            existante.Status = AlertStatus.Resolue;
            existante.ResolvedAt = DateTimeOffset.UtcNow;

            logger.LogInformation("Alerte résolue — conflit de rôle Center/Standby sur l'environnement {Env}.", environmentId);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// FR-059I : alertes EDI, dérivées des fichiers suivis par
    /// <c>EdiTrackingService</c>. Deux conditions, chacune conditionnée à un
    /// seuil DÉCLARÉ sur le composant — sans seuil, aucune alerte n'est levée
    /// plutôt que d'en inventer un par défaut.
    /// </summary>
    public async Task EvaluateEdiAsync(N4Component composant, List<EdiFile> fichiers, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existantes = await db.Alerts
            .Where(a => a.ComponentId == composant.Id
                        && (a.Kind == AlertKind.FichierEdiNonConsomme
                            || a.Kind == AlertKind.AucuneIntegrationEdiRecente
                            || a.Kind == AlertKind.EchecsEdiRepetes)
                        && (a.Status == AlertStatus.Ouverte || a.Status == AlertStatus.Acquittee))
            .ToListAsync(ct);

        // --- Fichiers non consommes au-dela du delai declare -----------------
        var signatureRetard = Alert.BuildSignature(AlertKind.FichierEdiNonConsomme, composant.Id);
        var ouverteRetard = existantes.FirstOrDefault(a => a.Signature == signatureRetard);

        var enRetard = composant.SharedFolder.MaxPendingAgeHours is { } seuilAge
            ? fichiers.Where(f => f.Status is EdiFileStatus.EnAttente or EdiFileStatus.Rejete
                                   && f.Age.TotalHours > seuilAge).ToList()
            : [];

        if (enRetard.Count > 0)
        {
            var detail = $"{enRetard.Count} fichier(s) non consommé(s) depuis plus de "
                       + $"{composant.SharedFolder.MaxPendingAgeHours} h : "
                       + string.Join(", ", enRetard.Take(5).Select(f => f.FileName))
                       + (enRetard.Count > 5 ? "…" : "");

            if (ouverteRetard is null)
            {
                db.Alerts.Add(new Alert
                {
                    Signature = signatureRetard,
                    EnvironmentId = composant.EnvironmentId,
                    ComponentId = composant.Id,
                    ComponentName = composant.LogicalName,
                    Kind = AlertKind.FichierEdiNonConsomme,
                    Severity = AlertSeverity.Avertissement,
                    Title = $"{composant.LogicalName} : fichier(s) EDI non consommé(s)",
                    Detail = detail,
                    Recommendation = "Vérifiez le traitement en attente et la disponibilité du composant "
                        + "d'intégration en aval avant de rejouer ou de déplacer les fichiers concernés."
                });

                logger.LogWarning("Alerte ouverte — fichiers EDI non consommés sur {Composant}.", composant.LogicalName);
            }
            else
            {
                ouverteRetard.LastOccurredAt = DateTimeOffset.UtcNow;
                ouverteRetard.OccurrenceCount++;
                ouverteRetard.Detail = detail;
            }
        }
        else if (ouverteRetard is not null)
        {
            ouverteRetard.Status = AlertStatus.Resolue;
            ouverteRetard.ResolvedAt = DateTimeOffset.UtcNow;
        }

        // --- Aucune integration reussie recente --------------------------------
        var signatureIntegration = Alert.BuildSignature(AlertKind.AucuneIntegrationEdiRecente, composant.Id);
        var ouverteIntegration = existantes.FirstOrDefault(a => a.Signature == signatureIntegration);

        var enRetardIntegration = false;
        string? detailIntegration = null;

        if (composant.SharedFolder.MaxHoursSinceLastIntegration is { } seuilIntegration && fichiers.Count > 0)
        {
            var derniereIntegration = fichiers
                .Where(f => f.IntegratedAt is not null)
                .Select(f => f.IntegratedAt!.Value)
                .OrderDescending()
                .FirstOrDefault();

            if (derniereIntegration == default)
            {
                enRetardIntegration = true;
                detailIntegration = "Aucune intégration réussie n'a jamais été observée pour ce dossier.";
            }
            else if ((DateTimeOffset.UtcNow - derniereIntegration).TotalHours > seuilIntegration)
            {
                enRetardIntegration = true;
                detailIntegration = $"Dernière intégration réussie le "
                    + $"{derniereIntegration.ToLocalTime():dd/MM/yyyy HH:mm}, au-delà du délai déclaré "
                    + $"({seuilIntegration} h).";
            }
        }

        if (enRetardIntegration)
        {
            if (ouverteIntegration is null)
            {
                db.Alerts.Add(new Alert
                {
                    Signature = signatureIntegration,
                    EnvironmentId = composant.EnvironmentId,
                    ComponentId = composant.Id,
                    ComponentName = composant.LogicalName,
                    Kind = AlertKind.AucuneIntegrationEdiRecente,
                    Severity = AlertSeverity.Critique,
                    Title = $"{composant.LogicalName} : aucune intégration EDI récente",
                    Detail = detailIntegration!,
                    Recommendation = "Vérifiez que le composant d'intégration fonctionne et qu'il reçoit "
                        + "réellement des fichiers — une absence totale d'intégration peut aussi bien signaler "
                        + "un flux tari en amont qu'un composant en panne."
                });

                logger.LogWarning("Alerte ouverte — aucune intégration EDI récente sur {Composant}.", composant.LogicalName);
            }
            else
            {
                ouverteIntegration.LastOccurredAt = DateTimeOffset.UtcNow;
                ouverteIntegration.OccurrenceCount++;
                ouverteIntegration.Detail = detailIntegration!;
            }
        }
        else if (ouverteIntegration is not null)
        {
            ouverteIntegration.Status = AlertStatus.Resolue;
            ouverteIntegration.ResolvedAt = DateTimeOffset.UtcNow;
        }

        // --- Fichiers en echec repete (FR-059I) -------------------------------
        // Distinct du retard : un fichier retraite toutes les heures peut ne
        // jamais depasser le seuil d'anciennete tout en echouant a chaque
        // fois, ce qui pointe vers un partenaire ou un format en cause plutot
        // qu'un simple ralentissement.
        var signatureEchecs = Alert.BuildSignature(AlertKind.EchecsEdiRepetes, composant.Id);
        var ouverteEchecs = existantes.FirstOrDefault(a => a.Signature == signatureEchecs);

        var enEchecRepete = fichiers
            .Where(f => f.ConsecutiveRejections >= SeuilEchecsEdiConsecutifs)
            .OrderByDescending(f => f.ConsecutiveRejections)
            .ToList();

        if (enEchecRepete.Count > 0)
        {
            var detail = $"{enEchecRepete.Count} fichier(s) EDI en échec {SeuilEchecsEdiConsecutifs} fois ou plus "
                       + "de suite : "
                       + string.Join(", ", enEchecRepete.Take(5)
                             .Select(f => $"{f.FileName} ({f.ConsecutiveRejections}×, partenaire {f.Partner ?? "non classé"})"))
                       + (enEchecRepete.Count > 5 ? "…" : "");

            if (ouverteEchecs is null)
            {
                db.Alerts.Add(new Alert
                {
                    Signature = signatureEchecs,
                    EnvironmentId = composant.EnvironmentId,
                    ComponentId = composant.Id,
                    ComponentName = composant.LogicalName,
                    Kind = AlertKind.EchecsEdiRepetes,
                    Severity = AlertSeverity.Avertissement,
                    Title = $"{composant.LogicalName} : fichier(s) EDI en échec répété",
                    Detail = detail,
                    Recommendation = "Un échec qui se répète sur le même fichier signale souvent un format ou "
                        + "un partenaire en cause, pas un incident ponctuel. Consultez le journal du composant "
                        + "d'intégration avant de rejouer le fichier une nouvelle fois."
                });

                logger.LogWarning("Alerte ouverte — fichiers EDI en échec répété sur {Composant}.", composant.LogicalName);
            }
            else
            {
                ouverteEchecs.LastOccurredAt = DateTimeOffset.UtcNow;
                ouverteEchecs.OccurrenceCount++;
                ouverteEchecs.Detail = detail;
            }
        }
        else if (ouverteEchecs is not null)
        {
            ouverteEchecs.Status = AlertStatus.Resolue;
            ouverteEchecs.ResolvedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>FR-059I : au-delà, un fichier EDI est considéré en échec répété.</summary>
    private const int SeuilEchecsEdiConsecutifs = 3;

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

        // 7. Noeud lent (FR-058). Le temps de reponse du premier
        //    aller-retour d'interrogation est le signal de lenteur le moins
        //    cher : un noeud sous tension repond mesurablement plus
        //    lentement avant meme qu'un etat degrade ne se declenche.
        if (s.ResponseTimeMs is { } tempsReponse && tempsReponse > SeuilLenteurNoeudMs)
        {
            yield return new AlertCondition(
                AlertKind.NoeudLent, AlertSeverity.Avertissement,
                $"{s.LogicalName} répond lentement",
                $"Le dernier relevé a mis {tempsReponse:0} ms à répondre, contre un seuil de {SeuilLenteurNoeudMs:0} ms.",
                "Vérifiez la charge CPU/mémoire du serveur et la latence réseau vers lui avant qu'un état "
                + "dégradé ne se déclare : une lenteur de réponse précède souvent l'incident, elle ne le suit pas.",
                s.ComponentId);
        }

        // 8. Ressource critique (FR-054) : espace disque du serveur qui
        //    heberge ce composant. Meme seuil que le pre-check (< 10 %
        //    libre) pour que les deux controles se contredisent jamais.
        if (s.DiskFreePercentMin is { } libre && libre < 10)
        {
            yield return new AlertCondition(
                AlertKind.RessourceCritique,
                libre < 5 ? AlertSeverity.Critique : AlertSeverity.Avertissement,
                $"{s.HostName} : disque {s.DiskCriticalDrive} à {libre:0.0}% libre",
                $"Le disque {s.DiskCriticalDrive} du serveur hébergeant « {s.LogicalName} » ne dispose plus "
                + $"que de {libre:0.0}% d'espace libre.",
                "Un disque saturé bloque l'écriture des journaux applicatifs et des files ActiveMQ/KahaDB "
                + "avant qu'aucun autre symptôme n'apparaisse. Libérez de l'espace ou étendez le volume avant "
                + "de lancer toute opération mutative sur ce composant.",
                s.ComponentId);
        }

        // 9. Synchronisation N4-XPS en retard (FR-056). Portee composant
        //    (le Bridge ou le XPS concerne), pas environnement : contraste
        //    avec le conflit Center/Standby qui, lui, n'impute personne.
        if (s.SyncDelayed == true)
        {
            yield return new AlertCondition(
                AlertKind.SynchronisationXpsRetardee, AlertSeverity.Critique,
                $"{s.LogicalName} : synchronisation N4-XPS en retard",
                s.LastSyncConfirmedAt is { } confirmeLe
                    ? $"Dernier échange normal confirmé le {confirmeLe.ToLocalTime():dd/MM/yyyy HH:mm:ss}."
                    : "Aucun échange normal confirmé depuis le début de la supervision de ce composant.",
                "Retard documenté comme cause de désynchronisation N4/XPS : vérifiez les files Center, le "
                + "consommateur Bridge, la charge XPS et la base ECI avant d'agir sur le composant lui-même.",
                s.ComponentId);
        }
    }

    /// <summary>Au-delà, un nœud est considéré comme répondant lentement (FR-058).</summary>
    private const double SeuilLenteurNoeudMs = 5000;

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
