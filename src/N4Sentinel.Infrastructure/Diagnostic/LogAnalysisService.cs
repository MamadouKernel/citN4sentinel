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
        Guid sessionId, Guid componentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return SourceResult.Failed("Session de diagnostic introuvable.");

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
            FileName = composant.Readiness.LogPath ?? "(aucun chemin configuré)"
        };

        if (string.IsNullOrWhiteSpace(composant.Readiness.LogPath))
            return await EchouerAsync(db, source,
                $"Aucun chemin de journal n'est configuré pour « {composant.LogicalName} ». "
                + "Renseignez-le sur la fiche du composant, ou versez le fichier manuellement.", ct);

        if (composant.Server is null)
            return await EchouerAsync(db, source,
                $"Aucun serveur n'est rattaché à « {composant.LogicalName} ».", ct);

        var resolution = await targetFactory.CreateAsync(composant.Server, ct);
        if (!resolution.Succeeded)
            return await EchouerAsync(db, source, $"Serveur inaccessible : {resolution.Error}", ct);

        var delta = await connector.ReadLogDeltaAsync(
            resolution.Target!, composant.Readiness.LogPath!, 0, TailleMaximale, ct);

        if (!delta.Succeeded)
            return await EchouerAsync(db, source, $"Lecture impossible : {delta.Error}", ct);

        if (delta.Value is not { Exists: true })
            return await EchouerAsync(db, source,
                $"Aucun fichier ne correspond à « {composant.Readiness.LogPath} » sur "
                + $"{composant.Server.HostName}. Le journal a peut-être été déplacé, ou n'a jamais été écrit.", ct);

        source.ResolvedPath = delta.Value.ResolvedPath;
        source.FileName = System.IO.Path.GetFileName(delta.Value.ResolvedPath);
        source.SizeBytes = delta.Value.Length;
        source.Truncated = delta.Value.Length > TailleMaximale;

        return await IngererAsync(db, session, source, delta.Value.Text, composant.Role, ct);
    }

    /// <summary>
    /// Import manuel (FR-079B). Le chemin de repli quand le serveur est
    /// inaccessible — c'est-à-dire souvent, en situation d'incident grave.
    /// </summary>
    public async Task<SourceResult> ImportAsync(
        Guid sessionId, string fileName, string contenu, Guid? componentId = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return SourceResult.Failed("Session de diagnostic introuvable.");

        if (string.IsNullOrWhiteSpace(contenu))
            return SourceResult.Failed("Le fichier versé est vide.");

        N4Component? composant = null;
        if (componentId is not null)
            composant = await db.Components.AsNoTracking()
                .Include(c => c.Server)
                .FirstOrDefaultAsync(c => c.Id == componentId, ct);

        var source = new LogSource
        {
            SessionId = sessionId,
            ComponentId = composant?.Id,
            ComponentName = composant?.LogicalName,
            ComponentRole = composant?.Role,
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

        var lignes = contenu.Split('\n');
        source.LineCount = lignes.Length;

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

    private static async Task<SourceResult> EchouerAsync(
        N4SentinelDbContext db, LogSource source, string erreur, CancellationToken ct)
    {
        source.Error = erreur;
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);
        return SourceResult.Failed(erreur);
    }

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

                parSignature[signature.Code] = new LogFinding
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

            parMessage[cle] = new LogFinding
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
        var hypotheses = ConstruireHypotheses(constats, signatures, session.Id);

        db.Hypotheses.AddRange(hypotheses);

        // --- Verdict ------------------------------------------------------
        var concluantes = constats
            .Where(f => f.SignatureId is not null
                        && signatures.FirstOrDefault(s => s.Id == f.SignatureId)?.EstConcluante == true)
            .ToList();

        var limites = Limites(session, sourcesLues, sourcesEnEchec);

        if (constats.Count == 0)
        {
            session.Verdict = DiagnosticVerdict.RienDeConcluant;
            session.VerdictExplanation =
                "Aucune anomalie n'a été relevée dans ce qui a été analysé. "
                + "CE N'EST PAS UN CERTIFICAT DE BONNE SANTÉ : la panne peut se situer hors de la fenêtre "
                + "examinée, dans un journal qui n'a pas été collecté, ou ne rien écrire du tout. "
                + limites;
        }
        else if (concluantes.Count > 0)
        {
            var principale = concluantes.OrderByDescending(f => f.Severity)
                                        .ThenByDescending(f => f.OccurrenceCount).First();

            session.Verdict = DiagnosticVerdict.CauseCaracterisee;
            session.VerdictExplanation =
                $"Une signature connue a été reconnue : « {principale.Title} » "
                + $"({principale.OccurrenceCount} occurrence(s)). {principale.Meaning} "
                + limites;
        }
        else if (hypotheses.Count > 0 && hypotheses[0].Confidence >= 50)
        {
            session.Verdict = DiagnosticVerdict.PisteSerieuse;
            session.VerdictExplanation =
                $"Aucune signature connue n'établit de cause, mais les anomalies convergent vers un "
                + $"domaine : {Libelle(hypotheses[0].Domain)}. C'est une piste, pas une conclusion. "
                + limites;
        }
        else
        {
            session.Verdict = DiagnosticVerdict.AnomaliesSansCause;
            session.VerdictExplanation =
                $"{constats.Count} anomalie(s) relevée(s), mais elles ne dessinent aucune cause. "
                + "Elles sont présentées telles quelles : établir un lien entre elles serait une invention. "
                + limites;
        }

        session.AnalysedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

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

            hypotheses.Add(new DiagnosticHypothesis
            {
                SessionId = sessionId,
                Domain = groupe.Key,
                Confidence = confiance,
                Statement = Formuler(groupe.Key, membres),
                Evidence = string.Join(" · ", membres.Take(4)
                    .Select(f => $"{f.Title} ({f.OccurrenceCount}×, ligne {f.FirstLineNumber})")),
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
        _ => "domaine indéterminé"
    };

    // -----------------------------------------------------------------------
    // Analyse de ligne
    // -----------------------------------------------------------------------
    private static readonly Regex MotifHorodatage = new(
        @"(?<d>\d{4}-\d{2}-\d{2})[ T](?<t>\d{2}:\d{2}:\d{2})(?:[.,](?<ms>\d{1,3}))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
