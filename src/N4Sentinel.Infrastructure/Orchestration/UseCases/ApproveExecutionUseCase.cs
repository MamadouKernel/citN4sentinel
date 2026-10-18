using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration.UseCases;

public sealed class ApproveExecutionUseCase(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    MeriseBusinessPolicyService merisePolicies)
{
    public async Task<string?> ExecuteAsync(
        Guid executionId, string approvedBy,
        bool approverIsSolutionAdmin = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";

        if (execution.Status != ExecutionStatus.EnAttenteApprobation)
            return $"Cette exécution est en état {execution.Status} : elle n'attend pas d'approbation.";

        // L'ADMINISTRATEUR-SOLUTION N'A PAS DE LIMITE (décision d'exploitation,
        // autorisée explicitement par l'exploitant).
        //
        // Ce compte détient déjà toutes les capacités ; son approbation lève
        // l'opération en un seul geste — y compris sa PROPRE demande, et y
        // compris quand une double approbation serait sinon exigée. Le contrôle
        // à quatre yeux ne s'applique donc plus à ce rôle : c'est un choix
        // assumé, pas un oubli.
        //
        // Ce qui demeure, et qui rend la trace exploitable : l'identité réelle
        // de l'acteur est inscrite (ApprovedBy), horodatée, et la piste d'audit
        // nomme qui a demandé ET approuvé. Pour rétablir une séparation stricte
        // en Production, on crée un second compte approbateur — on ne bride pas
        // celui-ci.
        if (approverIsSolutionAdmin)
        {
            execution.ApprovedBy = approvedBy;
            execution.ApprovedAt = DateTimeOffset.UtcNow;

            if (execution.RequiresDoubleApproval && execution.SecondApprovedBy is null)
            {
                execution.SecondApprovedBy = approvedBy;
                execution.SecondApprovedAt = DateTimeOffset.UtcNow;
            }

            execution.Status = ExecutionStatus.EnPreparation;
            await db.SaveChangesAsync(ct);
            return null;
        }

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

        var estProd = await db.Environments.AsNoTracking()
            .Where(e => e.Id == execution.EnvironmentId)
            .Select(e => e.IsProduction)
            .FirstOrDefaultAsync(ct);

        var rgTroisDistincts = merisePolicies.ValidateDoubleApprovalDistinctActors(
            execution.RequestedBy,
            execution.ApprovedBy,
            approvedBy,
            estProd,
            execution.RequiresDoubleApproval);

        if (rgTroisDistincts is not null) return rgTroisDistincts;

        execution.SecondApprovedBy = approvedBy;
        execution.SecondApprovedAt = DateTimeOffset.UtcNow;
        execution.Status = ExecutionStatus.EnPreparation;

        await db.SaveChangesAsync(ct);
        return null;
    }
}
