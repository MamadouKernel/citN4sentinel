using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Orchestration.UseCases;
using N4Sentinel.Infrastructure.Persistence;
using Xunit;

namespace N4Sentinel.Tests;

/// <summary>
/// Approbation : l'Administrateur-Solution n'a pas de limite (décision
/// d'exploitation, autorisée explicitement). Son approbation lève l'opération
/// en un geste, y compris sa propre demande et une double approbation ; les
/// autres comptes restent soumis au contrôle à quatre yeux.
/// </summary>
public sealed class ApprobationAdminTests
{
    [Fact]
    public async Task AdminSolution_peut_approuver_sa_propre_operation()
    {
        var f = Fabrique();
        var id = await SeedExecutionAsync(f, requestedBy: "admin", doubleApprobation: false);
        var usecase = new ApproveExecutionUseCase(f, new MeriseBusinessPolicyService(f));

        var err = await usecase.ExecuteAsync(id, "admin", approverIsSolutionAdmin: true);

        Assert.Null(err);
        await using var db = f.CreateDbContext();
        var exec = await db.Executions.FirstAsync(e => e.Id == id);
        Assert.Equal(ExecutionStatus.EnPreparation, exec.Status);
        Assert.Equal("admin", exec.ApprovedBy);
    }

    [Fact]
    public async Task AdminSolution_leve_une_double_approbation_en_une_fois()
    {
        var f = Fabrique();
        var id = await SeedExecutionAsync(f, requestedBy: "admin", doubleApprobation: true);
        var usecase = new ApproveExecutionUseCase(f, new MeriseBusinessPolicyService(f));

        var err = await usecase.ExecuteAsync(id, "admin", approverIsSolutionAdmin: true);

        Assert.Null(err);
        await using var db = f.CreateDbContext();
        var exec = await db.Executions.FirstAsync(e => e.Id == id);
        Assert.Equal(ExecutionStatus.EnPreparation, exec.Status);
        Assert.Equal("admin", exec.ApprovedBy);
        Assert.Equal("admin", exec.SecondApprovedBy);
    }

    [Fact]
    public async Task NonAdmin_ne_peut_toujours_pas_approuver_sa_propre_operation()
    {
        var f = Fabrique();
        var id = await SeedExecutionAsync(f, requestedBy: "op1", doubleApprobation: false);
        var usecase = new ApproveExecutionUseCase(f, new MeriseBusinessPolicyService(f));

        var err = await usecase.ExecuteAsync(id, "op1", approverIsSolutionAdmin: false);

        Assert.NotNull(err);
        Assert.Contains("ne peut pas approuver sa propre", err);
        await using var db = f.CreateDbContext();
        var exec = await db.Executions.FirstAsync(e => e.Id == id);
        Assert.Equal(ExecutionStatus.EnAttenteApprobation, exec.Status);
    }

    // -----------------------------------------------------------------------
    private static async Task<Guid> SeedExecutionAsync(
        IDbContextFactory<N4SentinelDbContext> f, string requestedBy, bool doubleApprobation)
    {
        await using var db = f.CreateDbContext();
        var env = new N4Environment { Id = Guid.NewGuid(), Code = "PRD", Name = "Prod", Kind = EnvironmentKind.Production };
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            WorkflowName = "Redémarrage complet N4",
            EnvironmentId = env.Id,
            EnvironmentCode = "PRD",
            Status = ExecutionStatus.EnAttenteApprobation,
            RequestedBy = requestedBy,
            Reason = "Test",
            RequiresDoubleApproval = doubleApprobation
        };
        db.Environments.Add(env);
        db.Executions.Add(exec);
        await db.SaveChangesAsync();
        return exec.Id;
    }

    private static IDbContextFactory<N4SentinelDbContext> Fabrique() =>
        new FabriqueDeTest(new DbContextOptionsBuilder<N4SentinelDbContext>()
            .UseInMemoryDatabase($"appr-{Guid.NewGuid():N}").Options);

    private sealed class FabriqueDeTest(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
