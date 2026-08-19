using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

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
    IAuditWriter auditWriter,
    ILogger<ExecutionService> logger,
    OrchestrationEngine? engine = null,
    Notifications.OperationNotificationService? notifications = null,
    StepExecutor? stepExecutor = null,
    Security.DocumentAntivirusScanner? antivirus = null,
    Diagnostic.DiagnosticSessionService? diagnostics = null,
    ApprovalMatrixService? approvalMatrix = null,
    Supervision.SupervisionService? supervision = null,
    AdHocOperationService? adHoc = null)
{
    /// <summary>Plafond d'une preuve jointe (FR-026) — même ordre de grandeur qu'un versement documentaire.</summary>
    public const long EvidenceFileMaxBytes = 20L * 1024 * 1024;
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

        // FR-005 : une SIMULATION peut se lancer sur un workflow non encore
        // validé — c'est même la seule façon de satisfaire l'obligation de
        // simuler AVANT de valider. Rien ne le justifierait d'ailleurs : une
        // simulation n'émet aucune commande et ne touche à rien. Le cycle de
        // vie devient : brouillon → simulé → validé → lançable en réel.
        //
        // Une étape reste exigée dans les deux cas : dérouler une séquence
        // vide ne prouverait rien.
        if (workflow.Steps.Count == 0)
            return PrepareResult.Failed(
                $"Le workflow « {workflow.Name} » ne comporte aucune étape : "
                + "il n'y a rien à dérouler, même en simulation.");

        if (!isSimulation && !workflow.IsRunnable)
            return PrepareResult.Failed(
                $"Le workflow « {workflow.Name} » est en état {workflow.Status}. Il doit être "
                + "validé pour être lancé en réel. Lancez-le d'abord en simulation : elle "
                + "n'émet aucune commande, et sa réussite est la condition pour le valider.");

        if (string.IsNullOrWhiteSpace(reason))
            return PrepareResult.Failed(
                "Le motif est obligatoire. Une opération sans motif est une opération que personne "
                + "ne saura expliquer trois mois plus tard.");

        var environnement = workflow.Environment;
        var mutative = workflow.Kind is not WorkflowKind.ControleSeul;

        // FR-013 : la matrice de criticité peut EXIGER davantage que le
        // workflow ne le prévoit — jamais en exiger moins. Le seuil est le
        // niveau de criticité le plus élevé parmi les composants réellement
        // touchés par une étape de l'exécution.
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

        // Une exécution mutative en Production exige une approbation, que le
        // workflow l'ait prévue ou non. Le niveau de l'environnement et la
        // matrice de criticité priment sur le paramétrage du modèle — jamais
        // l'inverse.
        var approbationRequise = !isSimulation && mutative
            && (workflow.RequiresApproval || environnement?.Kind == EnvironmentKind.Production
                || regleMatrice?.RequiresApproval == true);

        // FR-046/047 : une simulation n'emet aucune commande (FR-005), donc ne
        // change rien au role actif reel — le choix de continuite ne la
        // concerne pas.
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
            // Recopie, comme le reste : l'exigence reste exacte meme si le
            // workflow change avant que l'approbation ait lieu.
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
    /// <summary>
    /// Approuve une exécution en attente. Si le workflow exige une double
    /// approbation (FR-013), le premier appel enregistre le premier
    /// approbateur et laisse l'exécution EN ATTENTE — elle ne passe en
    /// préparation qu'après un second appel, par une personne distincte du
    /// demandeur ET du premier approbateur. Un double regard qui accepterait
    /// la même personne deux fois ne serait qu'un simple regard déguisé.
    /// </summary>
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

        if (execution.ApprovedBy is null)
        {
            execution.ApprovedBy = approvedBy;
            execution.ApprovedAt = DateTimeOffset.UtcNow;

            if (!execution.RequiresDoubleApproval)
                execution.Status = ExecutionStatus.EnPreparation;

            await db.SaveChangesAsync(ct);
            return null;
        }

        // Une premiere approbation existe deja : soit ce second appel est le
        // second regard exige, soit il n'y a rien a faire de plus.
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

    /// <summary>
    /// Enregistre le choix de continuité Center (FR-046/047) : rester actif,
    /// ou basculer vers le Standby. Rejouable tant que l'exécution n'est pas
    /// lancée — un opérateur qui change d'avis avant le lancement réel ne
    /// doit pas être bloqué par un premier choix.
    /// </summary>
    public async Task<string?> SetContinuityChoiceAsync(
        Guid executionId, CenterContinuityChoice choice, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";

        if (!execution.IsActive || execution.Status is ExecutionStatus.EnCours or ExecutionStatus.EnPause)
            return $"Cette exécution est en état {execution.Status} : le choix de continuité ne peut plus être modifié.";

        // FR-046 : « rester actif » ne se contente pas d'être noté — la
        // séquence exacte du texte est construite automatiquement : vérifier
        // puis arrêter le Standby AVANT le primaire, attendre son retour
        // actif, puis remettre le Standby en service. Idempotent : un second
        // appel ne double pas les étapes déjà insérées.
        if (choice == CenterContinuityChoice.ResterActif
            && execution.ContinuityChoice != CenterContinuityChoice.ResterActif)
        {
            var erreur = await InsererSequenceContinuiteAsync(db, execution, ct);
            if (erreur is not null) return erreur;

            // Enregistrée à part : mélanger, dans le même lot de commandes,
            // l'insertion de nouvelles étapes et la mise à jour d'étapes
            // existantes de la même table provoquait une fausse alerte de
            // concurrence optimiste (rowversion) côté SQL Server.
            await db.SaveChangesAsync(ct);
        }

        execution.ContinuityChoice = choice;
        execution.ContinuityChoiceBy = actor;
        execution.ContinuityChoiceAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await RenseignerComposantsAsync(execution.Id, ct);
        return null;
    }

    /// <summary>
    /// Construit la séquence exacte de FR-046 autour de la première étape
    /// visant le Center primaire : Vérifier puis Arrêter le Standby avant,
    /// Vérifier le retour actif du primaire puis Démarrer le Standby après —
    /// uniquement quand le primaire revient (Redémarrer), jamais après un
    /// simple Arrêter définitif, où il n'y a rien à attendre.
    /// </summary>
    private static async Task<string?> InsererSequenceContinuiteAsync(
        N4SentinelDbContext db, WorkflowExecution execution, CancellationToken ct)
    {
        var etapeCenter = execution.Steps
            .Where(s => s.Action is StepAction.Arreter or StepAction.Redemarrer)
            .OrderBy(s => s.Order)
            .FirstOrDefault(s => s.ComponentId is not null);

        if (etapeCenter?.ComponentId is null) return null;

        var center = await db.Components.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == etapeCenter.ComponentId, ct);
        if (center is null || center.Role != ComponentRole.CenterNode) return null;

        var standby = await db.Components.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EnvironmentId == execution.EnvironmentId
                                    && c.Role == ComponentRole.StandbyCenterNode, ct);

        // Sans Standby declare, "rester actif" est vrai par construction : il
        // n'y a rien vers quoi basculer, donc rien a organiser autour. Ce
        // n'est jamais bloquant — seul Basculer, qui exige un Standby apte,
        // peut echouer pour ce motif (voir ControlerContinuiteCenterAsync).
        if (standby is null) return null;

        var avantStandby = new ExecutionStep
        {
            Name = $"Vérifier le Standby « {standby.LogicalName} » avant bascule",
            Action = StepAction.Verifier,
            ComponentId = standby.Id,
            ComponentName = standby.LogicalName,
            State = ExecutionStepState.AVenir,
            TimeoutSeconds = etapeCenter.TimeoutSeconds,
            ExpectedSeconds = 30,
            WarningThresholdSeconds = 60
        };
        var arreterStandby = new ExecutionStep
        {
            Name = $"Arrêter le Standby « {standby.LogicalName} » (continuité FR-046)",
            Action = StepAction.Arreter,
            ComponentId = standby.Id,
            ComponentName = standby.LogicalName,
            State = ExecutionStepState.AVenir,
            TimeoutSeconds = etapeCenter.TimeoutSeconds,
            ExpectedSeconds = etapeCenter.ExpectedSeconds,
            WarningThresholdSeconds = etapeCenter.WarningThresholdSeconds
        };

        var nouvelles = new List<ExecutionStep> { avantStandby, arreterStandby };

        if (etapeCenter.Action == StepAction.Redemarrer)
        {
            nouvelles.Add(new ExecutionStep
            {
                Name = $"Vérifier le retour actif du primaire « {center.LogicalName} »",
                Action = StepAction.Verifier,
                ComponentId = center.Id,
                ComponentName = center.LogicalName,
                State = ExecutionStepState.AVenir,
                TimeoutSeconds = etapeCenter.TimeoutSeconds,
                ExpectedSeconds = 30,
                WarningThresholdSeconds = 60
            });
            nouvelles.Add(new ExecutionStep
            {
                Name = $"Remettre le Standby « {standby.LogicalName} » en service",
                Action = StepAction.Demarrer,
                ComponentId = standby.Id,
                ComponentName = standby.LogicalName,
                State = ExecutionStepState.AVenir,
                TimeoutSeconds = etapeCenter.TimeoutSeconds,
                ExpectedSeconds = etapeCenter.ExpectedSeconds,
                WarningThresholdSeconds = etapeCenter.WarningThresholdSeconds
            });
        }

        // Séquence finale construite explicitement — jamais déduite d'une
        // comparaison numérique d'Order, trop fragile face aux égalités.
        var pivot = etapeCenter.Order;
        var sequence = execution.Steps.Where(s => s.Order < pivot).OrderBy(s => s.Order).ToList();
        sequence.AddRange(nouvelles.Take(2));
        sequence.Add(etapeCenter);
        if (nouvelles.Count > 2) sequence.AddRange(nouvelles.Skip(2));
        sequence.AddRange(execution.Steps.Where(s => s.Order > pivot).OrderBy(s => s.Order));

        // Les étapes déjà existantes sont renumérotées et enregistrées À PART,
        // AVANT l'insertion des nouvelles lignes : mélanger, dans le même lot
        // de commandes, la mise à jour d'étapes existantes et l'insertion de
        // nouvelles lignes de la même table provoquait une fausse alerte de
        // concurrence optimiste (rowversion) côté fournisseur SQL Server.
        for (var i = 0; i < sequence.Count; i++)
            sequence[i].Order = i + 1;

        await db.SaveChangesAsync(ct);

        foreach (var nouvelle in nouvelles)
            nouvelle.ExecutionId = execution.Id;

        db.ExecutionSteps.AddRange(nouvelles);
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

        // AC-07 : une tentative de lancement refusee faute d'approbation est
        // elle-meme un evenement a tracer, pas seulement le blocage lui-meme.
        // Sans cette ecriture, un operateur pouvait essayer de lancer une
        // operation Production non approuvee autant de fois qu'il le
        // souhaitait sans que rien n'en reste dans la piste d'audit.
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

        // FR-006 : seules les versions validees et actives d'un environnement
        // peuvent servir a une operation - un environnement redescendu en
        // Brouillon ou Desactive depuis la preparation de cette execution ne
        // doit pas laisser passer un lancement au nom d'un etat perime.
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

        // LE PRE-CHECK EST INFRANCHISSABLE. Il n'y a pas de parametre pour le
        // sauter, pas de profil qui en dispense. Decouvrir un serveur
        // injoignable a la septieme etape d'un arret complet laisse
        // l'ecosysteme a moitie eteint, et le composant qu'on ne peut pas
        // joindre est justement celui qu'il faudrait arreter.
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

        // Les etapes DOIVENT etre chargees : le chargement differe n'est pas
        // active sur ce contexte. Sans cet Include, execution.Steps est une
        // collection vide et la reinterrogation FR-024 plus bas parcourt du
        // vide — la reprise repartirait sur un etat de composant perime sans
        // que rien ne le signale.
        var execution = await db.Executions
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);
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
                etape.ErrorType = null;
                etape.Evidence = $"Reprise décidée par {actor} après constat de l'état réel.";
            }
        }

        // FR-024 : Réinterroger SupervisionService pour les composants impliqués dans les étapes restantes
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
                // Ne bloque pas la reprise, mais met à jour l'état frais en base pour le moteur
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

        // L'execution TOURNE : le moteur ecrit sur cette meme ligne (progression,
        // issue d'etape) pendant que l'operateur demande l'annulation. Enregistrer
        // la demande via l'entite suivie faisait entrer le jeton RowVersion en
        // conflit et levait une DbUpdateConcurrencyException — c'est-a-dire que
        // le bouton « Annuler » echouait, sans explication, precisement quand on
        // s'en sert : pendant une operation en cours.
        //
        // ExecuteUpdateAsync ecrit sans entite suivie et donc sans jeton de
        // concurrence. C'est legitime ici : l'annulation n'est pas une
        // modification d'etat concurrente du moteur, c'est un DRAPEAU que le
        // moteur lira a la prochaine etape. La clause sur les etats non
        // terminaux garantit qu'on ne ressuscite pas une execution qui vient de
        // se terminer entre-temps.
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

        // FR-013/FR-027 : en Production, la matrice de criticité peut exiger
        // un second regard sur le contournement — un rôle habilité et un
        // motif ne suffisent alors plus, il faut un approbateur DISTINCT du
        // demandeur. Tant que ce second regard manque, l'étape reste bloquée :
        // le contournement n'est jamais appliqué sur la seule demande.
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

        return null;
    }

    /// <summary>
    /// Second regard sur un contournement soumis à double approbation par la
    /// matrice de criticité (FR-013/FR-027). Applique le contournement une
    /// fois ce regard obtenu — jamais avant.
    /// </summary>
    public async Task<string?> ApproveSkipAsync(Guid stepId, string approver, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var etape = await db.ExecutionSteps.Include(s => s.Execution)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (etape is null) return "Étape introuvable.";

        if (etape.SkippedBy is null)
            return $"L'étape « {etape.Name} » n'a pas de demande de contournement en attente.";

        if (etape.IsTerminal)
            return $"L'étape « {etape.Name} » est déjà terminée ({etape.State}).";

        if (string.Equals(etape.SkippedBy, approver, StringComparison.OrdinalIgnoreCase))
            return "Le second approbateur doit être une personne différente du demandeur — "
                 + "un double regard par la même personne n'en est pas un.";

        etape.SkipCoApprovedBy = approver;
        etape.SkipCoApprovedAt = DateTimeOffset.UtcNow;
        etape.State = ExecutionStepState.Ignore;
        etape.EndedAt = DateTimeOffset.UtcNow;
        etape.ProgressMessage = null;

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Contournement de l'étape « {Etape} » (demandé par {Demandeur}) approuvé par {Approbateur}.",
            etape.Name, etape.SkippedBy, approver);

        await auditWriter.WriteAsync(
            AuditAction.Contournement, AuditOutcome.Succes, approver,
            entityType: nameof(ExecutionStep), entityId: etape.Id.ToString(), entityLabel: etape.Name,
            environmentId: etape.Execution?.EnvironmentId,
            reason: $"Second regard sur le contournement demandé par {etape.SkippedBy} : {etape.SkipReason}",
            correlationId: etape.Execution?.CorrelationId, ct: ct);

        return null;
    }

    /// <summary>
    /// Nouvelle tentative sur une étape bloquée, décidée par un opérateur.
    ///
    /// Le moteur ne rejoue JAMAIS de lui-même une action mutative qui a échoué :
    /// réessayer sans avoir compris la cause est le meilleur moyen d'aggraver un
    /// incident. Quelqu'un doit avoir regardé.
    /// </summary>
    public async Task<string?> RetryStepAsync(Guid stepId, string actor, string? justificationDerogation = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var etape = await db.ExecutionSteps.FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (etape is null) return "Étape introuvable.";

        if (etape.State is not (ExecutionStepState.Bloque or ExecutionStepState.Echec))
            return $"L'étape « {etape.Name} » est en état {etape.State} : il n'y a rien à réessayer.";

        // FR-004 : Dérogation explicite auditée requise pour relancer une action destructrice
        if (etape.IsDestructive)
        {
            if (string.IsNullOrWhiteSpace(justificationDerogation))
                return "Cette action est destructrice. Une dérogation explicite (justification) est obligatoire pour forcer une nouvelle tentative (FR-004).";

            await auditWriter.WriteAsync(
                action: AuditAction.Contournement,
                outcome: AuditOutcome.Succes,
                actor: actor,
                entityType: nameof(ExecutionStep), entityId: stepId.ToString(),
                entityLabel: $"{etape.Execution?.WorkflowName} v{etape.Execution?.WorkflowVersion} - Étape {etape.Name}",
                environmentId: etape.Execution?.EnvironmentId, reason: justificationDerogation,
                correlationId: etape.Execution?.CorrelationId, ct: ct);
        }

        // FR-024 : avant toute nouvelle tentative, recollecter l'état réel du
        // composant et le comparer à ce que l'étape devait produire — un
        // échec de commande ne prouve pas que rien ne s'est produit. Si l'état
        // réel montre que l'action a déjà abouti, rejouer la commande
        // répéterait aveuglément une action déjà réalisée : c'est exactement
        // ce que le texte interdit.
        if (supervision is not null && etape.ComponentId is { } componentId
            && etape.Action is StepAction.Demarrer or StepAction.Arreter or StepAction.Redemarrer)
        {
            var releve = await supervision.EvaluateComponentAsync(componentId, ct);
            var (dejaRealisee, constat) = EvaluerExecutionPrecedente(etape.Action, releve);

            if (dejaRealisee)
            {
                etape.State = ExecutionStepState.Avertissement;
                etape.EndedAt = DateTimeOffset.UtcNow;
                etape.Error = null;
                etape.ErrorType = null;
                etape.Evidence = $"Nouvelle tentative annulée par {actor} après recollecte de l'état réel : "
                                + $"{constat} Rejouer la commande aurait répété une action déjà réalisée (FR-024).";

                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "Nouvelle tentative de « {Etape} » annulée après recollecte : {Constat}", etape.Name, constat);
                return null;
            }

            etape.ProgressMessage = $"Nouvelle tentative demandée par {actor}. État réel constaté avant reprise : {constat}";
        }
        else
        {
            etape.ProgressMessage = $"Nouvelle tentative demandée par {actor}.";
        }

        etape.State = ExecutionStepState.AVenir;
        etape.StartedAt = null;
        etape.EndedAt = null;
        etape.Error = null;
        etape.ErrorType = null;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Nouvelle tentative sur l'étape « {Etape} », décidée par {Acteur}.", etape.Name, actor);
        return null;
    }

    /// <summary>
    /// FR-024 : détermine si l'état réel constaté montre que l'action visée
    /// par l'étape a déjà été menée à bien — auquel cas rejouer la commande
    /// serait une répétition aveugle, pas une nouvelle tentative.
    /// </summary>
    private static (bool DejaRealisee, string Constat) EvaluerExecutionPrecedente(
        StepAction action, Supervision.ComponentHealthSnapshot releve) => action switch
    {
        StepAction.Demarrer or StepAction.Redemarrer => releve.State switch
        {
            ComponentState.Disponible => (true,
                $"le composant est déjà Disponible ({(releve.LogProofStatus == Supervision.LogProofState.Proved ? "démarrage prouvé par le journal" : "service actif, démarrage applicatif non prouvé")})."),
            ComponentState.Demarrage => (false, "le composant est en cours de démarrage : action partiellement engagée."),
            _ => (false, $"le composant est {DecrireEtatConstate(releve.State)} : l'action de démarrage n'a pas abouti.")
        },
        StepAction.Arreter => releve.State switch
        {
            ComponentState.Arret or ComponentState.Indisponible => (true, "le composant est déjà arrêté."),
            _ => (false, $"le composant est {DecrireEtatConstate(releve.State)} : l'action d'arrêt n'a pas abouti.")
        },
        _ => (false, $"état réel constaté : {DecrireEtatConstate(releve.State)}.")
    };

    private static string DecrireEtatConstate(ComponentState state) => state switch
    {
        ComponentState.Disponible => "disponible",
        ComponentState.Degrade => "dégradé",
        ComponentState.Indisponible => "indisponible",
        ComponentState.Demarrage => "en cours de démarrage",
        ComponentState.Arret => "arrêté",
        ComponentState.Maintenance => "en maintenance",
        ComponentState.NonSupervise => "non supervisé (état réel inconnu)",
        _ => "dans un état non déterminé"
    };

    /// <summary>
    /// Arrêt forcé d'une étape d'arrêt restée bloquée (FR-029B).
    ///
    /// Réservé aux étapes dont le dernier échec documente précisément un
    /// blocage en StopPending — le cas connu où le service Windows ne rend
    /// jamais la main (notamment le Standby Center Node). Le moteur ne
    /// déclenche JAMAIS cette action de lui-même : elle exige une justification
    /// explicite et laisse une trace d'audit distincte du contournement
    /// (<see cref="AuditAction.ArretForce"/>, pas <see cref="AuditAction.Contournement"/>),
    /// car forcer un arrêt bloqué et sauter un contrôle sont deux décisions
    /// différentes qui ne doivent pas se confondre dans le journal.
    /// </summary>
    public async Task<string?> ForcerArretAsync(
        Guid stepId, string actor, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "Un arrêt forcé sans justification n'est pas un arrêt forcé, c'est un trou dans la traçabilité.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var etape = await db.ExecutionSteps.Include(s => s.Execution)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct);

        if (etape is null) return "Étape introuvable.";

        if (etape.Action != StepAction.Arreter)
            return $"L'étape « {etape.Name} » n'est pas une étape d'arrêt : l'arrêt forcé ne s'y applique pas.";

        if (etape.State is not (ExecutionStepState.Bloque or ExecutionStepState.Echec))
            return $"L'étape « {etape.Name} » est en état {etape.State} : il n'y a pas d'arrêt bloqué à forcer.";

        // §3.19 : classification exacte plutôt qu'une recherche de sous-chaîne
        // dans un message libre — un message reformulé ne casse plus ce garde-fou.
        if (etape.ErrorType != StepErrorType.ComportementConnuStopPending)
            return $"L'étape « {etape.Name} » n'a pas échoué sur un blocage StopPending connu : "
                 + "l'arrêt forcé n'est proposé que pour ce cas précis. Utilisez « Réessayer » ou "
                 + "« Contourner » selon la situation, ou déclarez une intervention manuelle.";

        if (stepExecutor is null)
            return "L'arrêt forcé n'est pas disponible dans ce contexte (moteur d'exécution absent).";

        var resultat = await stepExecutor.ForcerArretAsync(etape, ct);

        etape.State = resultat.State;
        etape.Evidence = resultat.State != ExecutionStepState.Echec ? resultat.Message : etape.Evidence;
        etape.Error = resultat.State == ExecutionStepState.Echec ? resultat.Message : null;
        etape.ErrorType = resultat.State == ExecutionStepState.Echec ? resultat.ErrorType : null;
        etape.ForcedStopBy = actor;
        etape.ForcedStopReason = reason;
        etape.EndedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Arrêt forcé de l'étape « {Etape} » par {Acteur} : {Motif} — résultat : {Resultat}",
            etape.Name, actor, reason, resultat.Message);

        await auditWriter.WriteAsync(
            AuditAction.ArretForce, AuditOutcome.Succes, actor,
            entityType: nameof(ExecutionStep), entityId: etape.Id.ToString(), entityLabel: etape.Name,
            environmentId: etape.Execution?.EnvironmentId, reason: reason,
            correlationId: etape.Execution?.CorrelationId, ct: ct);

        await ReveillerMoteurAsync(ct);

        return null;
    }

    /// <summary>
    /// §3.19 : « revenir au dernier point stable » — l'une des options sûres
    /// après un échec, à côté de Réessayer et Contourner.
    ///
    /// NE REJOUE RIEN AUTOMATIQUEMENT. Cette méthode fabrique une (ou deux, si
    /// démarrages et arrêts se mélangent) opération(s) ponctuelle(s) VALIDÉE(S)
    /// — via <see cref="AdHocOperationService"/>, la même fabrique que « Nouvelle
    /// opération » — qui inversent les seules étapes RÉUSSIES et RÉVERSIBLES
    /// (Démarrer/Arrêter) de l'exécution bloquée. L'opérateur les lance ensuite
    /// à la main, comme toute autre opération : préchecks, verrou et audit
    /// s'appliquent normalement. Rien n'est jamais démarré ou arrêté ici
    /// directement — inverser une commande à l'aveugle, sans repasser par les
    /// mêmes garde-fous, serait exactement le genre de raccourci dangereux que
    /// ce produit refuse partout ailleurs.
    ///
    /// Redémarrer, Vérifier, Attendre et Intervention manuelle n'ont pas
    /// d'inverse sûr : ces étapes sont ignorées, jamais devinées.
    /// </summary>
    public async Task<RollbackPlanResult> RollbackToStablePointAsync(
        Guid executionId, string actor, string justification, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(justification))
            return RollbackPlanResult.Failed("Un retour au dernier point stable sans justification n'est pas tracé, donc pas sûr.");

        if (adHoc is null)
            return RollbackPlanResult.Failed("Le retour au dernier point stable n'est pas disponible dans ce contexte.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return RollbackPlanResult.Failed("Exécution introuvable.");

        if (execution.Status is not (ExecutionStatus.Echec or ExecutionStatus.EnPause))
            return RollbackPlanResult.Failed(
                $"Cette exécution est en état {execution.Status} : le retour au dernier point stable ne "
                + "s'applique qu'à une exécution en échec ou bloquée.");

        var reversibles = execution.Steps
            .Where(s => s.State == ExecutionStepState.Reussi
                     && s.Action is StepAction.Demarrer or StepAction.Arreter
                     && s.ComponentId is not null)
            .ToList();

        if (reversibles.Count == 0)
            return RollbackPlanResult.Failed(
                "Rien à annuler : cette exécution n'a modifié aucun état réversible (Démarrer/Arrêter) avec succès.");

        // On ramène en bas ce qui a été monté avant de remonter ce qui a été
        // descendu : minimise le temps où un composant inattendu reste actif.
        var aArreter = reversibles.Where(s => s.Action == StepAction.Demarrer)
            .Select(s => s.ComponentId!.Value).Distinct().ToList();
        var aDemarrer = reversibles.Where(s => s.Action == StepAction.Arreter)
            .Select(s => s.ComponentId!.Value).Distinct().ToList();

        var crees = new List<(Guid WorkflowId, string Name)>();

        if (aArreter.Count > 0)
        {
            var r = await adHoc.BuildAsync(execution.EnvironmentId, aArreter, StepAction.Arreter, AdHocShape.Groupe, actor, ct);
            if (!r.Succeeded) return RollbackPlanResult.Failed(r.Error!);
            crees.Add((r.WorkflowId, r.Name));
        }

        if (aDemarrer.Count > 0)
        {
            var r = await adHoc.BuildAsync(execution.EnvironmentId, aDemarrer, StepAction.Demarrer, AdHocShape.Groupe, actor, ct);
            if (!r.Succeeded) return RollbackPlanResult.Failed(r.Error!);
            crees.Add((r.WorkflowId, r.Name));
        }

        await auditWriter.WriteAsync(
            AuditAction.Creation, AuditOutcome.Succes, actor,
            entityType: nameof(WorkflowExecution), entityId: execution.Id.ToString(), entityLabel: execution.WorkflowName,
            environmentId: execution.EnvironmentId, reason: justification,
            detail: $"Retour au dernier point stable préparé : {string.Join(", ", crees.Select(c => c.Name))}. "
                  + "À lancer explicitement depuis Nouvelle opération.",
            correlationId: execution.CorrelationId, ct: ct);

        logger.LogWarning(
            "Retour au dernier point stable préparé pour {Correlation} par {Acteur} : {N} opération(s) créée(s), motif : {Motif}",
            execution.CorrelationId, actor, crees.Count, justification);

        return RollbackPlanResult.Ok(crees);
    }

    /// <summary>
    /// Ouvre une session de diagnostic pour aider à la décision sur une étape
    /// bloquée ou en échec (FR-029) — pré-remplie avec le symptôme, le
    /// composant et l'action concernés, pour que l'opérateur n'ait pas à les
    /// ressaisir. Si une session est déjà reliée, la retourne sans en créer
    /// une seconde : une étape ne doit pas accumuler des diagnostics parallèles.
    /// </summary>
    public async Task<(string? Error, Guid? SessionId)> OuvrirDiagnosticDepuisEtapeAsync(
        Guid stepId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var etape = await db.ExecutionSteps.Include(s => s.Execution)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (etape is null) return ("Étape introuvable.", null);

        if (etape.DiagnosticSessionId is { } existante) return (null, existante);

        if (etape.State is not (ExecutionStepState.Bloque or ExecutionStepState.Echec))
            return ($"L'étape « {etape.Name} » est en état {etape.State} : rien à diagnostiquer.", null);

        if (diagnostics is null)
            return ("Le module diagnostic n'est pas disponible dans ce contexte.", null);

        var symptome = etape.Error ?? etape.ProgressMessage ?? "Étape bloquée sans message d'erreur détaillé.";

        var sessionId = await diagnostics.CreateAsync(
            etape.Execution!.EnvironmentId,
            $"Étape bloquée : {etape.Name}",
            $"Ouvert depuis l'opération « {etape.Execution.WorkflowName} » (étape {etape.Order} — {LibelleAction(etape.Action)} "
              + $"sur {etape.ComponentName ?? "aucun composant"}). Symptôme : {symptome}",
            null, actor, null, null, ct);

        etape.DiagnosticSessionId = sessionId;
        await db.SaveChangesAsync(ct);

        return (null, sessionId);
    }

    private static string LibelleAction(StepAction action) => action switch
    {
        StepAction.Demarrer => "démarrage",
        StepAction.Arreter => "arrêt",
        StepAction.Redemarrer => "redémarrage",
        StepAction.Verifier => "vérification",
        StepAction.Attendre => "attente",
        StepAction.InterventionManuelle => "intervention manuelle",
        _ => action.ToString()
    };

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

    /// <summary>
    /// Compte rendu d'une intervention manuelle (FR-026). Quand le workflow
    /// déclare la preuve jointe obligatoire, la confirmation est refusée sans
    /// fichier — un commentaire seul ne suffit pas dans ce cas, comme l'exige
    /// le texte (« joindre une preuve lorsque celle-ci est déclarée obligatoire »).
    /// </summary>
    public async Task<string?> ConfirmStepAsync(
        Guid stepId, string actor, string? note, bool succeeded,
        byte[]? evidenceFile = null, string? evidenceFileName = null, string? evidenceContentType = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var etape = await db.ExecutionSteps.FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (etape is null) return "Étape introuvable.";

        if (etape.Action != StepAction.InterventionManuelle)
            return "Cette étape est exécutée par le moteur : elle attend une autorisation, pas un compte rendu.";

        if (etape.State != ExecutionStepState.EnAttente)
            return $"L'étape « {etape.Name} » est en état {etape.State} : elle n'attend pas de confirmation.";

        if (etape.RequiresEvidenceFile && (evidenceFile is null || evidenceFile.Length == 0))
            return $"L'étape « {etape.Name} » exige une preuve jointe pour être confirmée : "
                 + "un commentaire seul ne suffit pas ici.";

        if (evidenceFile is { Length: > 0 })
        {
            if (evidenceFile.Length > EvidenceFileMaxBytes)
                return $"La preuve jointe dépasse {EvidenceFileMaxBytes / 1024 / 1024} Mo.";

            if (antivirus is not null)
            {
                var scan = await antivirus.ScanAsync(evidenceFile, ct);
                if (scan.Verdict == ScanVerdict.Infecte)
                    return "Preuve refusée : menace détectée par l'antivirus"
                         + (scan.ThreatName is { Length: > 0 } m ? $" ({m})" : ".") + ".";
            }

            etape.EvidenceFileContent = evidenceFile;
            etape.EvidenceFileName = evidenceFileName;
            etape.EvidenceFileContentType = evidenceContentType;
        }

        etape.ConfirmedBy = actor;
        etape.ConfirmedAt = DateTimeOffset.UtcNow;
        etape.OperatorNote = note;
        etape.State = succeeded ? ExecutionStepState.Reussi : ExecutionStepState.Echec;
        etape.EndedAt = DateTimeOffset.UtcNow;

        var mentionPreuve = etape.EvidenceFileName is { Length: > 0 } nomFichier
            ? $" Preuve jointe : {nomFichier}."
            : string.Empty;

        etape.Evidence = succeeded
            ? $"Intervention manuelle confirmée par {actor}.{mentionPreuve}" + (note is null ? "" : $" Note : {note}")
            : null;

        etape.Error = succeeded ? null
            : $"Intervention manuelle déclarée non réalisée par {actor}.{mentionPreuve}" + (note is null ? "" : $" Note : {note}");

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
            .Include(x => x.ExternalActions.OrderBy(a => a.OccurredAt))
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);
    }

    /// <summary>
    /// §3.19 : déclare une action manuelle effectuée HORS de N4 Sentinel
    /// pendant cette exécution — voir <see cref="ExternalActionDeclaration"/>.
    /// </summary>
    public async Task<string?> DeclareExternalActionAsync(
        Guid executionId, string description, DateTimeOffset occurredAt, string declaredBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(description)) return "Décrivez ce qui a été fait.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return "Exécution introuvable.";

        db.Add(new ExternalActionDeclaration
        {
            EnvironmentId = execution.EnvironmentId,
            WorkflowExecutionId = executionId,
            Description = description.Trim(),
            OccurredAt = occurredAt,
            DeclaredBy = declaredBy
        });

        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.Creation, AuditOutcome.Succes, declaredBy,
            entityType: nameof(ExternalActionDeclaration), entityId: executionId.ToString(),
            entityLabel: execution.WorkflowName, environmentId: execution.EnvironmentId,
            detail: $"Action externe déclarée : {description.Trim()}", ct: ct);

        return null;
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

/// <summary>§3.19 : plan de retour au dernier point stable — une ou deux opérations ponctuelles VALIDÉES, jamais lancées automatiquement.</summary>
public sealed record RollbackPlanResult
{
    public bool Succeeded { get; init; }
    public List<(Guid WorkflowId, string Name)> Workflows { get; init; } = [];
    public string? Error { get; init; }

    public static RollbackPlanResult Ok(List<(Guid WorkflowId, string Name)> workflows) =>
        new() { Succeeded = true, Workflows = workflows };

    public static RollbackPlanResult Failed(string error) => new() { Succeeded = false, Error = error };
}
