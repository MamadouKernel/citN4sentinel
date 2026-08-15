using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Diagnostic;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Moteur d'exécution des workflows (FR-020, NFR-002).
///
/// L'ÉTAT VIT EN BASE, PAS EN MÉMOIRE. Cette classe ne conserve que la liste des
/// exécutions qu'elle pilote en ce moment ; tout le reste — étape courante,
/// preuves, décisions de l'opérateur — est relu à chaque tour. C'est ce qui
/// permet à une exécution interrompue par le redémarrage du serveur applicatif
/// de reprendre, et c'est ce qui garantit qu'une commande envoyée depuis un
/// écran est prise en compte même si l'écran a été fermé entre-temps.
///
/// Le moteur ne décide jamais à la place de l'opérateur sur un doute. Il
/// s'arrête et demande.
/// </summary>
public sealed class OrchestrationEngine(
    IServiceScopeFactory scopeFactory,
    ILogger<OrchestrationEngine> logger)
{
    /// <summary>Exécutions pilotées par ce processus. Clé = identifiant d'exécution.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _enCours = new();

    /// <summary>Progression de l'étape courante, pour rafraîchir l'écran sans requête.</summary>
    private readonly ConcurrentDictionary<Guid, string> _progression = new();

    public event Action<Guid>? ExecutionChanged;

    public string? ProgressOf(Guid executionId) =>
        _progression.TryGetValue(executionId, out var m) ? m : null;

    public bool IsRunningHere(Guid executionId) => _enCours.ContainsKey(executionId);

    // -----------------------------------------------------------------------
    // Prise en charge
    // -----------------------------------------------------------------------
    /// <summary>
    /// Prend en charge les exécutions en cours qui ne le sont pas déjà.
    /// Appelée périodiquement : une exécution lancée depuis un écran est ainsi
    /// ramassée sans que l'écran ait à rester ouvert.
    /// </summary>
    /// <returns>Vrai si au moins une exécution est pilotée par ce processus.</returns>
    public async Task<bool> PickUpAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<N4SentinelDbContext>>()
            .CreateDbContextAsync(ct);

        var candidates = await db.Executions
            .AsNoTracking()
            .Where(x => x.Status == ExecutionStatus.EnCours || x.Status == ExecutionStatus.AnnulationDemandee)
            .Select(x => x.Id)
            .ToListAsync(ct);

        foreach (var id in candidates)
        {
            if (!_enCours.TryAdd(id, 0)) continue;

            _ = Task.Run(async () =>
            {
                try { await RunAsync(id, ct); }
                catch (OperationCanceledException) { /* arret du service */ }
                catch (Exception ex) { logger.LogError(ex, "Le pilotage de l'exécution {Id} s'est interrompu.", id); }
                finally { _enCours.TryRemove(id, out _); }
            }, ct);
        }

        return !_enCours.IsEmpty;
    }

    /// <summary>
    /// Réconciliation au démarrage de l'application (NFR-002).
    ///
    /// Une exécution laissée « en cours » par un arrêt brutal ne reprend pas
    /// toute seule. On distingue deux situations :
    ///
    ///  — l'interruption est tombée ENTRE deux étapes : rien n'était en vol, la
    ///    reprise est sûre et automatique ;
    ///  — l'interruption est tombée PENDANT une étape : on ignore si la commande
    ///    a été émise, si elle a abouti, si le composant a démarré. Reprendre
    ///    sur une base fausse est pire que s'arrêter. L'exécution passe en
    ///    « réconciliation requise » et attend qu'un opérateur constate l'état
    ///    réel.
    /// </summary>
    public async Task ReconcileAtStartupAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<N4SentinelDbContext>>()
            .CreateDbContextAsync(ct);

        var abandonnees = await db.Executions
            .Include(x => x.Steps)
            .Where(x => x.Status == ExecutionStatus.EnCours || x.Status == ExecutionStatus.AnnulationDemandee)
            .ToListAsync(ct);

        if (abandonnees.Count == 0) return;

        foreach (var execution in abandonnees)
        {
            var enVol = execution.Steps
                .Where(s => s.State is ExecutionStepState.EnCours or ExecutionStepState.Verification)
                .ToList();

            if (enVol.Count == 0)
            {
                logger.LogInformation(
                    "Exécution {Correlation} reprise automatiquement : l'interruption est tombée entre deux étapes, "
                    + "aucune action n'était en vol.", execution.CorrelationId);
                continue;
            }

            execution.Status = ExecutionStatus.ReconciliationRequise;
            execution.Outcome =
                $"L'application s'est arrêtée pendant l'étape « {enVol[0].Name} ». "
                + "Il est impossible de savoir si l'action a été émise, ni si elle a abouti. "
                + "Constatez l'état réel du composant avant de reprendre : reprendre sur une base fausse "
                + "est plus dangereux que de s'arrêter ici.";

            foreach (var etape in enVol)
                etape.ProgressMessage = "Étape interrompue par l'arrêt de l'application. État réel à constater.";

            logger.LogWarning(
                "Exécution {Correlation} placée en réconciliation : l'étape « {Etape} » était en vol "
                + "au moment de l'arrêt.", execution.CorrelationId, enVol[0].Name);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Prolonge les verrous des exécutions non terminées. Une exécution en pause
    /// garde son verrou : l'écosystème est à moitié dans un état voulu, personne
    /// d'autre ne doit y toucher.
    /// </summary>
    public async Task RenewLocksAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = await sp.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>().CreateDbContextAsync(ct);
        var locks = sp.GetRequiredService<EnvironmentLockService>();

        var actives = await db.Executions
            .AsNoTracking()
            .Where(x => !x.IsSimulation
                        && (x.Status == ExecutionStatus.EnCours
                            || x.Status == ExecutionStatus.EnPause
                            || x.Status == ExecutionStatus.AnnulationDemandee
                            || x.Status == ExecutionStatus.ReconciliationRequise))
            .Select(x => x.Id)
            .ToListAsync(ct);

        foreach (var id in actives)
            await locks.RenewAsync(id, ct);
    }

    // -----------------------------------------------------------------------
    // Déroulement
    // -----------------------------------------------------------------------
    private async Task RunAsync(Guid executionId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var dbFactory = sp.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>();
        var executor = sp.GetRequiredService<StepExecutor>();
        var locks = sp.GetRequiredService<EnvironmentLockService>();

        while (!ct.IsCancellationRequested)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var execution = await db.Executions
                .Include(x => x.Steps)
                .FirstOrDefaultAsync(x => x.Id == executionId, ct);

            if (execution is null) return;

            execution.LastHeartbeatAt = DateTimeOffset.UtcNow;

            // --- Annulation demandee : l'etape en cours est allee a son terme,
            // on s'arrete ici plutot que de couper au milieu de la suivante.
            if (execution.Status == ExecutionStatus.AnnulationDemandee)
            {
                foreach (var restante in execution.Steps.Where(s => !s.IsTerminal))
                    restante.State = ExecutionStepState.Annule;

                await TerminerAsync(db, execution, ExecutionStatus.Annule,
                    $"Annulée par {execution.CancelRequestedBy}. Les étapes déjà exécutées ne sont PAS défaites : "
                    + "l'écosystème est dans un état intermédiaire, à constater avant toute reprise.", locks, ct);

                Notifier(executionId);
                return;
            }

            // --- Pause demandee : elle prend effet entre deux etapes.
            if (execution.PauseRequestedBy is not null && execution.Status == ExecutionStatus.EnCours)
            {
                execution.Status = ExecutionStatus.EnPause;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Exécution {Correlation} mise en pause.", execution.CorrelationId);
                Notifier(executionId);
                return;
            }

            if (execution.Status != ExecutionStatus.EnCours)
            {
                await db.SaveChangesAsync(ct);
                return;
            }

            var restantes = execution.Steps
                .Where(s => !s.IsTerminal)
                .OrderBy(s => s.Order)
                .ToList();

            // --- Plus rien a faire : on conclut.
            if (restantes.Count == 0)
            {
                var (statut, motif) = Conclure(execution);
                await TerminerAsync(db, execution, statut, motif, locks, ct);
                Notifier(executionId);
                return;
            }

            var suivante = restantes[0];

            // --- Etape en attente d'un geste humain : on ne fait rien, on attend.
            if (suivante.State is ExecutionStepState.EnAttente or ExecutionStepState.Bloque)
            {
                await db.SaveChangesAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            // --- Confirmation préalable exigée : le moteur vérifie si le Palier 2 (automatique bout en bout) est actif.
            if (suivante.RequiresConfirmation && suivante.ConfirmedBy is null)
            {
                var isPalier2Auto = execution.AutomationLevel == AutomationLevel.AutomatiqueBoutEnBout 
                                  && !execution.IsFallbackSemiAutoForced 
                                  && suivante.Action != StepAction.InterventionManuelle;

                if (isPalier2Auto)
                {
                    suivante.ConfirmedBy = "Système (Palier 2 Automatique)";
                    suivante.ConfirmedAt = DateTimeOffset.UtcNow;
                    suivante.ProgressMessage = "Validation automatique Palier 2 — Exécution contrôlée de bout en bout.";
                    logger.LogInformation("[Palier 2] Auto-confirmation de l'étape {StepName} pour l'exécution {CorrelationId}.", suivante.Name, execution.CorrelationId);
                }
                else
                {
                    suivante.State = ExecutionStepState.EnAttente;
                    suivante.ProgressMessage =
                        "Cette étape exige une confirmation explicite avant d'être exécutée.";
                    await db.SaveChangesAsync(ct);
                    Notifier(executionId);
                    continue;
                }
            }

            // --- Constitution du lot a lancer ensemble (FR-023) : la premiere
            // etape, puis toute suite CONTIGUE d'etapes declarees
            // parallelisables et immediatement pretes. Le validateur de
            // sequence a deja refuse, a la preparation, toute parallelisation
            // qui violerait le graphe de dependances (ex. plusieurs noeuds
            // Cluster demarres ensemble) : le moteur peut donc lancer le lot
            // sans le rejuger lui-meme. Une etape qui attend une confirmation
            // ou un geste humain arrete le lot ; elle sera traitee seule, au
            // tour suivant.
            var lot = new List<ExecutionStep> { suivante };
            if (suivante.CanRunInParallel)
            {
                foreach (var candidate in restantes.Skip(1))
                {
                    if (!candidate.CanRunInParallel) break;
                    if (candidate.State is ExecutionStepState.EnAttente or ExecutionStepState.Bloque) break;
                    if (candidate.RequiresConfirmation && candidate.ConfirmedBy is null) break;
                    lot.Add(candidate);
                }
            }

            // --- Lancement du lot.
            var enParallele = lot.Count > 1;
            foreach (var etape in lot)
            {
                etape.State = ExecutionStepState.EnCours;
                etape.StartedAt ??= DateTimeOffset.UtcNow;
                etape.AttemptCount++;
                etape.ProgressMessage = enParallele ? "Démarrage de l'étape (en parallèle)." : "Démarrage de l'étape.";
            }
            await db.SaveChangesAsync(ct);
            Notifier(executionId);

            if (enParallele)
                logger.LogInformation(
                    "[{Correlation}] Lancement en parallèle de {N} étapes : {Etapes}.",
                    execution.CorrelationId, lot.Count, string.Join(", ", lot.Select(e => e.Name)));

            var taches = lot.Select(etape => ExecuterUneEtapeAsync(
                dbFactory, executor, executionId, execution.CorrelationId, execution.IsSimulation, etape, ct));

            await Task.WhenAll(taches);

            _progression.TryRemove(executionId, out _);
            Notifier(executionId);
        }
    }

    /// <summary>
    /// Exécute une étape et applique son issue. Isolé de <see cref="RunAsync"/>
    /// pour pouvoir être lancé plusieurs fois à la fois via
    /// <see cref="Task.WhenAll"/> (FR-023) — chaque appel utilise son propre
    /// contexte EF Core, jamais partagé entre tâches concurrentes.
    /// </summary>
    private async Task ExecuterUneEtapeAsync(
        IDbContextFactory<N4SentinelDbContext> dbFactory, StepExecutor executor, Guid executionId,
        string correlationId, bool isSimulation, ExecutionStep suivante, CancellationToken ct)
    {
        var etapeId = suivante.Id;
        var progress = new Progress<string>(message =>
        {
            var masque = SecretMasker.Masquer(message).Texte;
            _progression[executionId] = $"[{suivante.Name}] {masque}";
            _ = EnregistrerProgressionAsync(dbFactory, etapeId, message);
            Notifier(executionId);
        });

        logger.LogInformation(
            "[{Correlation}] Étape {Ordre} — {Etape} ({Action}) sur {Composant}.",
            correlationId, suivante.Order, suivante.Name, suivante.Action,
            suivante.ComponentName ?? "—");

        var issue = await executor.ExecuteAsync(suivante, isSimulation, progress, ct);

        await AppliquerAsync(dbFactory, executionId, etapeId, issue, ct);
    }

    /// <summary>
    /// Inscrit l'issue de l'étape et, si elle a échoué, applique la conduite à
    /// tenir déclarée au workflow.
    /// </summary>
    private async Task AppliquerAsync(
        IDbContextFactory<N4SentinelDbContext> dbFactory, Guid executionId, Guid stepId,
        StepOutcome issue, CancellationToken ct)
    {
        // NOUVELLE TENTATIVE SUR CONFLIT DE CONCURRENCE. Un message
        // d'avancement encore en vol peut avoir modifie la ligne entre notre
        // lecture et notre ecriture. Laisser remonter l'erreur tuerait le
        // pilote et laisserait l'execution bloquee en « en cours » jusqu'au
        // redemarrage de l'application - sans que rien ne l'explique.
        //
        // L'issue de l'etape, elle, doit etre enregistree : c'est la seule
        // trace de ce qui vient de se passer sur l'ecosysteme.
        for (var tentative = 1; ; tentative++)
        {
            try
            {
                await AppliquerUneFoisAsync(dbFactory, executionId, stepId, issue, ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (tentative < 4)
            {
                logger.LogDebug(
                    "Conflit de concurrence en enregistrant l'issue de l'étape {Etape} "
                    + "(tentative {Tentative}). Relecture et nouvelle tentative.", stepId, tentative);

                await Task.Delay(50 * tentative, ct);
            }
        }
    }

    private async Task AppliquerUneFoisAsync(
        IDbContextFactory<N4SentinelDbContext> dbFactory, Guid executionId, Guid stepId,
        StepOutcome issue, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return;

        var etape = execution.Steps.FirstOrDefault(s => s.Id == stepId);
        if (etape is null) return;

        // Le contournement ou l'annulation ont pu tomber pendant que l'etape
        // tournait : la decision de l'operateur prime sur l'issue technique.
        if (etape.IsTerminal)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        switch (issue.State)
        {
            case ExecutionStepState.Reussi:
                etape.State = ExecutionStepState.Reussi;
                etape.Evidence = Tronquer(issue.Message);
                etape.EndedAt = DateTimeOffset.UtcNow;
                etape.ProgressMessage = null;
                break;

            case ExecutionStepState.Avertissement:
                etape.State = ExecutionStepState.Avertissement;
                etape.Evidence = Tronquer(issue.Message);
                etape.EndedAt = DateTimeOffset.UtcNow;
                etape.ProgressMessage = null;
                break;

            case ExecutionStepState.EnAttente:
                etape.State = ExecutionStepState.EnAttente;
                etape.ProgressMessage = Tronquer(issue.Message, 1000);
                break;

            default:
                etape.Error = Tronquer(issue.Message);
                etape.ProgressMessage = null;

                // Nouvelle tentative automatique : autorisee uniquement si le
                // workflow l'a explicitement prevue, et jamais sur une action
                // d'arret - la validation du workflow l'interdit deja.
                var peutReessayer = etape.AutomaticRetry && etape.AttemptCount <= etape.MaxRetries;

                if (peutReessayer)
                {
                    etape.State = ExecutionStepState.AVenir;
                    etape.StartedAt = null;
                    etape.ProgressMessage =
                        $"Tentative {etape.AttemptCount} en échec. Nouvelle tentative automatique "
                        + $"({etape.AttemptCount}/{etape.MaxRetries + 1}).";
                    break;
                }

                switch (etape.FailurePolicy)
                {
                    case StepFailurePolicy.Continuer:
                        etape.State = ExecutionStepState.Echec;
                        etape.EndedAt = DateTimeOffset.UtcNow;
                        logger.LogWarning(
                            "[{Correlation}] Étape « {Etape} » en échec, la séquence continue "
                            + "conformément à sa politique déclarée.", execution.CorrelationId, etape.Name);
                        break;

                    case StepFailurePolicy.DemanderALOperateur:
                        etape.State = ExecutionStepState.Bloque;
                        execution.Status = ExecutionStatus.EnPause;
                        execution.Outcome =
                            $"Suspendue à l'étape « {etape.Name} » : {Tronquer(issue.Message, 900)} "
                            + "Le moteur attend une décision — réessayer, contourner, ou annuler.";
                        break;

                    default: // Bloquer
                        etape.State = ExecutionStepState.Echec;
                        etape.EndedAt = DateTimeOffset.UtcNow;

                        foreach (var restante in execution.Steps.Where(s => !s.IsTerminal && s.Id != etape.Id))
                            restante.State = ExecutionStepState.Annule;

                        execution.Status = ExecutionStatus.Echec;
                        execution.EndedAt = DateTimeOffset.UtcNow;
                        execution.Outcome =
                            $"Arrêtée à l'étape « {etape.Name} » : {Tronquer(issue.Message, 900)} "
                            + "Les étapes suivantes n'ont pas été exécutées. Les précédentes ne sont pas défaites.";

                        await db.SaveChangesAsync(ct);

                        using (var scope = scopeFactory.CreateScope())
                            await scope.ServiceProvider.GetRequiredService<EnvironmentLockService>()
                                .ReleaseAsync(executionId, ct);
                        return;
                }
                break;
        }

        await db.SaveChangesAsync(ct);
    }

    private static (ExecutionStatus, string) Conclure(WorkflowExecution execution)
    {
        var echecs = execution.Steps.Count(s => s.State == ExecutionStepState.Echec);
        var reserves = execution.Steps.Count(s => s.State == ExecutionStepState.Avertissement);
        var contournees = execution.Steps.Count(s => s.State == ExecutionStepState.Ignore);

        if (echecs > 0)
            return (ExecutionStatus.TermineAvecAvertissements,
                $"Séquence terminée, mais {echecs} étape(s) en échec, poursuivies conformément à leur politique. "
                + "L'état de l'écosystème doit être vérifié.");

        if (reserves > 0 || contournees > 0)
        {
            var parties = new List<string>();
            if (reserves > 0)
                parties.Add($"{reserves} étape(s) dont le résultat n'a pas pu être PROUVÉ (à confirmer)");
            if (contournees > 0)
                parties.Add($"{contournees} étape(s) contournée(s) par décision d'un opérateur");

            return (ExecutionStatus.TermineAvecAvertissements,
                "Séquence terminée avec réserves : " + string.Join(", ", parties) + ".");
        }

        return (ExecutionStatus.TermineSucces,
            "Séquence terminée. Chaque étape a produit la preuve de son aboutissement.");
    }

    private static async Task TerminerAsync(
        N4SentinelDbContext db, WorkflowExecution execution, ExecutionStatus statut, string motif,
        EnvironmentLockService locks, CancellationToken ct)
    {
        execution.Status = statut;
        execution.EndedAt = DateTimeOffset.UtcNow;
        execution.Outcome = motif;

        await db.SaveChangesAsync(ct);
        await locks.ReleaseAsync(execution.Id, ct);
    }

    /// <summary>
    /// Écrit le message d'avancement.
    ///
    /// MISE À JOUR DIRECTE, SANS SUIVI D'ENTITÉ. Charger l'étape puis
    /// l'enregistrer ferait entrer ce message dans la course du jeton de
    /// concurrence : la progression et l'issue de l'étape modifient la même
    /// ligne, et l'écriture de l'issue échouerait avec une erreur de
    /// concurrence — ce qui tuait le pilote en silence et laissait l'exécution
    /// bloquée en « en cours » jusqu'au redémarrage.
    ///
    /// La progression est un confort d'affichage : elle n'a aucune raison de
    /// pouvoir faire échouer quoi que ce soit.
    /// </summary>
    private static async Task EnregistrerProgressionAsync(
        IDbContextFactory<N4SentinelDbContext> dbFactory, Guid stepId, string message)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            // Restreint aux etapes qui tournent : un message en retard ne doit
            // pas venir ecraser l'etat d'une etape deja conclue.
            // Masquage systematique (SEC-005 / FR-021) : ce message de
            // progression peut porter une ligne de journal N4 lue en direct,
            // le meme canal par lequel un secret peut fuir (voir StepOutcome).
            var masque = SecretMasker.Masquer(message).Texte;

            await db.ExecutionSteps
                .Where(s => s.Id == stepId
                            && (s.State == ExecutionStepState.EnCours
                                || s.State == ExecutionStepState.Verification))
                .ExecuteUpdateAsync(m => m.SetProperty(s => s.ProgressMessage, Tronquer(masque, 1000)));
        }
        catch
        {
            // Meme en cas d'echec, rien ne doit remonter : l'operation continue.
        }
    }

    private void Notifier(Guid executionId)
    {
        try { ExecutionChanged?.Invoke(executionId); }
        catch (Exception ex) { logger.LogDebug(ex, "Abonné en erreur lors de la notification."); }
    }

    /// <summary>
    /// Bascule d'urgence 1-Click vers le mode Semi-Automatique (Palier 1).
    /// Interrompt l'enchaînement automatique et remet l'exécution sous contrôle manuel strict.
    /// </summary>
    public async Task<bool> ToggleFallbackToSemiAutoAsync(Guid executionId, string actor, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<N4SentinelDbContext>>()
            .CreateDbContextAsync(ct);

        var execution = await db.Executions.FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return false;

        execution.AutomationLevel = AutomationLevel.SemiAutomatique;
        execution.IsFallbackSemiAutoForced = true;

        await db.SaveChangesAsync(ct);
        logger.LogWarning("[Urgence Palier 1] {Actor} a basculé l'exécution {CorrelationId} en mode Semi-Automatique.", actor, execution.CorrelationId);

        Notifier(executionId);
        return true;
    }

    private static string Tronquer(string texte, int max = 2000) =>
        texte.Length <= max ? texte : texte[..(max - 1)] + "…";
}
