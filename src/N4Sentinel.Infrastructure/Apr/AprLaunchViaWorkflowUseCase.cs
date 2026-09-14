using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Orchestration.UseCases;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Apr;

/// <summary>
/// Transitions une APR Proposée/Programmée → EnCoursDExecution en créant une
/// exécution workflow via PrepareExecutionUseCase. Attache ResultingExecutionId
/// à l'APR pour suivi. Pattern : scellé Scoped, retour string? (null = OK).
/// </summary>
public sealed class AprLaunchViaWorkflowUseCase(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    PrepareExecutionUseCase prepareExecution)
{
    private static readonly AprStatus[] StatutsAutorises = new[]
    {
        AprStatus.Detectee,
        AprStatus.Proposee,
        AprStatus.Programmee,
    };

    public async Task<string?> ExecuteAsync(
        Guid aprId,
        Guid workflowId,
        string actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
            return "Lancement APR : l'identifiant de l'acteur est obligatoire.";

        if (workflowId == Guid.Empty)
            return "Lancement APR : WorkflowId invalide (Guid vide).";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var apr = await db.PreventiveRecommendedActions
            .FirstOrDefaultAsync(a => a.Id == aprId, ct);

        if (apr is null) return "APR introuvable.";
        if (!StatutsAutorises.Contains(apr.APRStatus))
            return $"Impossible de lancer une APR en statut {apr.APRStatus}. "
                 + $"Statuts autorisés : {string.Join(", ", StatutsAutorises)}.";

        var motif = $"[APR {apr.Code}] {apr.Title}. "
                  + $"Raison : {apr.Description}";
        if (motif.Length > 1000) motif = motif[..990] + "…[tronqué]";

        PrepareResult resultat;
        try
        {
            resultat = await prepareExecution.ExecuteAsync(
                workflowId: workflowId,
                requestedBy: actor,
                reason: motif,
                ct: ct);
        }
        catch (Exception ex)
        {
            apr.APRStatus = AprStatus.Proposee;
            apr.ModifiedAt = DateTimeOffset.UtcNow;
            apr.Notes = (string.IsNullOrEmpty(apr.Notes) ? string.Empty : apr.Notes + " | ")
                + $"[ÉCHEC LANCEMENT {apr.ModifiedAt:u}] {ex.GetType().Name} : {ex.Message}";
            await db.SaveChangesAsync(ct);
            return $"Erreur technique au lancement workflow : {ex.Message}";
        }

        if (!resultat.Succeeded)
        {
            apr.APRStatus = AprStatus.Proposee;
            apr.ModifiedAt = DateTimeOffset.UtcNow;
            apr.Notes = (string.IsNullOrEmpty(apr.Notes) ? string.Empty : apr.Notes + " | ")
                + $"[REFUS LANCEMENT {apr.ModifiedAt:u}] {resultat.Error}";
            await db.SaveChangesAsync(ct);
            return $"Préparation workflow refusée : {resultat.Error}";
        }

        var maintenant = DateTimeOffset.UtcNow;
        apr.APRStatus = AprStatus.EnCoursDExecution;
        apr.LaunchWorkflowId = workflowId;
        apr.ResultingExecutionId = resultat.ExecutionId;
        apr.ExecutedBy = actor;
        apr.ExecutedAt = maintenant;
        apr.ModifiedAt = maintenant;

        if (resultat.RequiresApproval)
        {
            apr.Notes = (string.IsNullOrEmpty(apr.Notes) ? string.Empty : apr.Notes + " | ")
                + $"[LANCEMENT {maintenant:u}] Exécution créée #{resultat.ExecutionId:N} — "
                + "attente circuit d'approbation avant démarrage effectif.";
        }

        await db.SaveChangesAsync(ct);
        return null;
    }
}
