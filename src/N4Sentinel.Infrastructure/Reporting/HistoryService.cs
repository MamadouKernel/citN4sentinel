using System.Text;
using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Diagnostic;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Reporting;

/// <summary>
/// Historique consolidé et dossier d'escalade (FR-090, FR-093, FR-067).
///
/// L'historique rassemble ce que les autres écrans montrent séparément :
/// opérations, incidents, diagnostics, actions d'administration. Séparés, ils
/// répondent chacun à une question ; ensemble, ils répondent à la seule qui
/// compte le jour d'une escalade — <em>que s'est-il passé sur cet environnement
/// entre telle et telle heure</em>.
///
/// LE DOSSIER D'ESCALADE EST DESTINÉ À SORTIR DE L'ENTREPRISE. Il part chez
/// l'éditeur, chez un prestataire, en pièce jointe d'un courriel. Il ne doit
/// donc contenir aucun secret, et il doit énoncer ses limites : ce qu'il ne
/// couvre pas, et ce qui n'a pas pu être mesuré.
/// </summary>
public sealed class HistoryService(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    /// <summary>Rassemble les événements d'une période, toutes natures confondues.</summary>
    public async Task<HistoryPage> GetAsync(
        Guid? environmentId, DateTimeOffset from, DateTimeOffset to,
        HistoryFilter filter = HistoryFilter.Tout, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var evenements = new List<HistoryEvent>();

        // --- Opérations -----------------------------------------------------
        if (filter is HistoryFilter.Tout or HistoryFilter.Operations)
        {
            var executions = await db.Executions
                .AsNoTracking()
                .Where(x => x.CreatedAt >= from && x.CreatedAt <= to
                            && (environmentId == null || x.EnvironmentId == environmentId))
                .ToListAsync(ct);

            evenements.AddRange(executions.Select(x => new HistoryEvent
            {
                At = x.StartedAt ?? x.CreatedAt,
                Kind = HistoryKind.Operation,
                EnvironmentCode = x.EnvironmentCode,
                Title = $"{x.WorkflowName} (v{x.WorkflowVersion})"
                      + (x.IsSimulation ? " — simulation" : string.Empty),
                Detail = x.Outcome ?? x.Reason ?? string.Empty,
                Actor = x.RequestedBy,
                Outcome = LibelleExecution(x.Status),
                Severity = x.Status switch
                {
                    ExecutionStatus.Echec => HistorySeverity.Grave,
                    ExecutionStatus.TermineAvecAvertissements => HistorySeverity.Attention,
                    ExecutionStatus.ReconciliationRequise => HistorySeverity.Attention,
                    ExecutionStatus.Annule => HistorySeverity.Attention,
                    _ => HistorySeverity.Normal
                },
                Reference = x.CorrelationId,
                Link = $"/operations/{x.Id}"
            }));
        }

        // --- Alertes --------------------------------------------------------
        if (filter is HistoryFilter.Tout or HistoryFilter.Incidents)
        {
            var alertes = await db.Alerts
                .AsNoTracking()
                .Where(a => a.FirstOccurredAt >= from && a.FirstOccurredAt <= to
                            && (environmentId == null || a.EnvironmentId == environmentId))
                .ToListAsync(ct);

            evenements.AddRange(alertes.Select(a => new HistoryEvent
            {
                At = a.FirstOccurredAt,
                Kind = HistoryKind.Incident,
                EnvironmentCode = a.ComponentName,
                Title = a.Title,
                Detail = a.Detail,
                Outcome = a.Status.ToString(),
                Severity = a.Severity switch
                {
                    AlertSeverity.Critique => HistorySeverity.Grave,
                    AlertSeverity.Avertissement => HistorySeverity.Attention,
                    _ => HistorySeverity.Normal
                },
                Reference = a.OccurrenceCount > 1 ? $"{a.OccurrenceCount} occurrences" : null,
                Link = "/alertes"
            }));
        }

        // --- Diagnostics ----------------------------------------------------
        if (filter is HistoryFilter.Tout or HistoryFilter.Diagnostics)
        {
            var sessions = await db.Sessions
                .AsNoTracking()
                .Where(s => s.CreatedAt >= from && s.CreatedAt <= to
                            && (environmentId == null || s.EnvironmentId == environmentId))
                .ToListAsync(ct);

            evenements.AddRange(sessions.Select(s => new HistoryEvent
            {
                At = s.CreatedAt,
                Kind = HistoryKind.Diagnostic,
                EnvironmentCode = s.EnvironmentCode,
                Title = s.Title,
                Detail = s.VerdictExplanation ?? s.Reason ?? string.Empty,
                Actor = s.RequestedBy,
                Outcome = s.HasBeenAnalysed
                    ? DiagnosticSessionService.LibelleVerdict(s.Verdict)
                    : "non analysé",
                Severity = s.Verdict switch
                {
                    DiagnosticVerdict.CauseCaracterisee => HistorySeverity.Attention,
                    DiagnosticVerdict.PisteSerieuse => HistorySeverity.Attention,
                    _ => HistorySeverity.Normal
                },
                Reference = s.TicketReference,
                Link = $"/diagnostics/{s.Id}"
            }));
        }

        // --- Administration -------------------------------------------------
        if (filter is HistoryFilter.Tout or HistoryFilter.Administration)
        {
            var audits = await db.AuditEntries
                .AsNoTracking()
                .Where(a => a.OccurredAt >= from && a.OccurredAt <= to)
                .OrderByDescending(a => a.OccurredAt)
                .Take(500)
                .ToListAsync(ct);

            evenements.AddRange(audits.Select(a => new HistoryEvent
            {
                At = a.OccurredAt,
                Kind = HistoryKind.Administration,
                Title = $"{a.Action} — {a.EntityType}"
                      + (a.EntityLabel is { Length: > 0 } ? $" « {a.EntityLabel} »" : string.Empty),
                Detail = a.Detail ?? a.Reason ?? string.Empty,
                Actor = a.Actor,
                Outcome = a.Outcome.ToString(),
                Reference = a.CorrelationId,
                Severity = a.Outcome == AuditOutcome.Succes ? HistorySeverity.Normal : HistorySeverity.Attention,
                Link = "/admin/audit"
            }));
        }

        return new HistoryPage
        {
            From = from,
            To = to,
            Events = evenements.OrderByDescending(e => e.At).ToList()
        };
    }

    /// <summary>
    /// Dossier d'escalade : ce qui s'est passé sur la période, en un document
    /// transmissible.
    ///
    /// Il énonce sa portée AVANT son contenu. Un dossier qui ne dit pas ce
    /// qu'il ne couvre pas laisse croire qu'il couvre tout.
    /// </summary>
    public async Task<string> BuildEscalationPackAsync(
        Guid? environmentId, DateTimeOffset from, DateTimeOffset to,
        string requestedBy, string? context, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environnement = environmentId is null
            ? null
            : await db.Environments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == environmentId, ct);

        var page = await GetAsync(environmentId, from, to, HistoryFilter.Tout, ct);

        var sb = new StringBuilder();

        sb.AppendLine("# Dossier d'escalade — N4 Sentinel");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Environnement | {environnement?.Code ?? "tous"}"
                    + (environnement?.IsProduction == true ? " — **PRODUCTION**" : string.Empty) + " |");
        sb.AppendLine($"| Période | du {from.ToLocalTime():dd/MM/yyyy HH:mm} au {to.ToLocalTime():dd/MM/yyyy HH:mm} |");
        sb.AppendLine($"| Établi par | {requestedBy} |");
        sb.AppendLine($"| Établi le | {DateTimeOffset.Now:dd/MM/yyyy HH:mm} |");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.AppendLine("## Contexte");
            sb.AppendLine();
            sb.AppendLine(context.Trim());
            sb.AppendLine();
        }

        // --- Portée ---------------------------------------------------------
        sb.AppendLine("## Portée de ce dossier");
        sb.AppendLine();
        sb.AppendLine("Ce dossier rassemble ce que N4 Sentinel a **enregistré** sur la période : "
                    + "opérations lancées depuis l'application, alertes de supervision, sessions de "
                    + "diagnostic et actions d'administration.");
        sb.AppendLine();
        sb.AppendLine("Il ne couvre pas :");
        sb.AppendLine();
        sb.AppendLine("- les actions menées **hors de l'application** — arrêt manuel d'un service, "
                    + "intervention directe sur un serveur, modification de configuration N4 ;");
        sb.AppendLine("- les composants qui ne sont **pas déclarés** au référentiel ;");
        sb.AppendLine("- les périodes pendant lesquelles la supervision n'a pas pu joindre un serveur.");
        sb.AppendLine();
        sb.AppendLine("> Les secrets ont été masqués avant enregistrement : ce dossier n'a jamais contenu "
                    + "de mot de passe en clair.");
        sb.AppendLine();

        // --- Synthèse -------------------------------------------------------
        var operations = page.Events.Where(e => e.Kind == HistoryKind.Operation).ToList();
        var incidents = page.Events.Where(e => e.Kind == HistoryKind.Incident).ToList();
        var diagnostics = page.Events.Where(e => e.Kind == HistoryKind.Diagnostic).ToList();

        sb.AppendLine("## Synthèse");
        sb.AppendLine();
        sb.AppendLine($"- **{operations.Count} opération(s)**, dont "
                    + $"{operations.Count(e => e.Severity == HistorySeverity.Grave)} en échec.");
        sb.AppendLine($"- **{incidents.Count} alerte(s)**, dont "
                    + $"{incidents.Count(e => e.Severity == HistorySeverity.Grave)} critique(s).");
        sb.AppendLine($"- **{diagnostics.Count} diagnostic(s)** conduits sur la période.");
        sb.AppendLine();

        if (page.Events.Count == 0)
        {
            sb.AppendLine("> **Aucun événement enregistré sur cette période.** "
                        + "Cela ne signifie pas qu'il ne s'est rien passé : les actions menées hors de "
                        + "l'application n'y figurent pas.");
            sb.AppendLine();
        }

        // --- Chronologie ----------------------------------------------------
        if (page.Events.Count > 0)
        {
            sb.AppendLine("## Chronologie");
            sb.AppendLine();
            sb.AppendLine("| Horodatage | Nature | Événement | Acteur | Issue |");
            sb.AppendLine("|---|---|---|---|---|");

            foreach (var e in page.Events.OrderBy(x => x.At))
                sb.AppendLine($"| {e.At.ToLocalTime():dd/MM HH:mm:ss} | {Libelle(e.Kind)} "
                            + $"| {Cellule(e.Title)} | {Cellule(e.Actor ?? "—")} "
                            + $"| {Cellule(e.Outcome ?? "—")} |");

            sb.AppendLine();
        }

        // --- Détail des opérations non nominales -----------------------------
        var anormales = operations
            .Where(e => e.Severity != HistorySeverity.Normal)
            .OrderBy(e => e.At)
            .ToList();

        if (anormales.Count > 0)
        {
            sb.AppendLine("## Opérations non nominales");
            sb.AppendLine();

            foreach (var e in anormales)
            {
                sb.AppendLine($"### {e.At.ToLocalTime():dd/MM HH:mm} — {e.Title}");
                sb.AppendLine();
                sb.AppendLine($"- **Issue.** {e.Outcome}");
                sb.AppendLine($"- **Demandeur.** {e.Actor ?? "—"}");
                if (e.Reference is { Length: > 0 })
                    sb.AppendLine($"- **Corrélation.** `{e.Reference}`");
                if (e.Detail is { Length: > 0 })
                    sb.AppendLine($"- **Détail.** {e.Detail}");
                sb.AppendLine();
            }
        }

        // --- Diagnostics ----------------------------------------------------
        if (diagnostics.Count > 0)
        {
            sb.AppendLine("## Diagnostics");
            sb.AppendLine();

            foreach (var e in diagnostics.OrderBy(x => x.At))
            {
                sb.AppendLine($"### {e.At.ToLocalTime():dd/MM HH:mm} — {e.Title}");
                sb.AppendLine();
                sb.AppendLine($"- **Verdict.** {e.Outcome}");
                if (e.Detail is { Length: > 0 })
                    sb.AppendLine($"- {e.Detail}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"_N4 Sentinel — dossier produit le {DateTimeOffset.Now:dd/MM/yyyy à HH:mm} "
                    + "à partir des enregistrements de l'application._");

        return sb.ToString();
    }

    private static string Cellule(string t) => t.Replace("|", "\\|").Replace("\n", " ");

    private static string Libelle(HistoryKind k) => k switch
    {
        HistoryKind.Operation => "opération",
        HistoryKind.Incident => "alerte",
        HistoryKind.Diagnostic => "diagnostic",
        HistoryKind.Administration => "administration",
        _ => k.ToString()
    };

    private static string LibelleExecution(ExecutionStatus s) => s switch
    {
        ExecutionStatus.TermineSucces => "terminée, prouvée",
        ExecutionStatus.TermineAvecAvertissements => "terminée avec réserves",
        ExecutionStatus.Echec => "échec",
        ExecutionStatus.Annule => "annulée",
        ExecutionStatus.ReconciliationRequise => "réconciliation requise",
        ExecutionStatus.EnCours => "en cours",
        ExecutionStatus.EnPause => "en pause",
        _ => s.ToString()
    };
}

public enum HistoryKind
{
    Operation = 0,
    Incident = 1,
    Diagnostic = 2,
    Administration = 3
}

public enum HistorySeverity
{
    Normal = 0,
    Attention = 1,
    Grave = 2
}

public enum HistoryFilter
{
    Tout = 0,
    Operations = 1,
    Incidents = 2,
    Diagnostics = 3,
    Administration = 4
}

public sealed record HistoryEvent
{
    public DateTimeOffset At { get; init; }
    public HistoryKind Kind { get; init; }
    public string? EnvironmentCode { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string? Actor { get; init; }
    public string? Outcome { get; init; }
    public HistorySeverity Severity { get; init; }
    public string? Reference { get; init; }
    public string? Link { get; init; }
}

public sealed record HistoryPage
{
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public List<HistoryEvent> Events { get; init; } = [];
}
