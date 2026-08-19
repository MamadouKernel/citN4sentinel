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
    AdHocOperationService? adHoc = null,
    UseCases.PrepareExecutionUseCase? prepareUseCase = null,
    UseCases.ApproveExecutionUseCase? approveUseCase = null,
    UseCases.ControlExecutionUseCase? controlUseCase = null)
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
    public Task<PrepareResult> PrepareAsync(
        Guid workflowId, string requestedBy, string reason,
        string? ticketReference = null, bool isSimulation = false,
        DateTimeOffset? startWindow = null, DateTimeOffset? endWindow = null,
        CancellationToken ct = default)
    {
        if (prepareUseCase is null) throw new InvalidOperationException("PrepareExecutionUseCase not configured.");
        return prepareUseCase.ExecuteAsync(workflowId, requestedBy, reason, ticketReference, isSimulation, startWindow, endWindow, ct);
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
    public Task<string?> ApproveAsync(Guid executionId, string approvedBy, CancellationToken ct = default)
    {
        if (approveUseCase is null) throw new InvalidOperationException("ApproveExecutionUseCase not configured.");
        return approveUseCase.ExecuteAsync(executionId, approvedBy, ct);
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

    public Task<string?> StartAsync(Guid executionId, string actor, CancellationToken ct = default)
    {
        if (controlUseCase is null) throw new InvalidOperationException("ControlExecutionUseCase not configured.");
        return controlUseCase.StartAsync(executionId, actor, ct);
    }

    // -----------------------------------------------------------------------
    // Commandes en cours d'exécution
    // -----------------------------------------------------------------------
    public Task<string?> RequestPauseAsync(Guid executionId, string actor, CancellationToken ct = default)
    {
        if (controlUseCase is null) throw new InvalidOperationException("ControlExecutionUseCase not configured.");
        return controlUseCase.RequestPauseAsync(executionId, actor, ct);
    }

    public Task<string?> ResumeAsync(Guid executionId, string actor, CancellationToken ct = default)
    {
        if (controlUseCase is null) throw new InvalidOperationException("ControlExecutionUseCase not configured.");
        return controlUseCase.ResumeAsync(executionId, actor, ct);
    }

    public Task<string?> RequestCancelAsync(Guid executionId, string actor, CancellationToken ct = default)
    {
        if (controlUseCase is null) throw new InvalidOperationException("ControlExecutionUseCase not configured.");
        return controlUseCase.RequestCancelAsync(executionId, actor, ct);
    }

    /// <summary>
    /// Contournement d'une étape (FR-022, FR-027). Une étape non déclarée
    /// contournable ne peut JAMAIS être ignorée, quel que soit le profil du
    /// demandeur : c'est la seule façon de garantir qu'un contrôle bloquant
    /// reste bloquant.


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

    public Task<string?> SkipStepAsync(
        Guid stepId, string actor, string reason, CancellationToken ct = default)
    {
        if (controlUseCase is null) throw new InvalidOperationException("ControlExecutionUseCase not configured.");
        return controlUseCase.SkipStepAsync(stepId, actor, reason, ct);
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
