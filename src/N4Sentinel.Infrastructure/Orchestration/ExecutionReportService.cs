using System.Text;
using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Rapport d'exécution (FR-028, recette AC-14).
///
/// Il est produit à partir de ce qui a été ENREGISTRÉ pendant l'opération, pas
/// reconstitué après coup : chaque étape porte sa preuve, sa cause d'échec ou
/// sa justification de contournement, écrites au moment où elles ont été
/// constatées.
///
/// CE RAPPORT NE MAQUILLE RIEN. Une étape dont le résultat n'a pas pu être
/// prouvé apparaît comme telle, une étape contournée nomme qui l'a contournée
/// et pourquoi, et une séquence interrompue dit ce qui n'a pas été fait. C'est
/// un document destiné à être transmis — à un éditeur, à une direction, à un
/// auditeur — et un rapport qui embellit ne survit pas au premier recoupement.
/// </summary>
public sealed class ExecutionReportService(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    public async Task<string?> BuildMarkdownAsync(Guid executionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var x = await db.Executions
            .AsNoTracking()
            .Include(e => e.Steps.OrderBy(s => s.Order))
            .Include(e => e.Environment)
            .FirstOrDefaultAsync(e => e.Id == executionId, ct);

        if (x is null) return null;

        var sb = new StringBuilder();

        sb.AppendLine($"# Rapport d'exécution — {x.WorkflowName}");
        sb.AppendLine();
        sb.AppendLine($"**{Libelle(x.Status)}**"
                    + (x.IsSimulation ? " · SIMULATION : aucune commande n'a été émise" : string.Empty));
        sb.AppendLine();

        // --- Identification --------------------------------------------------
        sb.AppendLine("## Identification");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Environnement | {x.EnvironmentCode}"
                    + (x.Environment?.Kind == EnvironmentKind.Production ? " — **PRODUCTION**" : string.Empty) + " |");
        sb.AppendLine($"| Workflow | {x.WorkflowName}, version {x.WorkflowVersion} |");
        sb.AppendLine($"| Corrélation | `{x.CorrelationId}` |");
        sb.AppendLine($"| Demandeur | {x.RequestedBy} |");
        sb.AppendLine($"| Motif | {x.Reason} |");
        sb.AppendLine($"| Ticket | {x.TicketReference ?? "—"} |");
        sb.AppendLine($"| Approbation | {(x.ApprovedBy is null ? "non requise" : $"{x.ApprovedBy}, le {Date(x.ApprovedAt)}")} |");
        sb.AppendLine($"| Lancée | {Date(x.StartedAt)} |");
        sb.AppendLine($"| Terminée | {Date(x.EndedAt)} |");
        sb.AppendLine($"| Durée | {(x.Duration is { } d ? StepExecutor.Formater(d) : "—")} |");
        sb.AppendLine();

        if (x.ExpectedImpact is { Length: > 0 })
        {
            sb.AppendLine($"> **Impact annoncé au lancement.** {x.ExpectedImpact}");
            sb.AppendLine();
        }

        // --- Contrôles préalables --------------------------------------------
        var controles = PreflightService.Relire(x.PreflightJson);
        if (controles.Count > 0)
        {
            sb.AppendLine("## Contrôles préalables");
            sb.AppendLine();
            sb.AppendLine($"Passés le {Date(x.PreflightAt)}.");
            sb.AppendLine();
            sb.AppendLine("| Contrôle | Résultat | Constat |");
            sb.AppendLine("|---|---|---|");

            foreach (var c in controles)
                sb.AppendLine($"| {c.Name} | {Marque(c.Outcome)} | {Cellule(c.Detail)} |");

            sb.AppendLine();
        }

        // --- Déroulement -----------------------------------------------------
        sb.AppendLine("## Déroulement");
        sb.AppendLine();
        sb.AppendLine("| # | Étape | Composant | Résultat | Durée |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var e in x.Steps.OrderBy(s => s.Order))
            sb.AppendLine($"| {e.Order} | {Cellule(e.Name)} | {Cellule(e.ComponentName ?? "—")} "
                        + $"| {MarqueEtape(e.State)} | {(e.Duration is { } sd ? StepExecutor.Formater(sd) : "—")} |");

        sb.AppendLine();

        // --- Preuves et causes -----------------------------------------------
        sb.AppendLine("## Preuves, réserves et causes");
        sb.AppendLine();

        var detaille = false;
        foreach (var e in x.Steps.OrderBy(s => s.Order))
        {
            var lignes = new List<string>();

            if (e.Evidence is { Length: > 0 }) lignes.Add($"- **Preuve.** {e.Evidence}");
            if (e.Error is { Length: > 0 }) lignes.Add($"- **Cause de l'échec.** {e.Error}");

            if (e.SkippedBy is not null)
                lignes.Add($"- **Contournée par {e.SkippedBy}.** {e.SkipReason}");

            if (e.ConfirmedBy is not null)
                lignes.Add($"- **Confirmée par {e.ConfirmedBy}** le {Date(e.ConfirmedAt)}."
                         + (e.OperatorNote is { Length: > 0 } ? $" Note : {e.OperatorNote}" : string.Empty));

            if (e.AttemptCount > 1)
                lignes.Add($"- **{e.AttemptCount} tentatives.**");

            if (lignes.Count == 0) continue;

            detaille = true;
            sb.AppendLine($"### {e.Order}. {e.Name} — {LibelleEtape(e.State)}");
            sb.AppendLine();
            foreach (var l in lignes) sb.AppendLine(l);
            sb.AppendLine();
        }

        if (!detaille)
        {
            sb.AppendLine("_Aucune étape n'a produit de preuve, de réserve ni de cause d'échec._");
            sb.AppendLine();
        }

        // --- Conclusion ------------------------------------------------------
        sb.AppendLine("## Conclusion");
        sb.AppendLine();

        if (x.Outcome is { Length: > 0 })
        {
            sb.AppendLine(x.Outcome);
            sb.AppendLine();
        }

        var prouvees = x.Steps.Count(s => s.State == ExecutionStepState.Reussi);
        var reserves = x.Steps.Count(s => s.State == ExecutionStepState.Avertissement);
        var echecs = x.Steps.Count(s => s.State == ExecutionStepState.Echec);
        var ignorees = x.Steps.Count(s => s.State == ExecutionStepState.Ignore);
        var nonFaites = x.Steps.Count(s => s.State is ExecutionStepState.Annule or ExecutionStepState.AVenir);

        sb.AppendLine($"Sur {x.Steps.Count} étape(s) : **{prouvees} avec preuve**, "
                    + $"{reserves} sans preuve (à confirmer), {echecs} en échec, "
                    + $"{ignorees} contournée(s), {nonFaites} non exécutée(s).");
        sb.AppendLine();

        if (reserves > 0)
            sb.AppendLine("> **Réserve.** Certaines étapes se sont terminées sans que le résultat ait pu être "
                        + "prouvé par un marqueur de journal. Le service Windows tournait ; l'état applicatif "
                        + "de ces composants reste à confirmer par un autre moyen.");

        if (nonFaites > 0)
            sb.AppendLine();

        if (nonFaites > 0)
            sb.AppendLine("> **Séquence incomplète.** Des étapes n'ont pas été exécutées, et celles qui "
                        + "l'ont été ne sont PAS défaites. L'écosystème est dans un état intermédiaire : "
                        + "il doit être constaté avant toute nouvelle opération.");

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"_N4 Sentinel — rapport produit le {DateTimeOffset.Now:dd/MM/yyyy à HH:mm} "
                    + $"à partir des constats enregistrés pendant l'opération `{x.CorrelationId}`._");

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    private static string Date(DateTimeOffset? d) =>
        d is null ? "—" : d.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

    /// <summary>Neutralise les barres verticales, qui casseraient la table Markdown.</summary>
    private static string Cellule(string texte) => texte.Replace("|", "\\|").Replace("\n", " ");

    private static string Marque(PreflightOutcome o) => o switch
    {
        PreflightOutcome.Reussi => "OK",
        PreflightOutcome.Avertissement => "RÉSERVE",
        PreflightOutcome.Echec => "**ÉCHEC**",
        _ => "sans objet"
    };

    private static string MarqueEtape(ExecutionStepState s) => s switch
    {
        ExecutionStepState.Reussi => "Prouvé",
        ExecutionStepState.Avertissement => "À confirmer",
        ExecutionStepState.Echec => "**Échec**",
        ExecutionStepState.Ignore => "Contournée",
        ExecutionStepState.Annule => "Non exécutée",
        ExecutionStepState.AVenir => "Non exécutée",
        _ => s.ToString()
    };

    private static string LibelleEtape(ExecutionStepState s) => s switch
    {
        ExecutionStepState.Reussi => "résultat prouvé",
        ExecutionStepState.Avertissement => "résultat non prouvé",
        ExecutionStepState.Echec => "échec",
        ExecutionStepState.Ignore => "contournée",
        ExecutionStepState.Annule => "non exécutée",
        _ => s.ToString()
    };

    private static string Libelle(ExecutionStatus s) => s switch
    {
        ExecutionStatus.TermineSucces => "Opération terminée — chaque étape a produit la preuve de son aboutissement",
        ExecutionStatus.TermineAvecAvertissements => "Opération terminée avec réserves",
        ExecutionStatus.Echec => "Opération en échec",
        ExecutionStatus.Annule => "Opération annulée",
        ExecutionStatus.ReconciliationRequise => "Opération interrompue — réconciliation requise",
        ExecutionStatus.EnCours => "Opération en cours",
        ExecutionStatus.EnPause => "Opération en pause",
        _ => s.ToString()
    };
}
