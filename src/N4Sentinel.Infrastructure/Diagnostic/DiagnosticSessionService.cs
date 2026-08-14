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
public sealed class DiagnosticSessionService(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
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
