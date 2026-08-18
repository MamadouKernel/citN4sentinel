using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Diagnostic;

/// <summary>
/// Collecte et analyse des journaux (FR-070, FR-072, FR-076, FR-077, FR-079B).
///
/// LE CONTENU DES JOURNAUX N'EST PAS CONSERVÉ. Seuls les constats le sont —
/// avec leur ligne représentative et leur contexte, tous deux masqués. C'est un
/// choix délibéré : garder une copie intégrale des journaux de production
/// ferait de la base de N4 Sentinel un second endroit où ils traînent, avec ses
/// propres sauvegardes et ses propres droits d'accès. La contrepartie est
/// assumée : ajouter une signature n'enrichit pas les diagnostics passés, il
/// faut recollecter.
///
/// LE MASQUAGE INTERVIENT AVANT TOUTE ÉCRITURE. Aucun chemin de ce service
/// n'écrit un fragment de journal qui ne soit passé par le masqueur.
/// </summary>
public sealed class LogAnalysisService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector,
    SignatureCatalogue catalogue,
    Observability.MetricsService metrics,
    ILogger<LogAnalysisService> logger)
{
    /// <summary>
    /// Volume lu par journal. Au-delà, seule la fin du fichier est analysée :
    /// c'est là que se trouve ce qui vient de se passer.
    /// </summary>
    public const int TailleMaximale = 2 * 1024 * 1024;

    /// <summary>Lignes conservées autour d'une occurrence (FR-077).</summary>
    private const int LignesDeContexte = 3;

    // -----------------------------------------------------------------------
    // Collecte
    // -----------------------------------------------------------------------
    /// <summary>Collecte ciblée : lit le journal d'un composant sur son serveur.</summary>
    public async Task<SourceResult> CollectFromServerAsync(
        Guid sessionId, Guid componentId,
        DateTimeOffset? windowStart = null, DateTimeOffset? windowEnd = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return SourceResult.Failed("Session de diagnostic introuvable.");

        if (windowStart.HasValue || windowEnd.HasValue)
        {
            session.WindowStart = windowStart;
            session.WindowEnd = windowEnd;
            await db.SaveChangesAsync(ct);
        }

        var composant = await db.Components
            .AsNoTracking()
            .Include(c => c.Server)
            .FirstOrDefaultAsync(c => c.Id == componentId, ct);

        if (composant is null) return SourceResult.Failed("Composant introuvable.");

        var source = new LogSource
        {
            SessionId = sessionId,
            ComponentId = composant.Id,
            ComponentName = composant.LogicalName,
            ComponentRole = composant.Role,
            HostName = composant.Server?.HostName,
            Origin = LogOriginKind.CollecteCiblee,
            FileName = composant.Readiness.LogPath ?? "(aucun chemin configuré)",
            // §3.18 : rattache la collecte au ticket de l'incident/opération à
            // l'origine de cette session, quand il y en a un.
            CorrelationId = session.TicketReference
        };

        if (string.IsNullOrWhiteSpace(composant.Readiness.LogPath))
            return await EchouerAsync(db, source, LogCollectionFailureReason.ControleNonConfigure,
                $"Aucun chemin de journal n'est configuré pour « {composant.LogicalName} ». "
                + "Renseignez-le sur la fiche du composant, ou versez le fichier manuellement.", ct);

        if (composant.Server is null)
            return await EchouerAsync(db, source, LogCollectionFailureReason.ControleNonConfigure,
                $"Aucun serveur n'est rattaché à « {composant.LogicalName} ».", ct);

        var resolution = await targetFactory.CreateAsync(composant.Server, ct);
        if (!resolution.Succeeded)
            return await EchouerAsync(db, source, LogCollectionFailureReason.ConnecteurIndisponible,
                $"Serveur inaccessible : {resolution.Error}", ct);

        var delta = await connector.ReadLogDeltaAsync(
            resolution.Target!, composant.Readiness.LogPath!, 0, TailleMaximale, ct);

        if (!delta.Succeeded)
            return await EchouerAsync(db, source, ClasserEchecConnecteur(delta.Failure),
                $"Lecture impossible : {delta.Error}", ct);

        if (delta.Value is not { Exists: true })
            return await EchouerAsync(db, source, LogCollectionFailureReason.SourceAbsente,
                $"Aucun fichier ne correspond à « {composant.Readiness.LogPath} » sur "
                + $"{composant.Server.HostName}. Le journal a peut-être été déplacé, ou n'a jamais été écrit.", ct);

        source.ResolvedPath = delta.Value.ResolvedPath;
        source.FileName = System.IO.Path.GetFileName(delta.Value.ResolvedPath);
        source.SizeBytes = delta.Value.Length;
        source.Truncated = delta.Value.Length > TailleMaximale;

        // FR-061 : l'ecart d'horloge du serveur source, mesure au moment ou
        // ce journal est lu — sans lui, la chronologie multi-sources ne
        // pourrait jamais signaler l'incertitude qu'un decalage introduit.
        // Un echec de mesure n'empeche pas la collecte : l'ecart reste
        // simplement inconnu, ce qui est dit tel quel plus loin.
        var systeme = await connector.GetSystemAsync(resolution.Target!, ct);
        source.ClockSkewSecondsAtCollection = systeme.Succeeded ? systeme.Value?.ClockSkewSeconds : null;

        return await IngererAsync(db, session, source, delta.Value.Text, composant.Role, ct);
    }

    /// <summary>
    /// Import manuel (FR-079B). Le chemin de repli quand le serveur est
    /// inaccessible — c'est-à-dire souvent, en situation d'incident grave.
    /// </summary>
    public async Task<SourceResult> ImportAsync(
        Guid sessionId, string fileName, string contenu, Guid? componentId = null,
        DateTimeOffset? windowStart = null, DateTimeOffset? windowEnd = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return SourceResult.Failed("Session de diagnostic introuvable.");

        if (windowStart.HasValue || windowEnd.HasValue)
        {
            session.WindowStart = windowStart;
            session.WindowEnd = windowEnd;
            await db.SaveChangesAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(contenu))
            return SourceResult.Failed("Le fichier versé est vide.");

        N4Component? composant = null;
        var detecteAutomatiquement = false;

        if (componentId is not null)
        {
            composant = await db.Components.AsNoTracking()
                .Include(c => c.Server)
                .FirstOrDefaultAsync(c => c.Id == componentId, ct);
        }
        else
        {
            // FR-071 : l'operateur n'a pas precise de composant — tenter de
            // l'identifier depuis le nom de fichier, jamais depuis une
            // supposition sur le contenu qui pourrait se tromper de composant
            // en confondant deux journaux qui se ressemblent.
            composant = await IdentifierComposantAsync(db, session.EnvironmentId, fileName, ct);
            detecteAutomatiquement = composant is not null;
        }

        var source = new LogSource
        {
            SessionId = sessionId,
            ComponentId = composant?.Id,
            ComponentName = composant?.LogicalName,
            ComponentRole = composant?.Role,
            ComponentAutoDetected = detecteAutomatiquement,
            HostName = composant?.Server?.HostName,
            Origin = LogOriginKind.ImportManuel,
            FileName = fileName,
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(contenu)
        };

        var texte = contenu;
        if (texte.Length > TailleMaximale)
        {
            texte = texte[^TailleMaximale..];
            source.Truncated = true;
        }

        return await IngererAsync(db, session, source, texte, composant?.Role, ct);
    }

    // -----------------------------------------------------------------------
    // Ingestion : masquage puis analyse, dans la même passe
    // -----------------------------------------------------------------------
    private async Task<SourceResult> IngererAsync(
        N4SentinelDbContext db, DiagnosticSession session, LogSource source,
        string contenuBrut, ComponentRole? role, CancellationToken ct)
    {
        // MASQUAGE D'ABORD. Rien de ce qui suit ne doit voir le contenu brut.
        var (contenu, masques) = SecretMasker.Masquer(contenuBrut);
        source.MaskedSecretCount = masques;

        // §3.18/FR-067 : empreinte du contenu deja masque - jamais du brut,
        // jamais le contenu lui-meme conserve.
        source.ContentHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contenu)));

        var lignes = contenu.Split('\n');
        source.LineCount = lignes.Length;

        // FR-073 : periode couverte, volumes par niveau et version — sur
        // TOUT le fichier, pas seulement les lignes retenues comme anomalies.
        CalculerResume(lignes, source);

        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);

        var signatures = (await catalogue.GetActiveAsync(ct))
            .Where(s => s.AppliesToRole is null || role is null || s.AppliesToRole == role)
            .ToList();

        var constats = Analyser(lignes, signatures, session, source);

        if (constats.Count > 0)
        {
            db.Findings.AddRange(constats);
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Journal « {Fichier} » analysé : {Lignes} ligne(s), {Constats} constat(s), {Masques} secret(s) masqué(s).",
            source.FileName, source.LineCount, constats.Count, masques);

        return SourceResult.Ok(source.Id, source.LineCount, constats.Count, masques);
    }

    // -----------------------------------------------------------------------
    // Identification automatique (FR-071)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Cherche d'abord le nom exact d'un composant déclaré dans le nom du
    /// fichier — le seul signal assez fiable pour être affirmé. À défaut, un
    /// motif de nom de fichier non ambigu, mais seulement si un unique
    /// composant de ce rôle existe dans l'environnement : une identification
    /// hasardeuse ferait pire que pas d'identification du tout.
    /// </summary>
    private static async Task<N4Component?> IdentifierComposantAsync(
        N4SentinelDbContext db, Guid environmentId, string fileName, CancellationToken ct)
    {
        var composants = await db.Components.AsNoTracking()
            .Include(c => c.Server)
            .Where(c => c.EnvironmentId == environmentId)
            .ToListAsync(ct);

        // Correspondance par "unite" (bornes non alphanumeriques) et, a egalite,
        // le nom le plus long l'emporte : un nom court (ex. "ECN4") est parfois
        // un prefixe d'un nom plus specifique (ex. "ECN4Web") present dans le
        // meme fichier, et prendre le premier trouve attribuerait le journal
        // au mauvais composant sans jamais le signaler comme incertain.
        var parServiceName = composants
            .Where(c => !string.IsNullOrWhiteSpace(c.WindowsServiceName)
                && ContientCommeUnite(fileName, c.WindowsServiceName))
            .OrderByDescending(c => c.WindowsServiceName!.Length)
            .ToList();
        if (parServiceName.Count > 0) return parServiceName[0];

        var parLogicalName = composants
            .Where(c => ContientCommeUnite(fileName, c.LogicalName))
            .OrderByDescending(c => c.LogicalName.Length)
            .ToList();
        if (parLogicalName.Count > 0) return parLogicalName[0];

        var role = DeviserRoleParNomFichier(fileName);
        if (role is null) return null;

        var candidats = composants.Where(c => c.Role == role.Value).ToList();
        return candidats.Count == 1 ? candidats[0] : null;
    }

    /// <summary>
    /// Vrai si <paramref name="needle"/> apparaît dans <paramref name="haystack"/>
    /// sans être le prefixe/suffixe d'un mot plus long — évite qu'"ECN4" ne
    /// s'attribue à tort un fichier "ecn4web-...".
    /// </summary>
    private static bool ContientCommeUnite(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return false;
        var idx = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        var avant = idx > 0 ? haystack[idx - 1] : '\0';
        var finIdx = idx + needle.Length;
        var apres = finIdx < haystack.Length ? haystack[finIdx] : '\0';
        return !char.IsLetterOrDigit(avant) && !char.IsLetterOrDigit(apres);
    }

    private static ComponentRole? DeviserRoleParNomFichier(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        if (n.Contains("bridge")) return ComponentRole.BridgeDaemon;
        if (n.Contains("ecn4web") || n.Contains("ecn4-web") || n.Contains("ecn4_web")) return ComponentRole.Ecn4Web;
        if (n.Contains("ecn4")) return ComponentRole.Ecn4;
        if (n.Contains("xps")) return ComponentRole.Xps;
        return null;
    }

    // -----------------------------------------------------------------------
    // Résumé (FR-073)
    // -----------------------------------------------------------------------
    private static readonly Regex MotifNiveau = new(
        @"\b(INFO|WARN(?:ING)?|ERROR|DEBUG|FATAL|SEVERE)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MotifVersion = new(
        @"\bversion[\s:=]+v?(\d+\.\d+(?:\.\d+){0,2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Période couverte et volumes par niveau, calculés sur TOUT le fichier
    /// — pas seulement les lignes retenues comme anomalies, sans quoi la
    /// période afficherait celle des erreurs plutôt que celle du journal.
    /// </summary>
    private static void CalculerResume(string[] lignes, LogSource source)
    {
        // FR-071 : le type de journal se reconnaît sur un échantillon, pas
        // ligne par ligne — un motif structurel a besoin de plusieurs lignes
        // pour être affirmé sans ambiguïté.
        source.DetectedLogType = DetecterTypeJournal(string.Join('\n', lignes.Take(80)));

        foreach (var ligneBrute in lignes)
        {
            var ligne = ligneBrute.TrimEnd('\r');
            if (ligne.Length == 0) continue;

            var m = MotifHorodatage.Match(ligne);
            if (m.Success)
            {
                var texte = $"{m.Groups["d"].Value} {m.Groups["t"].Value}";
                if (DateTime.TryParseExact(texte, "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
                {
                    var horodatage = new DateTimeOffset(d);
                    if (source.EarliestEntryAt is null || horodatage < source.EarliestEntryAt)
                        source.EarliestEntryAt = horodatage;
                    if (source.LatestEntryAt is null || horodatage > source.LatestEntryAt)
                        source.LatestEntryAt = horodatage;
                }

                // FR-071 : fuseau horaire — seulement s'il est EXPLICITEMENT
                // écrit dans l'horodatage. La plupart des journaux N4
                // n'en portent aucun ; on ne le devine jamais (voir
                // ClockSkewSecondsAtCollection, la mesure live qui comble
                // cette absence pour la collecte ciblée).
                if (source.DetectedTimeZone is null && m.Groups["tz"].Success)
                    source.DetectedTimeZone = m.Groups["tz"].Value == "Z" ? "UTC" : m.Groups["tz"].Value;
            }

            var niveau = MotifNiveau.Match(ligne);
            if (niveau.Success)
            {
                var valeur = niveau.Value.ToUpperInvariant();
                if (valeur == "INFO") source.InfoCount++;
                else if (valeur.StartsWith("WARN", StringComparison.Ordinal)) source.WarningCount++;
                else if (valeur is "ERROR" or "FATAL" or "SEVERE") source.ErrorCount++;
            }

            if (source.DetectedVersion is null)
            {
                var version = MotifVersion.Match(ligne);
                if (version.Success) source.DetectedVersion = version.Groups[1].Value;
            }
        }
    }

    private static async Task<SourceResult> EchouerAsync(
        N4SentinelDbContext db, LogSource source, LogCollectionFailureReason motif, string erreur, CancellationToken ct)
    {
        source.Error = erreur;
        source.FailureReason = motif;
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);
        return SourceResult.Failed(erreur);
    }

    /// <summary>§3.18 : reclasse l'échec bas niveau du connecteur dans la taxonomie exacte exigée.</summary>
    private static LogCollectionFailureReason ClasserEchecConnecteur(Connectors.ConnectorFailure echec) => echec switch
    {
        Connectors.ConnectorFailure.AccesRefuse or Connectors.ConnectorFailure.AuthentificationRefusee
            => LogCollectionFailureReason.AccesRefuse,
        Connectors.ConnectorFailure.Timeout => LogCollectionFailureReason.Timeout,
        Connectors.ConnectorFailure.CibleIntrouvable or Connectors.ConnectorFailure.NomNonResolu
            => LogCollectionFailureReason.SourceAbsente,
        Connectors.ConnectorFailure.Injoignable => LogCollectionFailureReason.ConnecteurIndisponible,
        _ => LogCollectionFailureReason.ConnecteurIndisponible
    };

    // -----------------------------------------------------------------------
    // Analyse
    // -----------------------------------------------------------------------
    /// <summary>
    /// Reconnaît les signatures, puis regroupe les erreurs répétées non
    /// cataloguées.
    ///
    /// LE REGROUPEMENT EST L'ESSENTIEL. Quarante fois la même exception n'est
    /// pas quarante problèmes : c'est un problème vu quarante fois. Sans ce
    /// regroupement, la liste devient illisible et l'anomalie rare — souvent la
    /// plus intéressante — se noie dans la répétition.
    /// </summary>
    private static List<LogFinding> Analyser(
        string[] lignes, List<DiagnosticSignature> signatures,
        DiagnosticSession session, LogSource source)
    {
        var parSignature = new Dictionary<string, LogFinding>(StringComparer.Ordinal);
        var parMessage = new Dictionary<string, LogFinding>(StringComparer.Ordinal);

        for (var i = 0; i < lignes.Length; i++)
        {
            var ligne = lignes[i].TrimEnd('\r');
            if (ligne.Length == 0) continue;

            var horodatage = ExtraireHorodatage(ligne);

            // Fenetre d'analyse : une ligne hors plage n'est pas examinee. Le
            // verdict ne vaut que pour ce qui a ete regarde, et le rapport le dit.
            if (horodatage is not null && !DansLaFenetre(horodatage.Value, session)) continue;

            var reconnue = false;

            foreach (var signature in signatures)
            {
                if (!Correspond(ligne, signature.Pattern)) continue;

                reconnue = true;

                if (parSignature.TryGetValue(signature.Code, out var existant))
                {
                    existant.OccurrenceCount++;
                    if (horodatage is not null) existant.LastSeenAt = horodatage;
                    continue;
                }

                var nouveauConstat = new LogFinding
                {
                    SessionId = session.Id,
                    SourceId = source.Id,
                    SignatureId = signature.Id,
                    SignatureCode = signature.Code,
                    Domain = signature.Domain,
                    Severity = signature.Severity,
                    Title = signature.Name,
                    SampleLine = Tronquer(ligne, 1000),
                    Context = Contexte(lignes, i),
                    FirstSeenAt = horodatage,
                    LastSeenAt = horodatage,
                    FirstLineNumber = i + 1,
                    Meaning = signature.Meaning,
                    Remediation = signature.Remediation,
                    DocumentReference = signature.DocumentReference
                };
                EnrichirConstat(nouveauConstat, ligne);
                parSignature[signature.Code] = nouveauConstat;
            }

            if (reconnue) continue;

            // Erreur non cataloguee : on la retient quand meme, regroupee par
            // message normalise. Ne rapporter que le connu ferait passer a cote
            // de tout ce qui est nouveau - c'est-a-dire de l'essentiel un jour
            // de panne inedite.
            if (!EstUneErreur(ligne)) continue;

            var cle = Normaliser(ligne);
            if (cle.Length < 12) continue;

            if (parMessage.TryGetValue(cle, out var groupe))
            {
                groupe.OccurrenceCount++;
                if (horodatage is not null) groupe.LastSeenAt = horodatage;
                continue;
            }

            var constatGroupe = new LogFinding
            {
                SessionId = session.Id,
                SourceId = source.Id,
                Domain = DiagnosticDomain.Indetermine,
                Severity = ContientMotifCritique(ligne) ? SignatureSeverity.Critique : SignatureSeverity.Erreur,
                Title = Tronquer(MessageLisible(ligne), 180),
                SampleLine = Tronquer(ligne, 1000),
                Context = Contexte(lignes, i),
                FirstSeenAt = horodatage,
                LastSeenAt = horodatage,
                FirstLineNumber = i + 1,
                Meaning = "Erreur non répertoriée au catalogue. Elle est signalée parce qu'elle a la forme "
                        + "d'une erreur, sans que son sens soit connu de l'application."
            };
            EnrichirConstat(constatGroupe, ligne);
            parMessage[cle] = constatGroupe;
        }

        return parSignature.Values
            .Concat(parMessage.Values)
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.OccurrenceCount)
            .ToList();
    }

    /// <summary>
    /// Lignes encadrant l'occurrence (FR-077). Une erreur isolée de son
    /// contexte est souvent inexploitable : la cause réelle est fréquemment
    /// dans la ligne d'avant.
    /// </summary>
    private static string Contexte(string[] lignes, int index)
    {
        var debut = Math.Max(0, index - LignesDeContexte);
        var fin = Math.Min(lignes.Length - 1, index + LignesDeContexte);

        var extrait = new List<string>();
        for (var i = debut; i <= fin; i++)
        {
            var marque = i == index ? ">> " : "   ";
            extrait.Add($"{marque}{i + 1,6}  {lignes[i].TrimEnd('\r')}");
        }

        return Tronquer(string.Join('\n', extrait), 4000);
    }

    // -----------------------------------------------------------------------
    // Verdict et hypothèses
    // -----------------------------------------------------------------------
    /// <summary>
    /// Établit le verdict et les hypothèses à partir des constats (FR-063,
    /// FR-064, FR-069, FR-074).
    /// </summary>
    public async Task<DiagnosticSession?> ConcludeAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions
            .Include(s => s.Findings)
            .Include(s => s.Sources)
            .Include(s => s.Hypotheses)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null) return null;

        db.Hypotheses.RemoveRange(session.Hypotheses);

        var constats = session.Findings.ToList();
        var sourcesLues = session.Sources.Count(s => s.Succeeded);
        var sourcesEnEchec = session.Sources.Count(s => !s.Succeeded);

        var signatures = await db.Signatures.AsNoTracking().ToListAsync(ct);
        // FR-065 : seuils administrables plutôt que codés en dur.
        var parametres = await db.DiagnosticSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new DiagnosticSettings();
        var hypotheses = ConstruireHypotheses(constats, signatures, session.Id);

        db.Hypotheses.AddRange(hypotheses);

        // --- Verdict ------------------------------------------------------
        var concluantes = constats
            .Where(f => f.SignatureId is not null
                        && signatures.FirstOrDefault(s => s.Id == f.SignatureId)?.EstConcluante == true)
            .ToList();

        var limites = Limites(session, sourcesLues, sourcesEnEchec);

        // FR-069 : "informations insuffisantes" (rien n'a pu etre lu) est une
        // affirmation differente de "aucune anomalie detectee" (tout a ete lu,
        // rien trouve) - les confondre efface une nuance que le texte exige.
        if (constats.Count == 0 && (sourcesLues == 0 || sourcesEnEchec > 0))
        {
            session.Verdict = DiagnosticVerdict.InformationsInsuffisantes;
            session.VerdictExplanation =
                "Aucune anomalie n'a été relevée, mais une partie de ce qui devait être analysé n'a pas "
                + "pu être lue. Impossible de dire s'il n'y avait rien à trouver, ou si le signal se trouvait "
                + "précisément dans ce qui n'a pas été collecté. "
                + limites + " "
                // FR-064 : recommandation differenciee - le manque tient a la
                // collecte elle-meme, la reponse est donc de la completer.
                + "Recommandation : relancer une collecte complémentaire sur les sources en échec avant "
                + "toute autre conclusion.";
        }
        else if (constats.Count == 0)
        {
            session.Verdict = DiagnosticVerdict.RienDeConcluant;
            session.VerdictExplanation =
                "Aucune anomalie n'a été relevée dans ce qui a été analysé. "
                + "CE N'EST PAS UN CERTIFICAT DE BONNE SANTÉ : la panne peut se situer hors de la fenêtre "
                + "examinée, dans un journal qui n'a pas été collecté, ou ne rien écrire du tout. "
                + limites + " "
                // FR-064 : ici la collecte a reussi - la reponse est d'elargir
                // la fenetre ou de comparer a une periode saine, pas de relire.
                + "Recommandation : si le symptôme persiste, élargissez la fenêtre d'analyse ou comparez "
                + "avec une période saine connue (FR-066).";
        }
        else if (concluantes.Count > 0)
        {
            var principales = hypotheses.OrderByDescending(h => h.Confidence).ToList();
            if (principales.Count >= 2 && principales[0].Confidence >= parametres.HypothesisEstablishedThreshold
                                       && (principales[0].Confidence - principales[1].Confidence) <= 10)
            {
                session.Verdict = DiagnosticVerdict.PlusieursCausesPossibles;
                session.VerdictExplanation =
                    $"Plusieurs hypothèses concurrentes sont plausibles. "
                    + $"La différence de confiance entre « {principales[0].Statement} » et « {principales[1].Statement} » "
                    + $"est trop faible pour désigner l'une d'elles comme certaine. "
                    + limites + " "
                    + $"Recommandation : confrontez les preuves et les éléments contraires de chaque hypothèse.";
            }
            else
            {
                var principale = concluantes.OrderByDescending(f => f.Severity)
                                            .ThenByDescending(f => f.OccurrenceCount).First();

                // FR-069 : "cause confirmee" reste exceptionnel - poids maximal,
            // seule signature concluante, aucune autre hypothese de domaine
            // different en concurrence. Le cas ordinaire reste "tres probable".
            var domainesConcluants = concluantes.Select(f => f.Domain).Distinct().Count();
            var estConfirmee = principale.OccurrenceCount >= 1
                && signatures.FirstOrDefault(s => s.Id == principale.SignatureId)?.ConfidenceWeight >= parametres.ConclusiveSignatureConfidenceWeight
                && domainesConcluants == 1
                && concluantes.Count == concluantes.Count(f => f.Domain == principale.Domain);

            session.Verdict = estConfirmee ? DiagnosticVerdict.CauseConfirmee : DiagnosticVerdict.CauseCaracterisee;
            session.VerdictExplanation =
                $"Une signature connue a été reconnue : « {principale.Title} » "
                + $"({principale.OccurrenceCount} occurrence(s)). {principale.Meaning} "
                + limites;
            }
        }
        else if (hypotheses.Count > 0 && hypotheses[0].Confidence >= parametres.SeriousLeadConfidenceThreshold)
        {
            session.Verdict = DiagnosticVerdict.PisteSerieuse;
            session.VerdictExplanation =
                $"Aucune signature connue n'établit de cause, mais les anomalies convergent vers un "
                + $"domaine : {Libelle(hypotheses[0].Domain)}. C'est une piste, pas une conclusion. "
                + limites + " "
                // FR-064 : la piste appelle un controle complementaire cible
                // sur le domaine identifie, pas une nouvelle collecte generale.
                + $"Recommandation : un contrôle complémentaire ciblé sur {Libelle(hypotheses[0].Domain)} "
                + "confirmerait ou écarterait cette piste.";
        }
        else
        {
            session.Verdict = DiagnosticVerdict.AnomaliesSansCause;
            session.VerdictExplanation =
                $"{constats.Count} anomalie(s) relevée(s), mais elles ne dessinent aucune cause. "
                + "Elles sont présentées telles quelles : établir un lien entre elles serait une invention. "
                + limites + " "
                // FR-064 : sans convergence de domaine, l'automatisation a
                // atteint sa limite - controle manuel, eventuellement escalade.
                + "Recommandation : un contrôle manuel des anomalies listées ci-dessous est nécessaire ; "
                + "envisagez une escalade vers l'équipe Infrastructure ou le support Navis si elles persistent.";
        }

        session.AnalysedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // NFR-008 : distribution des verdicts, consultable sans grep un fichier de log.
        metrics.RecordDiagnosticVerdict(session.Verdict);

        logger.LogInformation("Diagnostic {Session} conclu : {Verdict}, {Hypotheses} hypothèse(s).",
            session.Title, session.Verdict, hypotheses.Count);

        return session;
    }

    /// <summary>
    /// Formule les limites du verdict. Un diagnostic qui ne dit pas ce qu'il
    /// n'a pas regardé se fait croire au-delà de ce qu'il vaut.
    /// </summary>
    private static string Limites(DiagnosticSession session, int lues, int enEchec)
    {
        var parties = new List<string>
        {
            lues switch
            {
                0 => "AUCUN journal n'a pu être lu",
                1 => "un seul journal a été analysé",
                _ => $"{lues} journaux ont été analysés"
            }
        };

        if (enEchec > 0)
            parties.Add($"{enEchec} source(s) n'ont pas pu être collectées — leur contenu n'a donc pas été examiné");

        if (session.WindowStart is not null || session.WindowEnd is not null)
            parties.Add("la fenêtre examinée va de "
                      + $"{session.WindowStart?.ToLocalTime().ToString("dd/MM HH:mm") ?? "l'origine du fichier"} à "
                      + $"{session.WindowEnd?.ToLocalTime().ToString("dd/MM HH:mm") ?? "la fin du fichier"}");

        return "Portée de ce constat : " + string.Join(", ", parties) + ".";
    }

    private static List<DiagnosticHypothesis> ConstruireHypotheses(
        List<LogFinding> constats, List<DiagnosticSignature> signatures, Guid sessionId)
    {
        var hypotheses = new List<DiagnosticHypothesis>();

        foreach (var groupe in constats.Where(f => f.Domain != DiagnosticDomain.Indetermine)
                                       .GroupBy(f => f.Domain))
        {
            var membres = groupe.OrderByDescending(f => f.Severity)
                                .ThenByDescending(f => f.OccurrenceCount).ToList();

            // La confiance vient du poids de la signature la plus forte du
            // domaine, relevee si plusieurs constats distincts convergent. Elle
            // ne depasse jamais 95 : l'application n'a pas vu le systeme, elle
            // a lu un fichier.
            var poidsMax = membres
                .Select(f => signatures.FirstOrDefault(s => s.Id == f.SignatureId)?.ConfidenceWeight ?? 30)
                .DefaultIfEmpty(30)
                .Max();

            var convergence = Math.Min(15, (membres.Count - 1) * 5);
            var confiance = Math.Min(95, poidsMax + convergence);

            var principal = membres[0];
            var signaturePrincipale = signatures.FirstOrDefault(s => s.Id == principal.SignatureId);

            hypotheses.Add(new DiagnosticHypothesis
            {
                SessionId = sessionId,
                Domain = groupe.Key,
                Confidence = confiance,
                Statement = Formuler(groupe.Key, membres),
                Evidence = string.Join(" · ", membres.Take(4)
                    .Select(f => $"{f.Title} ({f.OccurrenceCount}×, ligne {f.FirstLineNumber})")),
                // FR-063 : ce qui contredirait l'hypothese, quand la signature
                // le documente ; dit explicitement quand rien n'a ete releve,
                // plutot que de laisser le champ silencieusement vide.
                CounterEvidence = signaturePrincipale?.CounterEvidence
                    ?? "Aucune preuve à l'encontre identifiée pour la signature retenue.",
                EvidenceObservedAt = membres.Where(f => f.LastSeenAt is not null).Select(f => f.LastSeenAt)
                    .DefaultIfEmpty(null).Max(),
                RuleVersion = signaturePrincipale is not null ? $"{signaturePrincipale.Code} v{signaturePrincipale.Version}" : null,
                Recommendation = principal.Remediation
            });
        }

        var classees = hypotheses.OrderByDescending(h => h.Confidence).ToList();
        for (var i = 0; i < classees.Count; i++) classees[i].Rank = i + 1;

        return classees;
    }

    private static string Formuler(DiagnosticDomain domaine, List<LogFinding> membres)
    {
        var total = membres.Sum(f => f.OccurrenceCount);
        var libelle = Libelle(domaine);

        return membres.Count == 1
            ? $"Anomalie de {libelle} : « {membres[0].Title} », {total} occurrence(s)."
            : $"Anomalies de {libelle} : {membres.Count} constats distincts, {total} occurrence(s) au total.";
    }

    public static string Libelle(DiagnosticDomain d) => d switch
    {
        DiagnosticDomain.BaseDeDonnees => "base de données",
        DiagnosticDomain.Reseau => "réseau",
        DiagnosticDomain.Memoire => "mémoire",
        DiagnosticDomain.Configuration => "configuration",
        DiagnosticDomain.Licence => "licence",
        DiagnosticDomain.Cluster => "cluster",
        DiagnosticDomain.Stockage => "stockage",
        DiagnosticDomain.Integration => "intégration",
        DiagnosticDomain.Securite => "sécurité",
        DiagnosticDomain.Horloge => "synchronisation d'horloge",
        DiagnosticDomain.Applicatif => "applicatif",
        DiagnosticDomain.Systeme => "système ou VM",
        DiagnosticDomain.Services => "services",
        DiagnosticDomain.N4Cluster => "N4 Cluster",
        DiagnosticDomain.CenterStandby => "Center/Standby",
        DiagnosticDomain.ActiveMqKahaDb => "ActiveMQ/KahaDB",
        DiagnosticDomain.BridgeXps => "Bridge/XPS",
        DiagnosticDomain.Ecn4Ecn4Web => "ECN4/ECN4Web",
        DiagnosticDomain.SharedFolders => "Shared Folders",
        DiagnosticDomain.EdiInterfaces => "EDI et interfaces",
        _ => "domaine indéterminé"
    };

    // -----------------------------------------------------------------------
    // Analyse de ligne
    // -----------------------------------------------------------------------
    // FR-072 : thread, classe et identifiant de transaction, quand le format
    // du journal les porte. Best-effort — un format non reconnu laisse ces
    // champs vides plutôt que d'inventer une correspondance.
    private static readonly Regex MotifThread = new(
        @"\[(?<thread>[\w][\w\-\.\/ ]{0,60})\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MotifClasse = new(
        @"\b(?<classe>(?:[a-z][a-z0-9]*\.){2,}[A-Z][A-Za-z0-9_$]*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MotifTransaction = new(
        @"(?:trans(?:action)?[_-]?id|txn[_-]?id|correlation[_-]?id|request[_-]?id)[\s:=]+[""']?(?<id>[\w\-]{6,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void EnrichirConstat(LogFinding constat, string ligne)
    {
        var thread = MotifThread.Match(ligne);
        if (thread.Success) constat.ThreadName = thread.Groups["thread"].Value;

        var classe = MotifClasse.Match(ligne);
        if (classe.Success) constat.LoggerClass = classe.Groups["classe"].Value;

        var transaction = MotifTransaction.Match(ligne);
        if (transaction.Success) constat.TransactionId = transaction.Groups["id"].Value;
    }

    private static readonly Regex MotifHorodatage = new(
        @"(?<d>\d{4}-\d{2}-\d{2})[ T](?<t>\d{2}:\d{2}:\d{2})(?:[.,](?<ms>\d{1,3}))?" +
        @"(?<tz>Z|[+-]\d{2}:?\d{2})?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// FR-071 : reconnaît le format d'un journal aux marqueurs structurels
    /// qu'il porte — jamais deviné depuis le seul nom de fichier, qui peut
    /// mentir. Retourne null plutôt que d'inventer un type non reconnu.
    /// </summary>
    private static readonly Regex MotifJournalN4 = new(
        @"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}[.,]\d+\s+(INFO|WARN|DEBUG|ERROR|FATAL)\s+\[",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex MotifJournalIis = new(
        @"^#Fields:|^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \S+ (GET|POST|PUT|DELETE) ",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static string? DetecterTypeJournal(string echantillon)
    {
        if (MotifJournalN4.IsMatch(echantillon)) return "Journal applicatif N4 (log4j)";
        if (MotifJournalIis.IsMatch(echantillon)) return "Journal IIS (W3C)";
        if (echantillon.Contains("<Event xmlns=", StringComparison.OrdinalIgnoreCase))
            return "Journal d'événements Windows (XML)";
        return null;
    }

    private static readonly Regex MotifErreur = new(
        @"\b(?:ERROR|SEVERE|FATAL|ERREUR|Exception|Caused by)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MotifCritique = new(
        @"\b(?:FATAL|SEVERE)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Remplace ce qui varie d'une occurrence à l'autre, pour regrouper.</summary>
    private static readonly Regex MotifVariable = new(
        @"\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?" +
        @"|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}" +
        @"|\b\d+\b" +
        @"|(?:[A-Za-z]:)?[\\/][^\s""']{4,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static DateTimeOffset? ExtraireHorodatage(string ligne)
    {
        var m = MotifHorodatage.Match(ligne);
        if (!m.Success) return null;

        var texte = $"{m.Groups["d"].Value} {m.Groups["t"].Value}";
        return DateTime.TryParseExact(texte, "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? new DateTimeOffset(d)
            : null;
    }

    private static bool DansLaFenetre(DateTimeOffset horodatage, DiagnosticSession session)
    {
        if (session.WindowStart is { } debut && horodatage < debut) return false;
        if (session.WindowEnd is { } fin && horodatage > fin) return false;
        return true;
    }

    private static bool EstUneErreur(string ligne) => MotifErreur.IsMatch(ligne);

    private static bool ContientMotifCritique(string ligne) => MotifCritique.IsMatch(ligne);

    private static string Normaliser(string ligne) => MotifVariable.Replace(ligne, "#").Trim();

    /// <summary>Retire l'entête technique pour ne garder que le message.</summary>
    private static string MessageLisible(string ligne)
    {
        var separateur = ligne.IndexOf(" - ", StringComparison.Ordinal);
        if (separateur > 0 && separateur + 3 < ligne.Length)
            return ligne[(separateur + 3)..].Trim();

        var crochet = ligne.LastIndexOf(']');
        if (crochet > 0 && crochet + 1 < ligne.Length)
            return ligne[(crochet + 1)..].Trim();

        return ligne.Trim();
    }

    private static bool Correspond(string ligne, string motif)
    {
        try
        {
            return Regex.IsMatch(ligne, motif,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (RegexMatchTimeoutException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static string Tronquer(string texte, int max) =>
        texte.Length <= max ? texte : texte[..(max - 1)] + "…";
}

public sealed record SourceResult
{
    public bool Succeeded { get; init; }
    public Guid SourceId { get; init; }
    public int LineCount { get; init; }
    public int FindingCount { get; init; }
    public int MaskedSecretCount { get; init; }
    public string? Error { get; init; }

    public static SourceResult Ok(Guid id, int lignes, int constats, int masques) =>
        new() { Succeeded = true, SourceId = id, LineCount = lignes, FindingCount = constats, MaskedSecretCount = masques };

    public static SourceResult Failed(string error) => new() { Succeeded = false, Error = error };
}
