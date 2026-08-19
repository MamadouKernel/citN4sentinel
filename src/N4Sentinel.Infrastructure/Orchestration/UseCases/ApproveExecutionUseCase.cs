using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration.UseCases;

public sealed class ApproveExecutionUseCase(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    public async Task<string?> ExecuteAsync(Guid executionId, string approvedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";

        if (execution.Status != ExecutionStatus.EnAttenteApprobation)
            return $"Cette exécution est en état {execution.Status} : elle n'attend pas d'approbation.";

        if (string.Equals(execution.RequestedBy, approvedBy, StringComparison.OrdinalIgnoreCase))
            return "Le demandeur ne peut pas approuver sa propre opération. "
                 + "L'approbation doit venir d'une autre personne.";

        if (execution.ApprovedBy is null)
        {
            execution.ApprovedBy = approvedBy;
            execution.ApprovedAt = DateTimeOffset.UtcNow;

            if (!execution.RequiresDoubleApproval)
                execution.Status = ExecutionStatus.EnPreparation;

            await db.SaveChangesAsync(ct);
            return null;
        }

        if (!execution.RequiresDoubleApproval)
            return "Cette exécution est déjà approuvée.";

        if (string.Equals(execution.ApprovedBy, approvedBy, StringComparison.OrdinalIgnoreCase))
            return "Le second approbateur doit être une personne différente du premier — "
                 + "un double regard par la même personne n'en est pas un.";

        execution.SecondApprovedBy = approvedBy;
        execution.SecondApprovedAt = DateTimeOffset.UtcNow;
        execution.Status = ExecutionStatus.EnPreparation;

        await db.SaveChangesAsync(ct);
        return null;
    }
}
