using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration.UseCases;

public sealed class PrepareExecutionUseCase(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<PrepareExecutionUseCase> logger,
    ApprovalMatrixService? approvalMatrix = null)
{
    public async Task<PrepareResult> ExecuteAsync(
        Guid workflowId, string requestedBy, string reason,
        string? ticketReference = null, bool isSimulation = false,
        DateTimeOffset? startWindow = null, DateTimeOffset? endWindow = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var workflow = await db.Workflows
            .Include(w => w.Steps)
            .Include(w => w.Environment)
            .FirstOrDefaultAsync(w => w.Id == workflowId, ct);

        if (workflow is null) return PrepareResult.Failed("Workflow introuvable.");

        if (workflow.Steps.Count == 0)
            return PrepareResult.Failed(
                $"Le workflow « {workflow.Name} » ne comporte aucune étape : "
                + "il n'y a rien à dérouler, même en simulation.");

        if (!isSimulation && !workflow.IsRunnable)
            return PrepareResult.Failed(
                $"Le workflow « {workflow.Name} » est en état {workflow.Status}. Il doit être "
                + "validé pour être lancé en réel. Lancez-le d'abord en simulation.");

        if (string.IsNullOrWhiteSpace(reason))
            return PrepareResult.Failed("Le motif est obligatoire.");

        var environnement = workflow.Environment;
        var mutative = workflow.Kind is not WorkflowKind.ControleSeul;

        ApprovalMatrixRule? regleMatrice = null;
        if (approvalMatrix is not null && environnement is not null)
        {
            var idsComposants = workflow.Steps
                .Where(s => s.ComponentId is not null)
                .Select(s => s.ComponentId!.Value)
                .Distinct()
                .ToList();

            var criticites = idsComposants.Count == 0
                ? []
                : await db.Components.AsNoTracking()
                    .Where(c => idsComposants.Contains(c.Id))
                    .Select(c => c.Criticality)
                    .ToListAsync(ct);

            var criticiteMax = criticites.Count == 0 ? CriticalityLevel.Faible : criticites.Max();

            regleMatrice = await approvalMatrix.ResolveAsync(environnement.Kind, workflow.Kind, criticiteMax, ct);
        }

        var approbationRequise = !isSimulation && mutative
            && (workflow.RequiresApproval || environnement?.Kind == EnvironmentKind.Production
                || regleMatrice?.RequiresApproval == true);

        var centerIds = await db.Components.AsNoTracking()
            .Where(c => c.EnvironmentId == workflow.EnvironmentId && c.Role == ComponentRole.CenterNode)
            .Select(c => c.Id)
            .ToListAsync(ct);
        var continuiteRequise = !isSimulation && centerIds.Count > 0 && workflow.Steps.Any(s =>
            s.Action is StepAction.Arreter or StepAction.Redemarrer
            && s.ComponentId is { } id && centerIds.Contains(id));

        var execution = new WorkflowExecution
        {
            WorkflowId = workflow.Id,
            WorkflowVersion = workflow.Version,
            WorkflowName = workflow.Name,
            Kind = workflow.Kind,
            EnvironmentId = workflow.EnvironmentId,
            EnvironmentCode = environnement?.Code ?? "INCONNU",
            Status = approbationRequise ? ExecutionStatus.EnAttenteApprobation : ExecutionStatus.EnPreparation,
            IsSimulation = isSimulation,
            RequestedBy = requestedBy,
            Reason = reason,
            TicketReference = ticketReference,
            ExpectedImpact = DecrireImpact(workflow),
            RequiresDoubleApproval = approbationRequise
                && (workflow.RequiresDoubleApproval || regleMatrice?.RequiresDoubleApproval == true),
            ContinuityChoiceRequired = continuiteRequise,
            StartWindow = startWindow,
            EndWindow = endWindow,
            EstimatedTotalDuration = TimeSpan.FromSeconds(workflow.Steps.Sum(s => (double)s.ExpectedSeconds))
        };

        foreach (var modele in workflow.Steps.OrderBy(s => s.Order))
        {
            execution.Steps.Add(new ExecutionStep
            {
                Order = modele.Order,
                Name = modele.Name,
                Action = modele.Action,
                ComponentId = modele.ComponentId,
                TimeoutSeconds = modele.TimeoutSeconds,
                ExpectedSeconds = modele.ExpectedSeconds,
                WarningThresholdSeconds = modele.WarningThresholdSeconds,
                IsSkippable = modele.IsSkippable,
                RequiresConfirmation = modele.RequiresConfirmation,
                RequiresEvidenceFile = modele.RequiresEvidenceFile,
                FailurePolicy = modele.FailurePolicy,
                Instruction = modele.Instruction,
                MaxRetries = modele.MaxRetries,
                AutomaticRetry = modele.AutomaticRetry,
                RetryDelaySeconds = modele.RetryDelaySeconds,
                CanRunInParallel = modele.CanRunInParallel,
                State = ExecutionStepState.AVenir
            });
        }

        workflow.HasBeenExecuted = true;
        db.Executions.Add(execution);
        await db.SaveChangesAsync(ct);

        await RenseignerComposantsAsync(execution.Id, ctFactory: dbFactory, ct);

        logger.LogInformation(
            "Exécution {Correlation} préparée : {Workflow} v{Version} sur {Env}{Simulation}.",
            execution.CorrelationId, workflow.Name, workflow.Version, execution.EnvironmentCode,
            isSimulation ? " (simulation)" : string.Empty);

        return PrepareResult.Ok(execution.Id, approbationRequise);
    }

    private static string DecrireImpact(Workflow workflow) => workflow.Kind switch
    {
        WorkflowKind.ArretComplet =>
            "INTERRUPTION TOTALE de service : l'écosystème N4 sera indisponible pendant toute l'opération.",
        WorkflowKind.DemarrageComplet =>
            "Remise en service progressive. Le terminal reste indisponible jusqu'à confirmation du dernier composant.",
        WorkflowKind.RedemarrageComplet =>
            "INTERRUPTION TOTALE de service pendant le redémarrage complet de l'écosystème.",
        WorkflowKind.RollingRestart =>
            "Service maintenu, capacité réduite pendant le redémarrage successif des nœuds.",
        WorkflowKind.OperationPartielle =>
            "Interruption partielle, limitée aux composants visés et à ceux qui en dépendent.",
        WorkflowKind.OperationUnitaire =>
            "Impact limité au composant visé et à ceux qui en dépendent.",
        WorkflowKind.ControleSeul =>
            "AUCUN IMPACT : opération en lecture seule.",
        WorkflowKind.RepriseApresEchec =>
            "Impact dépendant de l'état laissé par l'opération précédente. À évaluer avant lancement.",
        _ => "Impact à évaluer."
    };

    private static async Task RenseignerComposantsAsync(Guid executionId, IDbContextFactory<N4SentinelDbContext> ctFactory, CancellationToken ct)
    {
        await using var db = await ctFactory.CreateDbContextAsync(ct);

        var etapes = await db.ExecutionSteps
            .Where(s => s.ExecutionId == executionId && s.ComponentId != null)
            .ToListAsync(ct);

        if (etapes.Count == 0) return;

        var ids = etapes.Select(s => s.ComponentId!.Value).Distinct().ToList();
        var composants = await db.Components
            .AsNoTracking()
            .Include(c => c.Server)
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        foreach (var etape in etapes)
        {
            if (!composants.TryGetValue(etape.ComponentId!.Value, out var composant)) continue;
            etape.ComponentName = composant.LogicalName;
            etape.HostName = composant.Server?.HostName;
        }

        await db.SaveChangesAsync(ct);
    }
}
