using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Diagnostic;

/// <summary>
/// Cycle de vie d'une session de diagnostic (FR-062 à FR-069).
///
/// UNE SESSION EST UNE INVESTIGATION, PAS UNE LECTURE. Elle porte ce que l'on
/// cherche, ce qui a été regardé, ce qui a été trouvé, et ce qui n'a pas pu
/// l'être. C'est cette dernière partie qui distingue un diagnostic d'une simple
/// consultation de journal.
/// </summary>
public sealed class DiagnosticSessionService(
    IDbContextFactory<N4SentinelDbContext> dbFactory, LogAnalysisService logAnalysis)
{
    /// <summary>Au-delà, une référence est jugée trop ancienne pour être utilisée sans avertissement (FR-066).</summary>
    private static readonly TimeSpan AgeMaximalReference = TimeSpan.FromDays(90);


    public async Task<Guid> CreateAsync(
        Guid environmentId, string title, string? reason, string? ticket, string requestedBy,
        DateTimeOffset? windowStart, DateTimeOffset? windowEnd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environnement = await db.Environments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == environmentId, ct);

        var session = new DiagnosticSession
        {
            EnvironmentId = environmentId,
            EnvironmentCode = environnement?.Code ?? "INCONNU",
            Title = string.IsNullOrWhiteSpace(title) ? "Diagnostic" : title.Trim(),
            Reason = reason,
            TicketReference = ticket,
            RequestedBy = requestedBy,
            WindowStart = windowStart,
            WindowEnd = windowEnd
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session.Id;
    }

    /// <summary>
    /// FR-068 : ouvre une session directement depuis une alerte, en
    /// identifiant le composant concerné — ou, pour une alerte de portée
    /// environnement, les composants candidats — et en lançant la collecte
    /// aussitôt. Ne remplace pas la sélection manuelle : elle reste possible
    /// depuis l'écran de diagnostic, notamment quand aucun candidat ne peut
    /// être déterminé.
    /// </summary>
    public async Task<CreateFromAlertResult> CreateFromAlertAsync(
        Guid alertId, string requestedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var alert = await db.Alerts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null) return CreateFromAlertResult.Failed("Alerte introuvable.");

        var environnement = await db.Environments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == alert.EnvironmentId, ct);

        var session = new DiagnosticSession
        {
            EnvironmentId = alert.EnvironmentId,
            EnvironmentCode = environnement?.Code ?? "INCONNU",
            Title = $"Diagnostic — {alert.Title}",
            Reason = $"Ouvert automatiquement depuis l'alerte « {alert.Title} » (FR-068). {alert.Detail}",
            RequestedBy = requestedBy,
            // Marge avant le premier signalement : la cause precede
            // generalement l'alerte elle-meme, pas l'inverse.
            WindowStart = alert.FirstOccurredAt.AddMinutes(-30),
            WindowEnd = DateTimeOffset.UtcNow,
            SourceAlertId = alert.Id
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        var candidats = await IdentifierComposantsCandidatsAsync(db, alert, ct);

        foreach (var composantId in candidats)
            await logAnalysis.CollectFromServerAsync(session.Id, composantId, ct);

        if (candidats.Count > 0)
            await logAnalysis.ConcludeAsync(session.Id, ct);

        return CreateFromAlertResult.Ok(session.Id, candidats.Count);
    }

    /// <summary>
    /// FR-059J : ouvre une session de diagnostic rattachée à UN fichier EDI
    /// précis, pas seulement au composant qui l'héberge. Le nom du fichier,
    /// son partenaire et son état constatent la cible dans le motif de la
    /// session — c'est ce lien-là qui manquait : un clic « Diagnostiquer »
    /// depuis une alerte EDI ouvrait un diagnostic générique du composant,
    /// sans jamais dire pour quel fichier.
    /// </summary>
    public async Task<CreateFromAlertResult> CreateFromEdiFileAsync(
        EdiFile fichier, Guid componentId, string requestedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composant = await db.Components.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == componentId, ct);
        if (composant is null) return CreateFromAlertResult.Failed("Composant introuvable.");

        var session = new DiagnosticSession
        {
            EnvironmentId = composant.EnvironmentId,
            EnvironmentCode = (await db.Environments.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == composant.EnvironmentId, ct))?.Code ?? "INCONNU",
            Title = $"Diagnostic EDI — {fichier.FileName}",
            Reason = $"Fichier « {fichier.FileName} » (partenaire : {fichier.Partner ?? "non classé"}, "
                   + $"statut : {fichier.Status}, âge : {Formater(fichier.Age)}"
                   + (fichier.ConsecutiveRejections > 0
                        ? $", {fichier.ConsecutiveRejections} échec(s) consécutif(s)"
                        : string.Empty)
                   + $") sur « {composant.LogicalName} ».",
            RequestedBy = requestedBy,
            WindowStart = fichier.FirstSeenAt.AddMinutes(-30),
            WindowEnd = DateTimeOffset.UtcNow
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        await logAnalysis.CollectFromServerAsync(session.Id, componentId, ct);
        await logAnalysis.ConcludeAsync(session.Id, ct);

        return CreateFromAlertResult.Ok(session.Id, 1);
    }

    private static string Formater(TimeSpan d) =>
        d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalHours} h";

    /// <summary>
    /// Une alerte de composant designe directement sa cible. Une alerte de
    /// portee environnement (ex. conflit de role Center/Standby) n'en
    /// designe aucune a elle seule : les candidats sont deduits de sa
    /// nature, jamais devines au hasard — une liste vide est un resultat
    /// honnete quand aucune regle ne s'applique, pas une erreur a masquer.
    /// </summary>
    private static async Task<List<Guid>> IdentifierComposantsCandidatsAsync(
        N4SentinelDbContext db, Alert alert, CancellationToken ct)
    {
        if (alert.ComponentId is { } id) return [id];

        if (alert.Kind == AlertKind.ConflitRoleActifCenter)
        {
            return await db.Components.AsNoTracking()
                .Where(c => c.EnvironmentId == alert.EnvironmentId
                            && (c.Role == ComponentRole.CenterNode || c.Role == ComponentRole.StandbyCenterNode))
                .Select(c => c.Id)
                .ToListAsync(ct);
        }

        return [];
    }

    public async Task<DiagnosticSession?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Sessions
            .AsNoTracking()
            .Include(s => s.Sources)
            .Include(s => s.Findings)
            .Include(s => s.Hypotheses.OrderBy(h => h.Rank))
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<List<DiagnosticSession>> GetRecentAsync(
        Guid? environmentId, int count = 40, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var requete = db.Sessions.AsNoTracking().AsQueryable();
        if (environmentId is not null) requete = requete.Where(s => s.EnvironmentId == environmentId);

        return await requete.OrderByDescending(s => s.CreatedAt).Take(count).ToListAsync(ct);
    }

    /// <summary>FR-066 : sessions pouvant servir de référence — marquées saines explicitement, jamais devinées.</summary>
    public async Task<List<DiagnosticSession>> GetBaselineCandidatesAsync(
        Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Sessions.AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId && s.IsReferenceBaseline)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// FR-066 : marque ou démarque une session comme référence saine. C'est
    /// une affirmation humaine — l'application ne peut pas déduire seule
    /// qu'une période a été « validée saine ».
    /// </summary>
    public async Task<string?> MarkAsBaselineAsync(Guid sessionId, bool isBaseline, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return "Session introuvable.";

        if (isBaseline && !session.HasBeenAnalysed)
            return "Une session doit être analysée avant de pouvoir servir de référence.";

        session.IsReferenceBaseline = isBaseline;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>FR-066 : choisit (ou retire) la session de référence utilisée pour la comparaison.</summary>
    public async Task<string?> SetReferenceAsync(
        Guid sessionId, Guid? referenceSessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return "Session introuvable.";

        if (referenceSessionId is { } refId)
        {
            var reference = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == refId, ct);
            if (reference is null) return "Session de référence introuvable.";
            if (!reference.IsReferenceBaseline)
                return "Cette session n'est pas marquée comme référence saine — marquez-la explicitement avant de vous en servir de comparaison.";
        }

        session.ReferenceSessionId = referenceSessionId;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// FR-066 : anomalies présentes dans la session courante mais absentes de
    /// la référence, avec l'âge et la complétude de cette dernière rendus
    /// visibles — une référence ancienne ou incomplète ne doit jamais être
    /// utilisée sans avertissement.
    /// </summary>
    public async Task<ReferenceComparison?> CompareToReferenceAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await GetAsync(sessionId, ct);
        if (session?.ReferenceSessionId is not { } refId) return null;

        var reference = await GetAsync(refId, ct);
        if (reference is null) return null;

        var motifsReference = reference.Findings
            .Select(f => f.SignatureCode ?? f.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nouveaux = session.Findings
            .Where(f => !motifsReference.Contains(f.SignatureCode ?? f.Title))
            .OrderByDescending(f => f.Severity)
            .ToList();

        var age = DateTimeOffset.UtcNow - reference.CreatedAt;

        return new ReferenceComparison(
            Reference: reference,
            NewFindings: nouveaux,
            ReferenceAgeDays: (int)age.TotalDays,
            IsStale: age > AgeMaximalReference,
            ReferenceScopeIncomplete: reference.Sources.Any(s => !s.Succeeded));
    }

    /// <summary>
    /// FR-061 : aligne les constats de toutes les sources sur une
    /// chronologie commune, ajustée de l'écart d'horloge mesuré à la
    /// collecte de chacune. Une entrée reste marquée incertaine dès que cet
    /// écart est inconnu ou dépasse le seuil d'une seconde — l'ordre affiché
    /// est alors probable, pas garanti, et le dit.
    /// </summary>
    public static List<TimelineEntry> BuildTimeline(DiagnosticSession session)
    {
        var sourcesParId = session.Sources.ToDictionary(s => s.Id);

        return session.Findings
            .Where(f => f.FirstSeenAt is not null)
            .Select(f =>
            {
                sourcesParId.TryGetValue(f.SourceId, out var source);
                var ecart = source?.ClockSkewSecondsAtCollection;
                var brut = f.FirstSeenAt!.Value;
                var ajuste = ecart is { } e ? brut - TimeSpan.FromSeconds(e) : brut;

                return new TimelineEntry(
                    AdjustedTime: ajuste,
                    RawTime: brut,
                    ComponentName: source?.ComponentName ?? "—",
                    HostName: source?.HostName,
                    Title: f.Title,
                    Severity: f.Severity,
                    ClockSkewSeconds: ecart,
                    IsUncertain: ecart is null || Math.Abs(ecart.Value) > 1.0);
            })
            .OrderBy(t => t.AdjustedTime)
            .ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return;

        db.Sessions.Remove(session);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Rapport de diagnostic, destiné à être transmis — à l'éditeur, à une
    /// direction, à un auditeur.
    ///
    /// Il énonce ses limites AVANT ses conclusions. Un rapport qui affirme sans
    /// dire ce qu'il n'a pas regardé se fait croire au-delà de ce qu'il vaut,
    /// et se retourne contre son auteur au premier recoupement.
    /// </summary>
    public async Task<string?> BuildMarkdownAsync(Guid id, CancellationToken ct = default)
    {
        var session = await GetAsync(id, ct);
        if (session is null) return null;

        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"# Diagnostic — {session.Title}");
        sb.AppendLine();
        sb.AppendLine($"**{LibelleVerdict(session.Verdict)}**");
        sb.AppendLine();

        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Environnement | {session.EnvironmentCode} |");
        sb.AppendLine($"| Demandeur | {session.RequestedBy} |");
        sb.AppendLine($"| Objet | {session.Reason ?? "—"} |");
        sb.AppendLine($"| Ticket | {session.TicketReference ?? "—"} |");
        sb.AppendLine($"| Fenêtre | {Plage(session)} |");
        sb.AppendLine($"| Analysé le | {session.AnalysedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "—"} |");
        sb.AppendLine();

        if (session.VerdictExplanation is { Length: > 0 })
        {
            sb.AppendLine("## Verdict");
            sb.AppendLine();
            sb.AppendLine(session.VerdictExplanation);
            sb.AppendLine();
        }

        // --- Sources ---------------------------------------------------------
        sb.AppendLine("## Journaux examinés");
        sb.AppendLine();
        sb.AppendLine("| Journal | Composant | Origine | Lignes | Secrets masqués | État |");
        sb.AppendLine("|---|---|---|---|---|---|");

        foreach (var s in session.Sources)
            sb.AppendLine($"| {Cellule(s.FileName)} | {Cellule(s.ComponentName ?? "—")} "
                        + $"| {(s.Origin == LogOriginKind.CollecteCiblee ? "collecte" : "import")} "
                        + $"| {s.LineCount} | {s.MaskedSecretCount} "
                        + $"| {(s.Succeeded ? (s.Truncated ? "fin de fichier seulement" : "complet") : "**non collecté**")} |");

        sb.AppendLine();

        if (session.Sources.Any(s => !s.Succeeded))
        {
            sb.AppendLine("> **Sources manquantes.** Certains journaux n'ont pas pu être collectés. "
                        + "Leur contenu n'a pas été examiné, et le verdict ne les couvre donc pas.");
            sb.AppendLine();
            foreach (var s in session.Sources.Where(x => !x.Succeeded))
                sb.AppendLine($"- {s.ComponentName ?? s.FileName} : {s.Error}");
            sb.AppendLine();
        }

        // --- Hypothèses ------------------------------------------------------
        if (session.Hypotheses.Count > 0)
        {
            sb.AppendLine("## Hypothèses");
            sb.AppendLine();
            sb.AppendLine("_Ce sont des hypothèses, présentées avec ce sur quoi elles reposent. "
                        + "Elles peuvent être contestées._");
            sb.AppendLine();

            foreach (var h in session.Hypotheses.OrderBy(x => x.Rank))
            {
                sb.AppendLine($"### {h.Rank}. {LogAnalysisService.Libelle(h.Domain)} — confiance {h.Confidence} %"
                            + (h.EstEtablie ? "" : " _(insuffisante pour conclure)_"));
                sb.AppendLine();
                sb.AppendLine(h.Statement);
                sb.AppendLine();
                sb.AppendLine($"- **Sur quoi elle repose.** {h.Evidence}");
                if (h.Recommendation is { Length: > 0 })
                    sb.AppendLine($"- **À vérifier.** {h.Recommendation}");
                sb.AppendLine();
            }
        }

        // --- Constats --------------------------------------------------------
        sb.AppendLine("## Constats");
        sb.AppendLine();

        if (session.Findings.Count == 0)
        {
            sb.AppendLine("_Aucune anomalie relevée dans ce qui a été analysé._");
            sb.AppendLine();
        }
        else
        {
            foreach (var f in session.Findings.OrderByDescending(x => x.Severity)
                                              .ThenByDescending(x => x.OccurrenceCount))
            {
                sb.AppendLine($"### {f.Title}");
                sb.AppendLine();
                sb.AppendLine($"- **Gravité.** {f.Severity} · **Domaine.** {LogAnalysisService.Libelle(f.Domain)}"
                            + (f.SignatureCode is null ? " · _non répertorié au catalogue_" : $" · signature `{f.SignatureCode}`"));
                sb.AppendLine($"- **Occurrences.** {f.OccurrenceCount}, à partir de la ligne {f.FirstLineNumber}"
                            + (f.FirstSeenAt is null ? "" : $", de {f.FirstSeenAt:HH:mm:ss} à {f.LastSeenAt:HH:mm:ss}"));

                if (f.Meaning is { Length: > 0 })
                    sb.AppendLine($"- **Ce que cela signifie.** {f.Meaning}");

                if (f.Remediation is { Length: > 0 })
                    sb.AppendLine($"- **Conduite à tenir.** {f.Remediation}");

                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(f.Context ?? f.SampleLine);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("_Les secrets ont été masqués avant enregistrement : ce rapport n'a jamais contenu "
                    + "de mot de passe en clair. Le contenu intégral des journaux n'est pas conservé par "
                    + "N4 Sentinel._");
        sb.AppendLine();
        sb.AppendLine($"_N4 Sentinel — rapport produit le {DateTimeOffset.Now:dd/MM/yyyy à HH:mm}._");

        return sb.ToString();
    }

    private static string Plage(DiagnosticSession s) =>
        s.WindowStart is null && s.WindowEnd is null
            ? "tout le contenu analysé"
            : $"{s.WindowStart?.ToLocalTime().ToString("dd/MM HH:mm") ?? "origine"} → "
              + $"{s.WindowEnd?.ToLocalTime().ToString("dd/MM HH:mm") ?? "fin"}";

    private static string Cellule(string t) => t.Replace("|", "\\|");

    public static string LibelleVerdict(DiagnosticVerdict v) => v switch
    {
        DiagnosticVerdict.CauseCaracterisee => "Cause caractérisée",
        DiagnosticVerdict.PisteSerieuse => "Piste sérieuse, sans certitude",
        DiagnosticVerdict.AnomaliesSansCause => "Anomalies relevées, cause non identifiée",
        DiagnosticVerdict.RienDeConcluant => "Rien de concluant — ce qui ne veut pas dire que tout va bien",
        _ => v.ToString()
    };
}

/// <summary>FR-068.</summary>
public sealed record CreateFromAlertResult
{
    public bool Succeeded { get; init; }
    public Guid SessionId { get; init; }

    /// <summary>Nombre de composants dont la collecte a été lancée automatiquement.</summary>
    public int ComponentsCollected { get; init; }

    public string? Error { get; init; }

    public static CreateFromAlertResult Ok(Guid sessionId, int composants) =>
        new() { Succeeded = true, SessionId = sessionId, ComponentsCollected = composants };

    public static CreateFromAlertResult Failed(string error) => new() { Succeeded = false, Error = error };
}

/// <summary>FR-066.</summary>
public sealed record ReferenceComparison(
    DiagnosticSession Reference,
    List<LogFinding> NewFindings,
    int ReferenceAgeDays,
    bool IsStale,
    bool ReferenceScopeIncomplete);

/// <summary>FR-061 : une entrée de la chronologie multi-sources.</summary>
public sealed record TimelineEntry(
    DateTimeOffset AdjustedTime,
    DateTimeOffset RawTime,
    string ComponentName,
    string? HostName,
    string Title,
    SignatureSeverity Severity,
    double? ClockSkewSeconds,
    bool IsUncertain);
