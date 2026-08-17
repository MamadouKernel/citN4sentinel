using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Retention;

/// <summary>
/// Politique de rétention (FR-079, SEC-009) : configuration, prévisualisation
/// et application. La purge n'est JAMAIS silencieuse — un administrateur voit
/// toujours ce qui serait supprimé avant de le confirmer, sur le même principe
/// que les autres actions destructives du produit.
///
/// L'AUDIT N'EST JAMAIS PURGÉ ICI (voir <see cref="RetentionPolicy"/>) : seuls
/// les journaux de diagnostic et l'historique d'exécution le sont, et
/// uniquement ce qui est clos/terminé - jamais une investigation ou une
/// opération en cours.
/// </summary>
public sealed class RetentionPolicyService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IAuditWriter auditWriter,
    ILogger<RetentionPolicyService> logger)
{
    // WorkflowExecution.IsActive est une propriete calculee en C#, non
    // mappee : EF ne peut pas la traduire en SQL. On reexprime la meme
    // definition avec l'enum, seule traduisible.
    private static readonly ExecutionStatus[] StatutsActifs =
    [
        ExecutionStatus.EnCours, ExecutionStatus.EnPause, ExecutionStatus.AnnulationDemandee,
        ExecutionStatus.EnAttenteApprobation, ExecutionStatus.EnPreparation
    ];

    public async Task<RetentionPolicy> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var politique = await db.RetentionPolicies.AsNoTracking().FirstOrDefaultAsync(ct);
        return politique ?? new RetentionPolicy();
    }

    public async Task<string?> SaveAsync(RetentionPolicy politique, string actor, CancellationToken ct = default)
    {
        if (politique.LogsRetentionDays < 1 || politique.ReportsRetentionDays < 1
            || politique.AuditRetentionDays < 1 || politique.SignalsRetentionDays < 1)
            return "Chaque délai de rétention doit être d'au moins 1 jour.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existante = await db.RetentionPolicies.FirstOrDefaultAsync(ct);
        if (existante is null)
        {
            db.RetentionPolicies.Add(politique);
        }
        else
        {
            existante.LogsRetentionDays = politique.LogsRetentionDays;
            existante.ReportsRetentionDays = politique.ReportsRetentionDays;
            existante.AuditRetentionDays = politique.AuditRetentionDays;
            existante.SignalsRetentionDays = politique.SignalsRetentionDays;
        }

        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.Modification, AuditOutcome.Succes, actor,
            entityType: nameof(RetentionPolicy),
            detail: $"Logs {politique.LogsRetentionDays} j, rapports {politique.ReportsRetentionDays} j, "
                  + $"signaux {politique.SignalsRetentionDays} j, audit (référence) {politique.AuditRetentionDays} j.",
            ct: ct);

        return null;
    }

    /// <summary>Ce que la purge supprimerait, sans rien supprimer.</summary>
    public async Task<RetentionPreview> PreviewAsync(CancellationToken ct = default)
    {
        var politique = await GetAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var seuilLogs = DateTimeOffset.UtcNow.AddDays(-politique.LogsRetentionDays);
        var seuilRapports = DateTimeOffset.UtcNow.AddDays(-politique.ReportsRetentionDays);

        var sessionsEligibles = db.Sessions
            .Where(s => s.Phase == DiagnosticPhase.ClotureEtCapitalisation && s.CreatedAt < seuilLogs)
            .Select(s => s.Id);

        var sources = await db.Sources.CountAsync(s => sessionsEligibles.Contains(s.SessionId), ct);
        var constats = await db.Findings.CountAsync(f => sessionsEligibles.Contains(f.SessionId), ct);

        var executions = await db.Executions.CountAsync(
            e => !StatutsActifs.Contains(e.Status) && e.EndedAt != null && e.EndedAt < seuilRapports, ct);

        var seuilSignaux = DateTimeOffset.UtcNow.AddDays(-politique.SignalsRetentionDays);
        var signaux = await db.ComponentSignals.CountAsync(s => s.CapturedAt < seuilSignaux, ct);

        return new RetentionPreview
        {
            LogsRetentionDays = politique.LogsRetentionDays,
            ReportsRetentionDays = politique.ReportsRetentionDays,
            SignalsRetentionDays = politique.SignalsRetentionDays,
            LogSourcesToDelete = sources,
            LogFindingsToDelete = constats,
            ExecutionsToDelete = executions,
            SignalsToDelete = signaux
        };
    }

    /// <summary>
    /// Applique la purge : extraits de journal des sessions closes au-delà du
    /// délai configuré, exécutions terminées au-delà du délai configuré. La
    /// session de diagnostic elle-même (titre, verdict, phases) est conservée.
    /// </summary>
    public async Task<RetentionPreview> ApplyAsync(string actor, CancellationToken ct = default)
    {
        var apercu = await PreviewAsync(ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var seuilLogs = DateTimeOffset.UtcNow.AddDays(-apercu.LogsRetentionDays);
        var seuilRapports = DateTimeOffset.UtcNow.AddDays(-apercu.ReportsRetentionDays);

        var sessionsEligibles = db.Sessions
            .Where(s => s.Phase == DiagnosticPhase.ClotureEtCapitalisation && s.CreatedAt < seuilLogs)
            .Select(s => s.Id);

        var findingsASupprimer = db.Findings.Where(f => sessionsEligibles.Contains(f.SessionId));
        var sourcesASupprimer = db.Sources.Where(s => sessionsEligibles.Contains(s.SessionId));

        var findingsSupprimees = await findingsASupprimer.ExecuteDeleteAsync(ct);
        var sourcesSupprimees = await sourcesASupprimer.ExecuteDeleteAsync(ct);

        var executionsASupprimer = db.Executions.Where(
            e => !StatutsActifs.Contains(e.Status) && e.EndedAt != null && e.EndedAt < seuilRapports);
        var executionsSupprimees = await executionsASupprimer.ExecuteDeleteAsync(ct);

        var seuilSignaux = DateTimeOffset.UtcNow.AddDays(-apercu.SignalsRetentionDays);
        var signauxSupprimes = await db.ComponentSignals.Where(s => s.CapturedAt < seuilSignaux).ExecuteDeleteAsync(ct);

        logger.LogInformation(
            "Purge de rétention appliquée par {Acteur} : {Sources} extrait(s) de journal, "
            + "{Constats} constat(s), {Executions} exécution(s), {Signaux} relevé(s) de supervision supprimé(s).",
            actor, sourcesSupprimees, findingsSupprimees, executionsSupprimees, signauxSupprimes);

        await auditWriter.WriteAsync(
            AuditAction.Suppression, AuditOutcome.Succes, actor,
            entityType: nameof(RetentionPolicy),
            detail: $"Purge appliquée : {sourcesSupprimees} source(s) de journal, "
                  + $"{findingsSupprimees} constat(s), {executionsSupprimees} exécution(s) terminée(s), "
                  + $"{signauxSupprimes} relevé(s) de supervision.",
            ct: ct);

        return apercu with
        {
            LogSourcesToDelete = sourcesSupprimees,
            LogFindingsToDelete = findingsSupprimees,
            ExecutionsToDelete = executionsSupprimees,
            SignalsToDelete = signauxSupprimes
        };
    }
}

public sealed record RetentionPreview
{
    public int LogsRetentionDays { get; init; }
    public int ReportsRetentionDays { get; init; }
    public int SignalsRetentionDays { get; init; }
    public int LogSourcesToDelete { get; init; }
    public int LogFindingsToDelete { get; init; }
    public int ExecutionsToDelete { get; init; }
    public int SignalsToDelete { get; init; }
}
