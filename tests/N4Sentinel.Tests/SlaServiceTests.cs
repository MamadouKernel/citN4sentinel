using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests du service SLA — FR-094.
///
/// TROIS INDICATEURS AJOUTÉS CETTE SESSION, chacun avec sa propre règle
/// d'honnêteté : le taux de réussite se calcule PAR OPÉRATION, pas seulement
/// toutes confondues ; une étape n'est « lente » que si elle dépasse le seuil
/// DÉCLARÉ sur elle, jamais une estimation ; une cause n'est « récurrente »
/// qu'à partir de deux occurrences, jamais présentée sur une seule.
/// </summary>
public sealed class SlaServiceTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private TestDbContextFactory _factory = null!;
    private SlaService _sla = null!;

    private Guid _envId;

    public async Task InitializeAsync()
    {
        var cs = $"Server=localhost;Database={_databaseName};Trusted_Connection=True;"
               + "TrustServerCertificate=True;MultipleActiveResultSets=True";

        _factory = new TestDbContextFactory(TestDbContextOptions.Builder(cs).Options);

        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();

            var env = new N4Environment { Code = "UAT", Name = "Recette", Kind = EnvironmentKind.UAT };
            db.Environments.Add(env);
            await db.SaveChangesAsync();
            _envId = env.Id;
        }

        _sla = new SlaService(_factory);
    }

    public async Task DisposeAsync()
    {
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var master = new Microsoft.Data.SqlClient.SqlConnection(MasterConnection);
        await master.OpenAsync();
        var cmd = master.CreateCommand();
        cmd.CommandText =
            $"IF DB_ID('{_databaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{_databaseName}]; END";
        await cmd.ExecuteNonQueryAsync();
    }

    // =======================================================================
    // FR-094 — Taux de réussite par opération
    // =======================================================================
    [Fact(DisplayName = "Le taux de réussite est calculé par opération, pas seulement toutes confondues")]
    public async Task GenerateReportAsync_Calcule_Le_Taux_Par_Operation()
    {
        await AjouterExecutionAsync("Démarrage Bridge", ExecutionStatus.TermineSucces);
        await AjouterExecutionAsync("Démarrage Bridge", ExecutionStatus.TermineSucces);
        await AjouterExecutionAsync("Arrêt Cluster", ExecutionStatus.Echec);
        await AjouterExecutionAsync("Arrêt Cluster", ExecutionStatus.TermineSucces);

        var rapport = await _sla.GenerateReportAsync(_envId, TimeSpan.FromDays(7));

        var bridge = rapport.SuccessRateByOperation.Single(o => o.WorkflowName == "Démarrage Bridge");
        Assert.Equal(2, bridge.TotalExecutions);
        Assert.Equal(100.0, bridge.SuccessRatePercentage);

        var cluster = rapport.SuccessRateByOperation.Single(o => o.WorkflowName == "Arrêt Cluster");
        Assert.Equal(2, cluster.TotalExecutions);
        Assert.Equal(50.0, cluster.SuccessRatePercentage);
    }

    // =======================================================================
    // FR-094 — Étapes lentes
    // =======================================================================
    [Fact(DisplayName = "Une étape au-delà de son seuil d'avertissement déclaré est signalée comme lente")]
    public async Task GenerateReportAsync_Signale_Une_Etape_Au_Dela_Du_Seuil()
    {
        await AjouterExecutionAvecEtapeAsync(
            "Démarrage Bridge", ExecutionStatus.TermineSucces,
            dureeSecondes: 120, seuilAvertissement: 60, seuilAttendu: 30);

        var rapport = await _sla.GenerateReportAsync(_envId, TimeSpan.FromDays(7));

        var etape = Assert.Single(rapport.SlowSteps);
        Assert.Equal(30, etape.ExpectedSeconds);
        Assert.True(etape.ActualSeconds >= 119);
    }

    [Fact(DisplayName = "Une étape sous son seuil d'avertissement déclaré n'est jamais signalée comme lente")]
    public async Task GenerateReportAsync_Ne_Signale_Pas_Une_Etape_Sous_Le_Seuil()
    {
        await AjouterExecutionAvecEtapeAsync(
            "Démarrage Bridge", ExecutionStatus.TermineSucces,
            dureeSecondes: 20, seuilAvertissement: 60, seuilAttendu: 30);

        var rapport = await _sla.GenerateReportAsync(_envId, TimeSpan.FromDays(7));

        Assert.Empty(rapport.SlowSteps);
    }

    // =======================================================================
    // FR-094 — Causes récurrentes
    // =======================================================================
    [Fact(DisplayName = "Une cause d'échec classée revient regroupée, comptée, jamais confondue avec une autre")]
    public async Task GenerateReportAsync_Regroupe_Les_Causes_Recurrentes_Par_Type_Classe()
    {
        await AjouterExecutionAvecEchecAsync("Op 1", StepErrorType.TimeoutAttente);
        await AjouterExecutionAvecEchecAsync("Op 2", StepErrorType.TimeoutAttente);
        await AjouterExecutionAvecEchecAsync("Op 3", StepErrorType.CommandeRefusee);

        var rapport = await _sla.GenerateReportAsync(_envId, TimeSpan.FromDays(7));

        var recurrente = Assert.Single(rapport.RecurringCauses);
        Assert.Equal(StepErrorType.TimeoutAttente, recurrente.ErrorType);
        Assert.Equal(2, recurrente.OccurrenceCount);
    }

    [Fact(DisplayName = "Une seule occurrence d'une cause n'est jamais présentée comme une récurrence")]
    public async Task GenerateReportAsync_N_Affiche_Pas_Une_Cause_Isolee()
    {
        await AjouterExecutionAvecEchecAsync("Op 1", StepErrorType.TimeoutAttente);

        var rapport = await _sla.GenerateReportAsync(_envId, TimeSpan.FromDays(7));

        Assert.Empty(rapport.RecurringCauses);
    }

    // =======================================================================
    // Aides
    // =======================================================================
    private async Task<Guid> CreerWorkflowAsync(N4SentinelDbContext db, string nom)
    {
        var workflow = new Workflow
        {
            EnvironmentId = _envId,
            Code = $"WF{Guid.NewGuid():N}"[..8],
            Name = nom,
            Kind = WorkflowKind.OperationUnitaire,
            Status = LifecycleStatus.Valide
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        return workflow.Id;
    }

    private async Task AjouterExecutionAsync(string nomWorkflow, ExecutionStatus statut)
    {
        await using var db = _factory.CreateDbContext();
        var workflowId = await CreerWorkflowAsync(db, nomWorkflow);
        db.Executions.Add(new WorkflowExecution
        {
            EnvironmentId = _envId,
            EnvironmentCode = "UAT",
            WorkflowId = workflowId,
            WorkflowVersion = 1,
            WorkflowName = nomWorkflow,
            Status = statut,
            RequestedBy = "op1",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            EndedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task AjouterExecutionAvecEtapeAsync(
        string nomWorkflow, ExecutionStatus statut, int dureeSecondes, int seuilAvertissement, int seuilAttendu)
    {
        await using var db = _factory.CreateDbContext();
        var workflowId = await CreerWorkflowAsync(db, nomWorkflow);
        var debut = DateTimeOffset.UtcNow.AddMinutes(-10);
        var execution = new WorkflowExecution
        {
            EnvironmentId = _envId,
            EnvironmentCode = "UAT",
            WorkflowId = workflowId,
            WorkflowVersion = 1,
            WorkflowName = nomWorkflow,
            Status = statut,
            RequestedBy = "op1",
            StartedAt = debut,
            EndedAt = DateTimeOffset.UtcNow
        };
        execution.Steps.Add(new ExecutionStep
        {
            Order = 1,
            Name = "Démarrer le Bridge",
            Action = StepAction.Demarrer,
            State = ExecutionStepState.Reussi,
            ExpectedSeconds = seuilAttendu,
            WarningThresholdSeconds = seuilAvertissement,
            StartedAt = debut,
            EndedAt = debut.AddSeconds(dureeSecondes)
        });
        db.Executions.Add(execution);
        await db.SaveChangesAsync();
    }

    private async Task AjouterExecutionAvecEchecAsync(string nomWorkflow, StepErrorType type)
    {
        await using var db = _factory.CreateDbContext();
        var workflowId = await CreerWorkflowAsync(db, nomWorkflow);
        var execution = new WorkflowExecution
        {
            EnvironmentId = _envId,
            EnvironmentCode = "UAT",
            WorkflowId = workflowId,
            WorkflowVersion = 1,
            WorkflowName = nomWorkflow,
            Status = ExecutionStatus.Echec,
            RequestedBy = "op1",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            EndedAt = DateTimeOffset.UtcNow
        };
        execution.Steps.Add(new ExecutionStep
        {
            Order = 1,
            Name = "Démarrer le Bridge",
            Action = StepAction.Demarrer,
            State = ExecutionStepState.Echec,
            Error = "Une erreur technique.",
            ErrorType = type
        });
        db.Executions.Add(execution);
        await db.SaveChangesAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
