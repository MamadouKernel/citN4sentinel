using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Cycle de vie d'une exécution : préparation, approbation, lancement, et les
/// commandes que l'opérateur peut adresser au moteur pendant qu'elle tourne
/// (FR-011 à FR-013, FR-022, FR-026, FR-027).
///
/// Ce service ne pilote AUCUN serveur. Il enregistre des intentions ; le moteur
/// les lit et agit. Cette séparation est ce qui permet à une exécution de
/// survivre au redémarrage du serveur applicatif : tout ce qui compte est en
/// base, rien d'essentiel n'est en mémoire.
/// </summary>
public sealed class ExecutionService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    EnvironmentLockService locks,
    ILogger<ExecutionService> logger,
    OrchestrationEngine? engine = null)
{
    /// <summary>
    /// Réveille le moteur immédiatement après un lancement ou une reprise.
    ///
    /// Sans cela, il faudrait attendre le prochain passage du service
    /// d'arrière-plan — jusqu'à vingt secondes pendant lesquelles l'opérateur
    /// regarde un écran qui ne bouge pas et se demande si son clic a été pris.
    /// Le passage périodique reste le filet de sécurité, il n'est plus le
    /// chemin normal.
    ///
    /// Le moteur est facultatif : les tests instancient ce service seul.
    /// </summary>
    private async Task ReveillerMoteurAsync(CancellationToken ct)
    {
        if (engine is null) return;

        try { await engine.PickUpAsync(ct); }
        catch (Exception ex)
        {
            // Le passage periodique reprendra l'execution : l'echec du reveil
            // retarde l'affichage, il ne perd rien.
            logger.LogWarning(ex, "Réveil immédiat du moteur en échec ; la reprise se fera au prochain passage.");
        }
    }

    // -----------------------------------------------------------------------
    // Préparation
    // -----------------------------------------------------------------------
    /// <summary>
    /// Instancie une exécution à partir d'un workflow. Les étapes sont RECOPIÉES,
    /// pas référencées : le rapport doit rester exact même si le workflow évolue
    /// ensuite.
    /// </summary>
    public async Task<PrepareResult> PrepareAsync(
        Guid workflowId, string requestedBy, string reason, string? ticketReference,
        bool isSimulation, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var workflow = await db.Workflows
            .Include(w => w.Steps)
            .Include(w => w.Environment)
            .FirstOrDefaultAsync(w => w.Id == workflowId, ct);

        if (workflow is null) return PrepareResult.Failed("Workflow introuvable.");

        if (!workflow.IsRunnable)
            return PrepareResult.Failed(
                $"Le workflow « {workflow.Name} » est en état {workflow.Status} et compte "
                + $"{workflow.Steps.Count} étape(s). Il doit être validé et comporter au moins une étape.");

        if (string.IsNullOrWhiteSpace(reason))
            return PrepareResult.Failed(
                "Le motif est obligatoire. Une opération sans motif est une opération que personne "
                + "ne saura expliquer trois mois plus tard.");

        var environnement = workflow.Environment;
        var mutative = workflow.Kind is not WorkflowKind.ControleSeul;

        // Une exécution mutative en Production exige une approbation, que le
        // workflow l'ait prévue ou non. Le niveau de l'environnement prime sur
        // le paramétrage du modèle.
        var approbationRequise = !isSimulation && mutative
            && (workflow.RequiresApproval || environnement?.Kind == EnvironmentKind.Production);

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
            ExpectedImpact = DecrireImpact(workflow)
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
                IsSkippable = modele.IsSkippable,
                RequiresConfirmation = modele.RequiresConfirmation,
                FailurePolicy = modele.FailurePolicy,
                Instruction = modele.Instruction,
                MaxRetries = modele.MaxRetries,
                AutomaticRetry = modele.AutomaticRetry,
                State = ExecutionStepState.AVenir
            });
        }

        // Le workflow devient immuable des l'instant ou une execution s'en sert.
        workflow.HasBeenExecuted = true;

        db.Executions.Add(execution);
        await db.SaveChangesAsync(ct);

        await RenseignerComposantsAsync(execution.Id, ct);

        logger.LogInformation(
            "Exécution {Correlation} préparée : {Workflow} v{Version} sur {Env}{Simulation}.",
            execution.CorrelationId, workflow.Name, workflow.Version, execution.EnvironmentCode,
            isSimulation ? " (simulation)" : string.Empty);

        return PrepareResult.Ok(execution.Id, approbationRequise);
    }

    /// <summary>Recopie le nom du composant et de son hôte, pour un rapport lisible sans jointure.</summary>
    private async Task RenseignerComposantsAsync(Guid executionId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

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

    // -----------------------------------------------------------------------
    // Approbation et lancement
    // -----------------------------------------------------------------------
    public async Task<string?> ApproveAsync(Guid executionId, string approvedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";

        if (execution.Status != ExecutionStatus.EnAttenteApprobation)
            return $"Cette exécution est en état {execution.Status} : elle n'attend pas d'approbation.";

        // Le demandeur ne peut pas s'approuver lui-meme. Le double regard n'a de
        // sens que s'il y a deux regards.
        if (string.Equals(execution.RequestedBy, approvedBy, StringComparison.OrdinalIgnoreCase))
            return "Le demandeur ne peut pas approuver sa propre opération. "
                 + "L'approbation doit venir d'une autre personne.";

        execution.ApprovedBy = approvedBy;
        execution.ApprovedAt = DateTimeOffset.UtcNow;
        execution.Status = ExecutionStatus.EnPreparation;

        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Lance l'exécution : prend le verrou d'environnement, puis passe en
    /// EnCours. Le moteur la prendra en charge.
    /// </summary>
    public async Task<string?> StartAsync(Guid executionId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";

        if (execution.Status == ExecutionStatus.EnAttenteApprobation)
            return "Cette opération attend une approbation avant d'être lancée.";

        if (execution.Status != ExecutionStatus.EnPreparation)
            return $"Cette exécution est en état {execution.Status} : elle ne peut pas être lancée.";

        // Une simulation n'emet aucune commande : elle ne prend donc pas le
        // verrou, et n'empeche personne de travailler.
        if (!execution.IsSimulation)
        {
            var verrou = await locks.AcquireAsync(
                execution.EnvironmentId, execution.Id, actor,
                $"{execution.WorkflowName} v{execution.WorkflowVersion}", ct);

            if (!verrou.Succeeded) return verrou.Error;
        }

        execution.Status = ExecutionStatus.EnCours;
        execution.StartedAt = DateTimeOffset.UtcNow;
        execution.LastHeartbeatAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Exécution {Correlation} lancée par {Acteur}.", execution.CorrelationId, actor);

        await ReveillerMoteurAsync(ct);
        return null;
    }

    // -----------------------------------------------------------------------
    // Commandes en cours d'exécution
    // -----------------------------------------------------------------------
    /// <summary>
    /// Demande de pause. Elle prend effet à la FIN de l'étape en cours : couper
    /// au milieu d'un démarrage laisserait le composant dans un état que
    /// personne ne saurait décrire.
    /// </summary>
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

        var execution = await db.Executions.FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";

        if (execution.Status is not (ExecutionStatus.EnPause or ExecutionStatus.ReconciliationRequise))
            return $"Cette exécution est en état {execution.Status} : il n'y a rien à reprendre.";

        // Reprise apres reconciliation : les etapes laissees en suspens par un
        // arret brutal repartent a zero, apres que l'operateur a constate l'etat
        // reel. On ne repart JAMAIS sur une etape dont on ignore l'issue.
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
                etape.Evidence = $"Reprise décidée par {actor} après constat de l'état réel.";
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

    /// <summary>
    /// Annulation. Elle ne coupe rien en vol : l'étape en cours va à son terme,
    /// et la séquence s'arrête après. Une annulation qui interrompt un arrêt
    /// N4 à mi-parcours produit un écosystème à moitié éteint.
    /// </summary>
    public async Task<string?> RequestCancelAsync(Guid executionId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);

        if (execution is null) return "Exécution introuvable.";

        if (execution.IsFinished)
            return "Cette exécution est déjà terminée.";

        execution.CancelRequestedBy = actor;

        // Deja en pause ou en attente : rien ne tourne, l'annulation est
        // immediate et sans risque.
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

        execution.Status = ExecutionStatus.AnnulationDemandee;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Contournement d'une étape (FR-022, FR-027). Une étape non déclarée
    /// contournable ne peut JAMAIS être ignorée, quel que soit le profil du
    /// demandeur : c'est la seule façon de garantir qu'un contrôle bloquant
    /// reste bloquant.
    /// </summary>
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

        etape.State = ExecutionStepState.Ignore;
        etape.SkippedBy = actor;
        etape.SkipReason = reason;
        etape.EndedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Étape « {Etape} » contournée par {Acteur} : {Motif}", etape.Name, actor, reason);

        return null;
    }

    /// <summary>
    /// Nouvelle tentative sur une étape bloquée, décidée par un opérateur.
    ///
    /// Le moteur ne rejoue JAMAIS de lui-même une action mutative qui a échoué :
    /// réessayer sans avoir compris la cause est le meilleur moyen d'aggraver un
    /// incident. Quelqu'un doit avoir regardé.
    /// </summary>
    public async Task<string?> RetryStepAsync(Guid stepId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var etape = await db.ExecutionSteps.FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (etape is null) return "Étape introuvable.";

        if (etape.State is not (ExecutionStepState.Bloque or ExecutionStepState.Echec))
            return $"L'étape « {etape.Name} » est en état {etape.State} : il n'y a rien à réessayer.";

        etape.State = ExecutionStepState.AVenir;
        etape.StartedAt = null;
        etape.EndedAt = null;
        etape.Error = null;
        etape.ProgressMessage = $"Nouvelle tentative demandée par {actor}.";

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Nouvelle tentative sur l'étape « {Etape} », décidée par {Acteur}.", etape.Name, actor);
        return null;
    }

    /// <summary>
    /// Autorisation d'une étape automatique déclarée à confirmation préalable.
    /// L'opérateur ne dit pas ici que le travail est fait — il dit que le moteur
    /// peut y aller. C'est un feu vert, pas un compte rendu.
    /// </summary>
    public async Task<string?> AuthorizeStepAsync(Guid stepId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var etape = await db.ExecutionSteps.FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (etape is null) return "Étape introuvable.";

        if (etape.Action == StepAction.InterventionManuelle)
            return "Cette étape attend un compte rendu d'intervention, pas une autorisation.";

        if (etape.State != ExecutionStepState.EnAttente)
            return $"L'étape « {etape.Name} » est en état {etape.State} : elle n'attend pas d'autorisation.";

        etape.ConfirmedBy = actor;
        etape.ConfirmedAt = DateTimeOffset.UtcNow;
        etape.State = ExecutionStepState.AVenir;
        etape.ProgressMessage = $"Autorisée par {actor}.";

        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Compte rendu d'une intervention manuelle (FR-026).</summary>
    public async Task<string?> ConfirmStepAsync(
        Guid stepId, string actor, string? note, bool succeeded, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var etape = await db.ExecutionSteps.FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (etape is null) return "Étape introuvable.";

        if (etape.Action != StepAction.InterventionManuelle)
            return "Cette étape est exécutée par le moteur : elle attend une autorisation, pas un compte rendu.";

        if (etape.State != ExecutionStepState.EnAttente)
            return $"L'étape « {etape.Name} » est en état {etape.State} : elle n'attend pas de confirmation.";

        etape.ConfirmedBy = actor;
        etape.ConfirmedAt = DateTimeOffset.UtcNow;
        etape.OperatorNote = note;
        etape.State = succeeded ? ExecutionStepState.Reussi : ExecutionStepState.Echec;
        etape.EndedAt = DateTimeOffset.UtcNow;

        etape.Evidence = succeeded
            ? $"Intervention manuelle confirmée par {actor}." + (note is null ? "" : $" Note : {note}")
            : null;

        etape.Error = succeeded ? null
            : $"Intervention manuelle déclarée non réalisée par {actor}." + (note is null ? "" : $" Note : {note}");

        await db.SaveChangesAsync(ct);
        return null;
    }

    // -----------------------------------------------------------------------
    // Lecture
    // -----------------------------------------------------------------------
    public async Task<WorkflowExecution?> GetAsync(Guid executionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Executions
            .AsNoTracking()
            .Include(x => x.Steps.OrderBy(s => s.Order))
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);
    }

    public async Task<List<WorkflowExecution>> GetRecentAsync(
        Guid? environmentId, int count = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var requete = db.Executions.AsNoTracking().AsQueryable();
        if (environmentId is not null) requete = requete.Where(x => x.EnvironmentId == environmentId);

        return await requete
            .OrderByDescending(x => x.CreatedAt)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<List<WorkflowExecution>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Executions
            .AsNoTracking()
            .Include(x => x.Steps.OrderBy(s => s.Order))
            .Where(x => x.Status == ExecutionStatus.EnCours
                        || x.Status == ExecutionStatus.AnnulationDemandee
                        || x.Status == ExecutionStatus.EnPause
                        || x.Status == ExecutionStatus.ReconciliationRequise)
            .ToListAsync(ct);
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
}

public sealed record PrepareResult
{
    public bool Succeeded { get; init; }
    public Guid ExecutionId { get; init; }
    public bool RequiresApproval { get; init; }
    public string? Error { get; init; }

    public static PrepareResult Ok(Guid id, bool approbation) =>
        new() { Succeeded = true, ExecutionId = id, RequiresApproval = approbation };

    public static PrepareResult Failed(string error) => new() { Succeeded = false, Error = error };
}
