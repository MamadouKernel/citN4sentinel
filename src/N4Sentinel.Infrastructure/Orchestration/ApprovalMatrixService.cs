using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Matrice de criticité (FR-013, FR-027) : le circuit d'approbation
/// configurable selon l'environnement, le scénario et le niveau de risque des
/// composants concernés.
///
/// UNE RÈGLE DE LA MATRICE EST UN PLANCHER, JAMAIS UN PLAFOND. Ce qu'un
/// workflow ou une étape exige déjà (<see cref="Workflow.RequiresApproval"/>)
/// reste exigé même si aucune règle ne matche ; la matrice ne fait qu'ajouter
/// des exigences, jamais en retirer — voir les appelants dans
/// <see cref="ExecutionService"/>.
/// </summary>
public sealed class ApprovalMatrixService(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    public async Task<List<ApprovalMatrixRule>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApprovalMatrixRules
            .AsNoTracking()
            .OrderByDescending(r => r.MinCriticality)
            .ToListAsync(ct);
    }

    /// <summary>
    /// La règle activée la plus spécifique qui matche cet environnement, ce
    /// scénario, et dont le seuil de criticité est atteint par
    /// <paramref name="criticiteMax"/>. À spécificité égale, le seuil de
    /// criticité le plus élevé l'emporte — c'est la règle la plus exigeante
    /// qui doit gagner, jamais la plus permissive.
    /// </summary>
    public async Task<ApprovalMatrixRule?> ResolveAsync(
        EnvironmentKind environmentKind, WorkflowKind workflowKind, CriticalityLevel criticiteMax,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var candidates = await db.ApprovalMatrixRules
            .AsNoTracking()
            .Where(r => r.Enabled
                     && r.MinCriticality <= criticiteMax
                     && (r.EnvironmentKind == null || r.EnvironmentKind == environmentKind)
                     && (r.WorkflowKind == null || r.WorkflowKind == workflowKind))
            .ToListAsync(ct);

        return candidates
            .OrderByDescending(r => r.Specificity)
            .ThenByDescending(r => r.MinCriticality)
            .FirstOrDefault();
    }

    public async Task<string?> SaveAsync(ApprovalMatrixRule regle, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // L'Id est généré à la construction (AuditableEntity), pas à
        // l'insertion : on ne peut donc pas distinguer création et mise à
        // jour par sa seule présence, il faut vérifier si la ligne existe.
        var existante = await db.ApprovalMatrixRules.FirstOrDefaultAsync(r => r.Id == regle.Id, ct);

        if (existante is null)
        {
            db.ApprovalMatrixRules.Add(regle);
        }
        else
        {
            existante.EnvironmentKind = regle.EnvironmentKind;
            existante.WorkflowKind = regle.WorkflowKind;
            existante.MinCriticality = regle.MinCriticality;
            existante.RequiresApproval = regle.RequiresApproval;
            existante.RequiresDoubleApproval = regle.RequiresDoubleApproval;
            existante.Enabled = regle.Enabled;
            existante.Notes = regle.Notes;
        }

        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var regle = await db.ApprovalMatrixRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (regle is null) return;

        db.ApprovalMatrixRules.Remove(regle);
        await db.SaveChangesAsync(ct);
    }
}
