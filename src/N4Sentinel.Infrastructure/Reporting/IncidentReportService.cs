using System.Text;
using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Diagnostic;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Reporting;

/// <summary>
/// FR-096 : rapport d'incident automatique, assemblé à partir de ce qu'une
/// session de diagnostic a RÉELLEMENT ENREGISTRÉ — détection, phases
/// traversées (§3.10.1), constats, hypothèses, SOP exécuté pour la résoudre
/// s'il y en a un. Rien n'est recomposé ni deviné : une phase jamais atteinte
/// reste absente du rapport plutôt que supposée.
///
/// Disponible dès la création de la session ; le contenu est d'autant plus
/// complet qu'elle a progressé dans le cycle. Le format Markdown produit ici
/// est le même que celui d'ExecutionReportService/HistoryService — il hérite
/// donc automatiquement de l'export PDF/Word (FR-093) via ReportDocumentService.
/// </summary>
public sealed class IncidentReportService(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    public async Task<string?> BuildMarkdownAsync(Guid diagnosticSessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions
            .AsNoTracking()
            .Include(s => s.Sources)
            .Include(s => s.Findings)
            .Include(s => s.Hypotheses)
            .Include(s => s.PhaseTransitions.OrderBy(t => t.EnteredAt))
            .FirstOrDefaultAsync(s => s.Id == diagnosticSessionId, ct);

        if (session is null) return null;

        var sopExecutions = await db.SopExecutions
            .AsNoTracking()
            .Include(x => x.Steps)
            .Where(x => x.SourceDiagnosticSessionId == diagnosticSessionId)
            .ToListAsync(ct);

        var sb = new StringBuilder();

        sb.AppendLine($"# Rapport d'incident — {session.Title}");
        sb.AppendLine();
        sb.AppendLine(session.Phase == DiagnosticPhase.ClotureEtCapitalisation
            ? "**Incident clôturé.**"
            : $"**Non clôturé — phase courante : {DiagnosticSessionService.LibellePhase(session.Phase)}.**");
        sb.AppendLine();

        // --- Chronologie -------------------------------------------------------
        sb.AppendLine("## Chronologie");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Environnement | {session.EnvironmentCode} |");
        sb.AppendLine($"| Détection et enregistrement | {Date(session.CreatedAt)} par {Cellule(session.RequestedBy)} |");

        var priseEnCharge = session.PhaseTransitions
            .FirstOrDefault(t => t.Phase != DiagnosticPhase.DetectionEtEnregistrement);
        sb.AppendLine("| Prise en charge | "
            + (priseEnCharge is null ? "—" : $"{Date(priseEnCharge.EnteredAt)} par {Cellule(priseEnCharge.EnteredBy)}")
            + " |");

        var cloture = session.PhaseTransitions
            .LastOrDefault(t => t.Phase == DiagnosticPhase.ClotureEtCapitalisation);
        sb.AppendLine($"| Fin (clôture) | {(cloture is null ? "en cours" : Date(cloture.EnteredAt))} |");

        if (cloture is not null)
            sb.AppendLine($"| Durée totale | {Duree(cloture.EnteredAt - session.CreatedAt)} |");

        sb.AppendLine($"| Ticket | {session.TicketReference ?? "—"} |");
        sb.AppendLine();

        // --- Symptôme ------------------------------------------------------
        if (session.Reason is { Length: > 0 })
        {
            sb.AppendLine("## Symptôme");
            sb.AppendLine();
            sb.AppendLine(session.Reason);
            sb.AppendLine();
        }

        // --- Composants concernés -------------------------------------------
        var composants = session.Sources
            .Select(s => s.ComponentName)
            .Where(n => n is { Length: > 0 })
            .Distinct()
            .ToList();

        sb.AppendLine("## Composants concernés");
        sb.AppendLine();
        sb.AppendLine(composants.Count == 0
            ? "_Aucun composant identifié dans les sources collectées._"
            : string.Join(", ", composants));
        sb.AppendLine();

        // --- Cause identifiée ou probable ------------------------------------
        sb.AppendLine("## Cause identifiée ou probable");
        sb.AppendLine();
        sb.AppendLine($"**{DiagnosticSessionService.LibelleVerdict(session.Verdict)}**");
        sb.AppendLine();

        if (session.VerdictExplanation is { Length: > 0 })
        {
            sb.AppendLine(session.VerdictExplanation);
            sb.AppendLine();
        }

        if (session.Hypotheses.Count > 0)
        {
            sb.AppendLine("| Hypothèse | Domaine | Confiance |");
            sb.AppendLine("|---|---|---|");
            foreach (var h in session.Hypotheses.OrderByDescending(h => h.Confidence).Take(5))
                sb.AppendLine($"| {Cellule(h.Statement)} | {LogAnalysisService.Libelle(h.Domain)} | {h.Confidence} % |");
            sb.AppendLine();
        }

        // --- Preuves ---------------------------------------------------------
        sb.AppendLine("## Preuves");
        sb.AppendLine();
        if (session.Findings.Count == 0)
        {
            sb.AppendLine("_Aucun constat enregistré._");
        }
        else
        {
            sb.AppendLine("| Constat | Gravité | Occurrences |");
            sb.AppendLine("|---|---|---|");
            foreach (var f in session.Findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.OccurrenceCount).Take(15))
                sb.AppendLine($"| {Cellule(f.Title)} | {f.Severity} | {f.OccurrenceCount} |");
        }
        sb.AppendLine();

        // --- Recommandations : pour eviter la recurrence -----------------------
        // Ce que l'ecran affiche deja pendant l'investigation ("Conduite a tenir",
        // "A verifier") mais qui disparaissait de l'export : precisement la partie
        // qui sert a eviter que le meme incident ne se reproduise.
        var recommandations = session.Hypotheses
            .Where(h => h.Recommendation is { Length: > 0 })
            .OrderByDescending(h => h.Confidence)
            .Select(h => (Origine: $"Piste — {LogAnalysisService.Libelle(h.Domain)} ({h.Confidence} %)", Texte: h.Recommendation!))
            .Concat(session.Findings
                .Where(f => f.Remediation is { Length: > 0 })
                .OrderByDescending(f => f.Severity).ThenByDescending(f => f.OccurrenceCount)
                .Select(f => (Origine: $"Constat — {f.Title}", Texte: f.Remediation!)))
            .DistinctBy(r => r.Texte)
            .ToList();

        sb.AppendLine("## Recommandations pour éviter la récurrence");
        sb.AppendLine();
        sb.AppendLine(recommandations.Count == 0
            ? "_Aucune conduite à tenir n'a été enregistrée pour les constats et hypothèses de cette session._"
            : "Ce que l'investigation a identifié comme conduite à tenir — à vérifier ou à corriger pour éviter que ce même incident ne se reproduise :");
        sb.AppendLine();
        foreach (var (origine, texte) in recommandations)
            sb.AppendLine($"- **{Cellule(origine)}** — {Cellule(texte)}");
        sb.AppendLine();

        // --- Actions et intervenants (phases + SOP) ---------------------------
        sb.AppendLine("## Actions et intervenants");
        sb.AppendLine();
        if (session.PhaseTransitions.Count == 0)
        {
            sb.AppendLine("_Aucune phase enregistrée au-delà de la détection._");
        }
        else
        {
            foreach (var t in session.PhaseTransitions)
                sb.AppendLine($"- **{DiagnosticSessionService.LibellePhase(t.Phase)}** — {Date(t.EnteredAt)} par {Cellule(t.EnteredBy)}"
                    + (t.Note is { Length: > 0 } ? $" : {Cellule(t.Note)}" : string.Empty));
        }
        sb.AppendLine();

        if (sopExecutions.Count > 0)
        {
            sb.AppendLine("## SOP associé(s)");
            sb.AppendLine();
            foreach (var x in sopExecutions)
            {
                var faites = x.Steps.Count(s => s.IsTerminal);
                sb.AppendLine($"- **{Cellule(x.SopTitle)}** (v{x.SopVersion}) — {LibelleStatutSop(x.Status)}, "
                    + $"{faites}/{x.Steps.Count} étape(s), démarré par {Cellule(x.StartedBy)} le {Date(x.StartedAt)}.");
            }
            sb.AppendLine();
        }

        // --- Statut final ------------------------------------------------------
        sb.AppendLine("## Statut final");
        sb.AppendLine();
        sb.AppendLine(session.Phase == DiagnosticPhase.ClotureEtCapitalisation
            ? $"Clôturé. {(cloture?.Note is { Length: > 0 } ? Cellule(cloture.Note) : string.Empty)}"
            : $"Non clôturé — phase courante : {DiagnosticSessionService.LibellePhase(session.Phase)}.");
        sb.AppendLine();

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"_N4 Sentinel — rapport d'incident produit le {DateTimeOffset.Now:dd/MM/yyyy à HH:mm} "
                    + "à partir des constats enregistrés pendant l'investigation._");

        return sb.ToString();
    }

    private static string Date(DateTimeOffset? d) =>
        d is null ? "—" : d.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

    private static string Duree(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : $"{d.Minutes} min {d.Seconds} s";

    private static string Cellule(string texte) => texte.Replace("|", "\\|").Replace("\n", " ");

    private static string LibelleStatutSop(SopExecutionStatus s) => s switch
    {
        SopExecutionStatus.EnCours => "en cours",
        SopExecutionStatus.Termine => "terminé",
        SopExecutionStatus.Abandonne => "abandonné",
        _ => s.ToString()
    };
}
