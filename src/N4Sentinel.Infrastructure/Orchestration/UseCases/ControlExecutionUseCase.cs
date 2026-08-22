using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Infrastructure.Orchestration.UseCases;

public sealed class ControlExecutionUseCase(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    EnvironmentLockService locks,
    IAuditWriter auditWriter,
    ILogger<ControlExecutionUseCase> logger,
    OrchestrationEngine? engine = null,
    Notifications.OperationNotificationService? notifications = null,
    ApprovalMatrixService? approvalMatrix = null,
    Supervision.SupervisionService? supervision = null,
    CredentialStore? credentials = null)
{
    private async Task ReveillerMoteurAsync(CancellationToken ct)
    {
        if (engine is null) return;

        try { await engine.PickUpAsync(ct); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Réveil immédiat du moteur en échec ; la reprise se fera au prochain passage.");
        }
    }

    public async Task<string?> StartAsync(Guid executionId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";

        if (execution.Status == ExecutionStatus.EnAttenteApprobation)
        {
            await auditWriter.WriteAsync(
                AuditAction.TentativeNonAutorisee, AuditOutcome.Echec, actor,
                entityType: nameof(WorkflowExecution), entityId: execution.Id.ToString(),
                entityLabel: $"{execution.WorkflowName} v{execution.WorkflowVersion}",
                environmentId: execution.EnvironmentId,
                reason: "Lancement tenté avant approbation.",
                correlationId: execution.CorrelationId, ct: ct);

            return "Cette opération attend une approbation avant d'être lancée.";
        }

        if (execution.Status != ExecutionStatus.EnPreparation)
            return $"Cette exécution est en état {execution.Status} : elle ne peut pas être lancée.";

        var environnement = await db.Environments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == execution.EnvironmentId, ct);
        if (environnement is null || environnement.Status is not (LifecycleStatus.Valide or LifecycleStatus.Actif))
        {
            await auditWriter.WriteAsync(
                AuditAction.TentativeNonAutorisee, AuditOutcome.Echec, actor,
                entityType: nameof(WorkflowExecution), entityId: execution.Id.ToString(),
                entityLabel: $"{execution.WorkflowName} v{execution.WorkflowVersion}",
                environmentId: execution.EnvironmentId,
                reason: $"Environnement en statut {environnement?.Status.ToString() ?? "introuvable"} : lancement refusé.",
                correlationId: execution.CorrelationId, ct: ct);

            return $"L'environnement « {execution.EnvironmentCode} » n'est pas Validé ou Actif "
                 + $"(statut actuel : {environnement?.Status.ToString() ?? "introuvable"}). "
                 + "Seuls les environnements validés et actifs peuvent exécuter une opération.";
        }

        if (execution.PreflightAt is null)
            return "Les contrôles préalables n'ont pas été passés. "
                 + "Lancez le pré-check depuis l'écran de l'opération.";

        if (execution.PreflightBlocked)
        {
            await auditWriter.WriteAsync(
                AuditAction.TentativeNonAutorisee, AuditOutcome.Echec, actor,
                entityType: nameof(WorkflowExecution), entityId: execution.Id.ToString(),
                entityLabel: $"{execution.WorkflowName} v{execution.WorkflowVersion}",
                environmentId: execution.EnvironmentId,
                reason: "Lancement tenté malgré un pré-check bloquant.",
                correlationId: execution.CorrelationId, ct: ct);

            return "Les contrôles préalables ont relevé au moins un échec bloquant. "
                 + "L'opération ne peut pas être lancée tant qu'il n'est pas corrigé — "
                 + "un contrôle bloquant ne se contourne pas.";
        }

        if (!execution.IsSimulation)
        {
            // PORTILLON D'IDENTITE. Une simulation n'emet aucune commande et ne
            // demande donc aucun compte : c'est en execution reelle, et la
            // seulement, que l'absence de compte empeche vraiment de travailler.
            var barrage = await VerifierCompteExploitationAsync(execution, actor, ct);
            if (barrage is not null) return barrage;

            var verrou = await locks.AcquireAsync(
                execution.EnvironmentId, execution.Id, actor,
                $"{execution.WorkflowName} v{execution.WorkflowVersion}", ct);

            if (!verrou.Succeeded) return verrou.Error;

            // L'identite est FIGEE ICI, au lancement : celui qui lance engage
            // sa responsabilite en emettant reellement les commandes, et une
            // reprise ulterieure par quelqu'un d'autre ne doit pas changer
            // l'identite sous laquelle les serveurs N4 ont vu agir.
            execution.OperatingIdentityLogin = actor;

            // L'etiquette est recopiee, pas recalculee a l'affichage : le
            // compte peut changer ou son proprietaire quitter l'entreprise, et
            // un rapport doit continuer de dire sous quelle identite les
            // commandes SONT PARTIES.
            var compteEmploye = credentials is null ? null : await credentials.GetForLoginAsync(actor, ct);
            execution.OperatingIdentityLabel = ActingIdentity.Format(
                compteEmploye?.UserName, compteEmploye?.OwnerDisplayName);
        }

        execution.Status = ExecutionStatus.EnCours;
        execution.StartedAt = DateTimeOffset.UtcNow;
        execution.LastHeartbeatAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Exécution {Correlation} lancée par {Acteur}.", execution.CorrelationId, actor);

        await auditWriter.WriteAsync(
            AuditAction.ExecutionOperation, AuditOutcome.Succes, actor,
            entityType: nameof(WorkflowExecution), entityId: execution.Id.ToString(),
            entityLabel: $"{execution.WorkflowName} v{execution.WorkflowVersion}",
            environmentId: execution.EnvironmentId, reason: execution.Reason,
            correlationId: execution.CorrelationId, ct: ct);

        if (notifications is not null) await notifications.NotifierLancementAsync(execution, ct);

        await ReveillerMoteurAsync(ct);
        return null;
    }

    /// <summary>
    /// Verifie que celui qui lance dispose d'un compte d'exploitation
    /// utilisable.
    ///
    /// C'est le point de blocage choisi : l'operateur peut consulter et
    /// diagnostiquer sans rien saisir, mais pas emettre de commande sous une
    /// identite qui ne serait pas la sienne. Un compte ecarte apres expiration
    /// donne un message qui dit quoi faire, plutot qu'un echec WinRM a la
    /// troisieme etape.
    /// </summary>
    private async Task<string?> VerifierCompteExploitationAsync(
        WorkflowExecution execution, string actor, CancellationToken ct)
    {
        if (credentials is null) return null;

        var compte = await credentials.GetForLoginAsync(actor, ct);

        var refus = compte switch
        {
            null =>
                "Votre compte d'exploitation n'est pas enregistré. L'application agit sur les "
                + "serveurs N4 sous le compte de la personne qui lance l'opération : sans le vôtre, "
                + "aucune commande ne peut vous être attribuée. Renseignez-le depuis "
                + "« Mon compte d'exploitation », puis relancez.",

            { RequiresReentry: true } =>
                $"Votre compte d'exploitation {compte.UserName} a été écarté : {compte.InvalidationReason} "
                + "Ressaisissez-le depuis « Mon compte d'exploitation » avant de relancer.",

            { IsUsable: false } =>
                $"Votre compte d'exploitation est incomplet ({compte.SecretState}). "
                + "Complétez-le depuis « Mon compte d'exploitation » avant de relancer.",

            _ => null
        };

        if (refus is null) return null;

        await auditWriter.WriteAsync(
            AuditAction.TentativeNonAutorisee, AuditOutcome.Echec, actor,
            entityType: nameof(WorkflowExecution), entityId: execution.Id.ToString(),
            entityLabel: $"{execution.WorkflowName} v{execution.WorkflowVersion}",
            environmentId: execution.EnvironmentId,
            reason: "Lancement refusé : aucun compte d'exploitation utilisable pour cet opérateur.",
            correlationId: execution.CorrelationId, ct: ct);

        return refus;
    }

    public async Task<string?> RequestPauseAsync(Guid executionId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";

        if (execution.Status != ExecutionStatus.EnCours)
            return $"Cette exécution est en état {execution.Status} : elle ne peut pas être mise en pause.";

        execution.PauseRequestedBy = actor;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> ResumeAsync(Guid executionId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";

        if (execution.Status is not (ExecutionStatus.EnPause or ExecutionStatus.ReconciliationRequise))
            return $"Cette exécution est en état {execution.Status} : il n'y a rien à reprendre.";

        if (execution.Status == ExecutionStatus.ReconciliationRequise)
        {
            var suspendues = await db.ExecutionSteps
                .Where(s => s.ExecutionId == executionId
                            && (s.State == ExecutionStepState.EnCours || s.State == ExecutionStepState.Verification))
                .ToListAsync(ct);

            foreach (var etape in suspendues)
            {
                etape.State = ExecutionStepState.AVenir;
                etape.StartedAt = null;
                etape.ProgressMessage = null;
                etape.Error = null;
                etape.ErrorType = null;
                etape.Evidence = $"Reprise décidée par {actor} après constat de l'état réel.";
            }
        }

        if (supervision is not null)
        {
            var remainingComponentIds = execution.Steps
                .Where(s => s.State is ExecutionStepState.AVenir or ExecutionStepState.EnAttente && s.ComponentId is not null)
                .Select(s => s.ComponentId!.Value)
                .Distinct()
                .ToList();

            foreach (var cid in remainingComponentIds)
            {
                ct.ThrowIfCancellationRequested();
                await supervision.EvaluateComponentAsync(cid, ct);
            }
        }

        if (!execution.IsSimulation)
        {
            var verrou = await locks.AcquireAsync(
                execution.EnvironmentId, execution.Id, actor,
                $"{execution.WorkflowName} v{execution.WorkflowVersion} (reprise)", ct);

            if (!verrou.Succeeded) return verrou.Error;
        }

        execution.Status = ExecutionStatus.EnCours;
        execution.PauseRequestedBy = null;
        execution.LastHeartbeatAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Exécution {Correlation} reprise par {Acteur}.", execution.CorrelationId, actor);

        await ReveillerMoteurAsync(ct);
        return null;
    }

    public async Task<string?> RequestCancelAsync(Guid executionId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);

        if (execution is null) return "Exécution introuvable.";

        if (execution.IsFinished)
            return "Cette exécution est déjà terminée.";

        execution.CancelRequestedBy = actor;

        if (execution.Status is ExecutionStatus.EnPause
                             or ExecutionStatus.EnPreparation
                             or ExecutionStatus.EnAttenteApprobation
                             or ExecutionStatus.ReconciliationRequise)
        {
            foreach (var etape in execution.Steps.Where(s => !s.IsTerminal))
                etape.State = ExecutionStepState.Annule;

            execution.Status = ExecutionStatus.Annule;
            execution.EndedAt = DateTimeOffset.UtcNow;
            execution.Outcome = $"Annulée par {actor} avant exécution des étapes restantes.";

            await db.SaveChangesAsync(ct);
            await locks.ReleaseAsync(executionId, ct);
            return null;
        }

        var lignes = await db.Executions
            .Where(x => x.Id == executionId
                     && x.Status != ExecutionStatus.TermineSucces
                     && x.Status != ExecutionStatus.TermineAvecAvertissements
                     && x.Status != ExecutionStatus.Echec
                     && x.Status != ExecutionStatus.Annule)
            .ExecuteUpdateAsync(mise => mise
                .SetProperty(x => x.Status, ExecutionStatus.AnnulationDemandee)
                .SetProperty(x => x.CancelRequestedBy, actor), ct);

        return lignes == 0 ? "Cette exécution est déjà terminée." : null;
    }

    public async Task<string?> SkipStepAsync(
        Guid stepId, string actor, string reason, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var etape = await db.ExecutionSteps.Include(s => s.Execution)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct);

        if (etape is null) return "Étape introuvable.";

        if (!etape.IsSkippable)
            return $"L'étape « {etape.Name} » n'est pas déclarée contournable. "
                 + "Elle ne peut être ignorée par personne, quel que soit le profil.";

        if (string.IsNullOrWhiteSpace(reason))
            return "Un contournement sans justification n'est pas un contournement, c'est un trou dans la traçabilité.";

        if (etape.IsTerminal)
            return $"L'étape « {etape.Name} » est déjà terminée ({etape.State}).";

        if (approvalMatrix is not null && etape.Execution is not null)
        {
            var environnement = await db.Environments.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == etape.Execution.EnvironmentId, ct);

            if (environnement?.Kind == EnvironmentKind.Production)
            {
                var criticiteEtape = etape.ComponentId is { } compId
                    ? await db.Components.AsNoTracking()
                        .Where(c => c.Id == compId).Select(c => c.Criticality)
                        .FirstOrDefaultAsync(ct)
                    : CriticalityLevel.Faible;

                var regle = await approvalMatrix.ResolveAsync(
                    environnement.Kind, etape.Execution.Kind, criticiteEtape, ct);

                if (regle?.RequiresDoubleApproval == true && etape.SkipCoApprovedBy is null)
                {
                    etape.SkippedBy = actor;
                    etape.SkipReason = reason;
                    etape.ProgressMessage =
                        $"Contournement demandé par {actor}, en attente d'un second approbateur "
                        + "(matrice de criticité, FR-013/FR-027).";

                    await db.SaveChangesAsync(ct);

                    logger.LogWarning(
                        "Contournement de l'étape « {Etape} » demandé par {Acteur}, en attente d'un second "
                        + "approbateur imposé par la matrice de criticité.", etape.Name, actor);

                    return null;
                }
            }
        }

        etape.State = ExecutionStepState.Ignore;
        etape.SkippedBy = actor;
        etape.SkipReason = reason;
        etape.EndedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Étape « {Etape} » contournée par {Acteur} : {Motif}", etape.Name, actor, reason);

        await auditWriter.WriteAsync(
            AuditAction.Contournement, AuditOutcome.Succes, actor,
            entityType: nameof(ExecutionStep), entityId: etape.Id.ToString(), entityLabel: etape.Name,
            environmentId: etape.Execution?.EnvironmentId, reason: reason,
            correlationId: etape.Execution?.CorrelationId, ct: ct);

        await ReveillerMoteurAsync(ct);
        return null;
    }
}
