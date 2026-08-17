using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Diagnostic;

/// <summary>
/// Seuils de corrélation du moteur de diagnostic (FR-065) : ligne unique,
/// sur le même principe que <see cref="Retention.RetentionPolicyService"/>.
/// </summary>
public sealed class DiagnosticSettingsService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IAuditWriter auditWriter)
{
    public async Task<DiagnosticSettings> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DiagnosticSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new DiagnosticSettings();
    }

    public async Task<string?> SaveAsync(DiagnosticSettings parametres, string actor, CancellationToken ct = default)
    {
        if (parametres.HypothesisEstablishedThreshold is < 1 or > 100
            || parametres.ConclusiveSignatureConfidenceWeight is < 1 or > 100
            || parametres.SeriousLeadConfidenceThreshold is < 1 or > 100)
            return "Chaque seuil doit être compris entre 1 et 100.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existants = await db.DiagnosticSettings.FirstOrDefaultAsync(ct);
        if (existants is null)
        {
            db.DiagnosticSettings.Add(parametres);
        }
        else
        {
            existants.HypothesisEstablishedThreshold = parametres.HypothesisEstablishedThreshold;
            existants.ConclusiveSignatureConfidenceWeight = parametres.ConclusiveSignatureConfidenceWeight;
            existants.SeriousLeadConfidenceThreshold = parametres.SeriousLeadConfidenceThreshold;
        }

        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.Modification, AuditOutcome.Succes, actor,
            entityType: nameof(DiagnosticSettings),
            detail: $"Hypothèse établie ≥ {parametres.HypothesisEstablishedThreshold}, "
                  + $"signature concluante ≥ {parametres.ConclusiveSignatureConfidenceWeight}, "
                  + $"piste sérieuse ≥ {parametres.SeriousLeadConfidenceThreshold}.",
            ct: ct);

        return null;
    }
}
