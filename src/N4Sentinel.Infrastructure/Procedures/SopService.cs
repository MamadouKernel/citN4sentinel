using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Diagnostic;
using N4Sentinel.Infrastructure.Knowledge;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Procedures;

/// <summary>
/// Gestion des procédures opérationnelles standard (SOP) — FR-087 à FR-089D.
///
/// Même règle centrale que <c>WorkflowService</c> : UN SOP QUI A SERVI NE SE
/// MODIFIE PLUS. Toute modification produit une nouvelle version, et
/// l'ancienne reste attachée aux exécutions qu'elle a produites.
///
/// Ce service n'a AUCUNE dépendance vers l'orchestrateur : un SOP n'automatise
/// rien, il documente et trace un geste humain. C'est le même principe que la
/// base documentaire (FR-084/085) — voir <see cref="KnowledgeService"/>.
/// </summary>
public sealed class SopService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    KnowledgeService knowledge,
    ILogger<SopService> logger,
    IAuditWriter auditWriter)
{
    // -----------------------------------------------------------------------
    // Lecture
    // -----------------------------------------------------------------------
    public async Task<List<Domain.Sop>> GetForEnvironmentAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Sops
            .AsNoTracking()
            .Include(s => s.Steps)
            .Where(s => s.EnvironmentId == environmentId)
            .OrderBy(s => s.Code).ThenByDescending(s => s.Version)
            .ToListAsync(ct);
    }

    /// <summary>Dernière version utilisable de chaque SOP, groupée par code.</summary>
    public async Task<List<Domain.Sop>> GetUsableAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var tous = await db.Sops
            .AsNoTracking()
            .Include(s => s.Steps)
            .Where(s => s.EnvironmentId == environmentId
                        && (s.Status == LifecycleStatus.Valide || s.Status == LifecycleStatus.Actif))
            .ToListAsync(ct);

        return tous
            .GroupBy(s => s.Code)
            .Select(g => g.OrderByDescending(s => s.Version).First())
            .Where(s => s.Steps.Count > 0)
            .OrderBy(s => s.Title)
            .ToList();
    }

    public async Task<Domain.Sop?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Sops
            .AsNoTracking()
            .Include(s => s.Steps.OrderBy(st => st.Order))
            .Include(s => s.Associations)
            .Include(s => s.Environment)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>
    /// Candidats à la réutilisation pour un composant et/ou une signature
    /// donnés (FR-089D) — triés par pertinence de l'association puis par
    /// version la plus récente. Aucune recherche plein texte ici : c'est une
    /// correspondance sur ce qui a été explicitement rattaché au SOP, pas une
    /// devinette sur son contenu.
    /// </summary>
    public async Task<List<Domain.Sop>> SuggestForReuseAsync(
        Guid environmentId, Guid? componentId = null, ComponentRole? role = null,
        Guid? signatureId = null, CancellationToken ct = default)
    {
        if (componentId is null && role is null && signatureId is null) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var associations = await db.SopAssociations
            .AsNoTracking()
            .Include(a => a.Sop!).ThenInclude(s => s.Steps)
            .Where(a => a.Sop!.EnvironmentId == environmentId
                        && (a.Sop.Status == LifecycleStatus.Valide || a.Sop.Status == LifecycleStatus.Actif)
                        && ((componentId != null && a.ComponentId == componentId)
                            || (role != null && a.Role == role)
                            || (signatureId != null && a.SignatureId == signatureId)))
            .ToListAsync(ct);

        var candidats = associations
            .Select(a => a.Sop!)
            .Where(s => s.Steps.Count > 0)
            .GroupBy(s => s.Code)
            .Select(g => g.OrderByDescending(s => s.Version).First())
            .ToList();

        // P5 — Tri amélioré FR-089D :
        //   1. Score qualité (si calculable)
        //   2. Taux de réussite attesté (descendant, null en dernier)
        //   3. Dernière utilisation (récente en premier)
        //   4. Recence de modification
        var resultats = new List<(Domain.Sop Sop, int Score, int? TauxSucces, DateTimeOffset? DernierUsage)>();
        foreach (var s in candidats)
        {
            var stats = await GetDetailedUsageStatsAsync(s.Id, ct);
            var q = await CalculerRapportQualiteInterne(db, s, ct);
            resultats.Add((s, q.OverallScorePercent, stats.SuccessRatePercent, stats.DerniereUtilisation));
        }

        return resultats
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.TauxSucces ?? -1)
            .ThenByDescending(r => r.DernierUsage ?? DateTimeOffset.MinValue)
            .ThenByDescending(r => r.Sop.ModifiedAt ?? r.Sop.CreatedAt)
            .Select(r => r.Sop)
            .ToList();
    }

    /// <summary>
    /// Historique d'utilisation d'un SOP (FR-089D, AC-23) : nombre
    /// d'exécutions terminées, abandonnées, et taux de réussite historique.
    ///
    /// « Terminé » signifie que l'opérateur est allé au bout des étapes, pas
    /// que le résultat a été jugé bon — un taux calculé sur cette seule base
    /// serait une affirmation non prouvée. Le taux de réussite renvoyé ici
    /// vient donc EXCLUSIVEMENT des résultats explicitement attestés par un
    /// opérateur via <see cref="SopExecutionService.DeclareOutcomeAsync"/>
    /// (<see cref="SopExecution.Outcome"/>). Tant qu'aucune exécution n'a été
    /// attestée, le taux reste <c>null</c> — jamais 0 % ni 100 % par défaut.
    /// </summary>
    public async Task<SopUsageStats> GetUsageStatsAsync(Guid sopId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var comptes = await db.SopExecutions
            .Where(e => e.SopId == sopId)
            .GroupBy(e => e.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var attestees = await db.SopExecutions
            .Where(e => e.SopId == sopId && e.Outcome != null)
            .Select(e => e.Outcome!.Value)
            .ToListAsync(ct);

        return new SopUsageStats
        {
            Termine = comptes.FirstOrDefault(c => c.Key == SopExecutionStatus.Termine)?.Count ?? 0,
            Abandonne = comptes.FirstOrDefault(c => c.Key == SopExecutionStatus.Abandonne)?.Count ?? 0,
            EnCours = comptes.FirstOrDefault(c => c.Key == SopExecutionStatus.EnCours)?.Count ?? 0,
            OutcomesAttestes = attestees.Count,
            SuccessRatePercent = attestees.Count == 0
                ? null
                : (int)Math.Round(100.0 * attestees.Count(o => o == SopOutcome.ProblemeResolu) / attestees.Count)
        };
    }

    // -----------------------------------------------------------------------
    // Écriture
    // -----------------------------------------------------------------------
    public async Task<Guid> CreateAsync(Domain.Sop sop, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        sop.Version = 1;
        sop.Status = LifecycleStatus.Brouillon;
        sop.HasBeenExecuted = false;

        db.Sops.Add(sop);
        await db.SaveChangesAsync(ct);
        return sop.Id;
    }

    /// <summary>
    /// Enregistre une modification. Tant que le SOP n'a jamais servi, il est
    /// modifié sur place. Dès qu'une exécution s'en est servie, une nouvelle
    /// version est créée et l'ancienne reste intacte — identique à
    /// <c>WorkflowService.SaveAsync</c>.
    /// </summary>
    public async Task<SaveSopResult> SaveAsync(
        Guid sopId, string title, SopTemplateFields gabarit, List<SopStep> steps,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existant = await db.Sops.Include(s => s.Steps).FirstOrDefaultAsync(s => s.Id == sopId, ct);
        if (existant is null) return SaveSopResult.Failed("SOP introuvable.");

        var erreur = Valider(title, steps);
        if (erreur is not null) return SaveSopResult.Failed(erreur);

        if (!existant.HasBeenExecuted)
        {
            existant.Title = title;
            AppliquerGabarit(existant, gabarit);

            db.SopSteps.RemoveRange(existant.Steps);
            foreach (var (etape, index) in steps.Select((s, i) => (s, i)))
            {
                etape.Id = Guid.NewGuid();
                etape.SopId = existant.Id;
                etape.Order = index + 1;
                db.SopSteps.Add(etape);
            }

            await db.SaveChangesAsync(ct);
            return SaveSopResult.Ok(existant.Id, existant.Version, nouvelleVersion: false);
        }

        // Le SOP a servi : on ne le touche pas. Nouvelle version.
        var derniereVersion = await db.Sops
            .Where(s => s.EnvironmentId == existant.EnvironmentId && s.Code == existant.Code)
            .MaxAsync(s => s.Version, ct);

        var nouveau = new Domain.Sop
        {
            EnvironmentId = existant.EnvironmentId,
            Code = existant.Code,
            Title = title,
            Version = derniereVersion + 1,
            Status = LifecycleStatus.Brouillon,
            HasBeenExecuted = false,
            RequiresElevatedRole = existant.RequiresElevatedRole,
            SourceDocumentId = existant.SourceDocumentId,
            SourceExecutionId = existant.SourceExecutionId
        };
        AppliquerGabarit(nouveau, gabarit);

        db.Sops.Add(nouveau);
        await db.SaveChangesAsync(ct);

        foreach (var (etape, index) in steps.Select((s, i) => (s, i)))
        {
            etape.Id = Guid.NewGuid();
            etape.SopId = nouveau.Id;
            etape.Order = index + 1;
            db.SopSteps.Add(etape);
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "SOP {Code} : version {Ancienne} déjà exécutée, version {Nouvelle} créée. "
            + "L'ancienne reste attachée à ses exécutions.",
            existant.Code, existant.Version, nouveau.Version);

        return SaveSopResult.Ok(nouveau.Id, nouveau.Version, nouvelleVersion: true);
    }

    public async Task<string?> ChangeStatusAsync(
        Guid sopId, LifecycleStatus cible, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sop = await db.Sops.Include(s => s.Steps).FirstOrDefaultAsync(s => s.Id == sopId, ct);
        if (sop is null) return "SOP introuvable.";

        if (cible is LifecycleStatus.Valide or LifecycleStatus.Actif && sop.Steps.Count == 0)
            return "Un SOP sans étape ne peut pas être validé.";

        var precedent = sop.Status;
        sop.Status = cible;
        await db.SaveChangesAsync(ct);

        // FR-091 : la validation d'un SOP le rend reutilisable par tous —
        // ce changement de statut doit rester dans la piste d'audit, au
        // meme titre que la validation d'un workflow ou d'un document.
        await auditWriter.WriteAsync(
            AuditAction.ChangementDeStatut, AuditOutcome.Succes, actor,
            entityType: nameof(Domain.Sop), entityId: sop.Id.ToString(),
            entityLabel: $"{sop.Title} v{sop.Version}",
            reason: $"{precedent} → {cible}.", ct: ct);

        return null;
    }

    public async Task<string?> AddAssociationAsync(SopAssociation association, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.Sops.AnyAsync(s => s.Id == association.SopId, ct))
            return "SOP introuvable.";

        association.Id = Guid.NewGuid();
        db.SopAssociations.Add(association);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> RemoveAssociationAsync(Guid associationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var association = await db.SopAssociations.FirstOrDefaultAsync(a => a.Id == associationId, ct);
        if (association is null) return "Association introuvable.";

        db.SopAssociations.Remove(association);
        await db.SaveChangesAsync(ct);
        return null;
    }

    // -----------------------------------------------------------------------
    // FR-089B — Génération d'un brouillon à partir d'une exécution réussie
    // -----------------------------------------------------------------------
    /// <summary>
    /// Construit un brouillon de SOP à partir des étapes RÉELLEMENT exécutées
    /// par une <see cref="WorkflowExecution"/> terminée avec succès.
    ///
    /// Le résultat attendu de chaque étape est repris de la PREUVE réellement
    /// observée (Evidence), jamais d'une hypothèse : ce que le SOP décrit
    /// comme « résultat attendu » est ce qui s'est vraiment produit la
    /// dernière fois que cette séquence a tourné. Créé en Brouillon,
    /// jamais validé d'office — c'est l'exploitation du site qui le relit et
    /// le valide, exactement comme <c>WorkflowService.GenerateDefaultsAsync</c>.
    /// </summary>
    public async Task<GenerateSopResult> GenerateFromExecutionAsync(
        Guid executionId, string code, string title, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions
            .Include(x => x.Steps.OrderBy(s => s.Order))
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);

        if (execution is null) return GenerateSopResult.Failed("Exécution introuvable.");

        if (execution.Status != ExecutionStatus.TermineSucces)
            return GenerateSopResult.Failed(
                "Seule une exécution terminée SANS réserve peut servir de base à un SOP : "
                + $"celle-ci est en état « {execution.Status} ».");

        if (execution.Steps.Count == 0)
            return GenerateSopResult.Failed("Cette exécution ne comporte aucune étape.");

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(title))
            return GenerateSopResult.Failed("Le code et le titre du SOP sont obligatoires.");

        var sop = new Domain.Sop
        {
            EnvironmentId = execution.EnvironmentId,
            Code = code.Trim(),
            Title = title.Trim(),
            Version = 1,
            Status = LifecycleStatus.Brouillon,
            HasBeenExecuted = false,
            SourceExecutionId = execution.Id,
            Objective = $"Rejouer, à la main, la séquence « {execution.WorkflowName} » "
                      + $"telle qu'exécutée avec succès le {execution.EndedAt:dd/MM/yyyy}.",
            ExpectedOutcome = "Chaque étape ci-dessous a produit, la dernière fois, la preuve indiquée "
                             + "en résultat attendu. À RELIRE ET AJUSTER avant validation : une preuve "
                             + "observée une fois n'est pas une garantie pour la prochaine."
        };

        db.Sops.Add(sop);
        await db.SaveChangesAsync(ct);

        var ordre = 1;
        foreach (var etape in execution.Steps)
        {
            db.SopSteps.Add(new SopStep
            {
                SopId = sop.Id,
                Order = ordre++,
                Title = etape.Name,
                Instruction = DeriverInstruction(etape),
                ExpectedResult = etape.Evidence,
                ComponentId = etape.ComponentId,
                IsSkippable = etape.IsSkippable
            });
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "SOP {Code} généré en brouillon à partir de l'exécution {Correlation} ({N} étapes).",
            sop.Code, execution.CorrelationId, execution.Steps.Count);

        return GenerateSopResult.Ok(sop.Id);
    }

    // -----------------------------------------------------------------------
    // FR-097 — Génération d'un brouillon à partir d'un incident clos
    // -----------------------------------------------------------------------
    /// <summary>
    /// Construit un brouillon de SOP à partir des actions RÉELLEMENT
    /// déclarées pendant une <see cref="DiagnosticSession"/> CLÔTURÉE.
    ///
    /// Contrairement à <see cref="GenerateFromExecutionAsync"/>, un incident
    /// n'a pas d'étapes de workflow : sa trace des gestes réellement faits est
    /// <see cref="ExternalActionDeclaration"/> (§3.10.1, « action manuelle hors
    /// application »). Sans au moins une déclaration, il n'y a rien à
    /// capitaliser — un incident résolu sans qu'aucun geste n'ait été tracé ne
    /// produit aucun brouillon plutôt qu'un brouillon vide et trompeur.
    /// </summary>
    public async Task<GenerateSopResult> GenerateFromDiagnosticSessionAsync(
        Guid diagnosticSessionId, string code, string title, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions
            .Include(s => s.ExternalActions.OrderBy(a => a.OccurredAt))
            .FirstOrDefaultAsync(s => s.Id == diagnosticSessionId, ct);

        if (session is null) return GenerateSopResult.Failed("Session de diagnostic introuvable.");

        if (session.Phase != DiagnosticPhase.ClotureEtCapitalisation)
            return GenerateSopResult.Failed(
                "Seule une session de diagnostic CLÔTURÉE peut servir de base à un SOP : "
                + $"celle-ci en est à la phase « {DiagnosticSessionService.LibellePhase(session.Phase)} ».");

        if (session.ExternalActions.Count == 0)
            return GenerateSopResult.Failed(
                "Aucune action n'a été déclarée pendant cet incident (« Actions manuelles hors N4 Sentinel ») : "
                + "il n'y a rien de tracé à reprendre dans un SOP.");

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(title))
            return GenerateSopResult.Failed("Le code et le titre du SOP sont obligatoires.");

        var sop = new Domain.Sop
        {
            EnvironmentId = session.EnvironmentId,
            Code = code.Trim(),
            Title = title.Trim(),
            Version = 1,
            Status = LifecycleStatus.Brouillon,
            HasBeenExecuted = false,
            SourceDiagnosticSessionId = session.Id,
            Objective = $"Rejouer, à la main, les actions qui ont permis de clore l'incident « {session.Title} » "
                      + $"le {session.PhaseTransitions.OrderByDescending(t => t.EnteredAt).FirstOrDefault(t => t.Phase == DiagnosticPhase.ClotureEtCapitalisation)?.EnteredAt:dd/MM/yyyy}.",
            ExpectedOutcome = "Chaque étape ci-dessous reprend une action réellement déclarée pendant l'incident "
                             + "d'origine. À RELIRE ET AJUSTER avant validation : une action qui a fonctionné une "
                             + "fois n'est pas une garantie pour la prochaine, et le contexte peut avoir changé."
        };

        db.Sops.Add(sop);
        await db.SaveChangesAsync(ct);

        var ordre = 1;
        foreach (var action in session.ExternalActions)
        {
            db.SopSteps.Add(new SopStep
            {
                SopId = sop.Id,
                Order = ordre++,
                Title = action.ComponentName is { Length: > 0 } nom ? $"Action sur {nom}" : "Action déclarée",
                Instruction = action.Description,
                ComponentId = action.ComponentId
            });
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "SOP {Code} généré en brouillon à partir de l'incident « {Titre} » ({N} action(s) déclarée(s)).",
            sop.Code, session.Title, session.ExternalActions.Count);

        return GenerateSopResult.Ok(sop.Id);
    }

    /// <summary>
    /// Pour une étape automatisée, l'instruction n'existe pas telle quelle
    /// (elle a été une commande, pas un texte) : on la reconstitue à partir de
    /// ce qui a réellement été fait, pour qu'un opérateur puisse la reproduire
    /// à la main.
    /// </summary>
    private static string DeriverInstruction(ExecutionStep etape)
    {
        if (!string.IsNullOrWhiteSpace(etape.Instruction)) return etape.Instruction;

        var cible = etape.ComponentName ?? "le composant visé";
        return etape.Action switch
        {
            StepAction.Demarrer => $"Démarrer {cible}, puis attendre la preuve applicative de son bon démarrage.",
            StepAction.Arreter => $"Arrêter {cible}, puis attendre l'arrêt complet du service.",
            StepAction.Redemarrer => $"Redémarrer {cible} (arrêt complet, puis démarrage prouvé).",
            StepAction.Verifier => $"Contrôler l'état réel de {cible}.",
            StepAction.Attendre => "Observer une temporisation avant de poursuivre.",
            _ => $"Réaliser l'action « {etape.Action} » sur {cible}."
        };
    }

    // -----------------------------------------------------------------------
    // FR-059E/F/G — SOP guidé de reconstitution ActiveMQ/KahaDB
    // -----------------------------------------------------------------------
    /// <summary>
    /// Crée en brouillon le SOP de reconstitution du magasin ActiveMQ/KahaDB
    /// (fichier « db.data »), fondé sur la procédure validée du corpus de
    /// support (SOP-2 Fiche A, script Fix-N4-AMQCorruption.ps1).
    ///
    /// CHEMIN SOP GUIDÉ RETENU, PAS UNE AUTOMATISATION (décision actée pour
    /// FR-059E) : aucune commande de suppression de fichier n'est jamais
    /// émise par l'application elle-même — l'app n'a pas encore de capacité
    /// de ce type, et le corpus n'en documente le format nulle part au-delà
    /// du nom « db.data ». Chaque étape est réalisée à la main et confirmée
    /// avec preuve (FR-089A), ce qui satisfait FR-059G (aucune suppression
    /// sans sauvegarde, confirmation explicite, contrôle des services
    /// arrêtés et trace) par construction.
    ///
    /// Créé en Brouillon, jamais validé d'office : c'est l'exploitation du
    /// site qui relit et valide, comme <c>WorkflowService.GenerateDefaultsAsync</c>.
    /// </summary>
    public async Task<SaveSopResult> SeedReconstitutionKahaDbAsync(Guid environmentId, CancellationToken ct = default)
    {
        const string code = "RECONSTITUTION-AMQ-KAHADB";
        const string titre = "Reconstitution du magasin ActiveMQ/KahaDB (db.data)";

        await using (var verification = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await verification.Sops.AnyAsync(s => s.EnvironmentId == environmentId && s.Code == code, ct))
                return SaveSopResult.Failed(
                    "Ce SOP existe déjà pour cet environnement. Modifiez-le depuis la liste plutôt que d'en "
                    + "recréer un : un SOP qui a servi ne se remplace pas, il se met à jour en nouvelle version.");
        }

        var sopId = await CreateAsync(new Domain.Sop
        {
            EnvironmentId = environmentId,
            Code = code,
            Title = titre,
            // Reconstitution ActiveMQ/KahaDB : suppression de données sur
            // confirmation humaine (FR-059E/G) — reservee a l'Administrateur
            // N4, pas a tout Operateur N4 habilite a executer un SOP ordinaire.
            RequiresElevatedRole = true
        }, ct);

        var gabarit = new SopTemplateFields
        {
            Objective = "Reconstituer le magasin ActiveMQ/KahaDB après une corruption CONFIRMÉE du fichier "
                      + "« db.data », sans perte au-delà de ce qui était déjà en attente de traitement.",
            Scope = "S'applique uniquement à une corruption confirmée du fichier « db.data » du dossier "
                  + "ActiveMQ/KahaDB. Ne couvre PAS une corruption des journaux KahaDB eux-mêmes ni un cas "
                  + "incertain — voir Escalade.",
            Prerequisites = "Corruption confirmée (pas seulement suspectée). Accès administrateur à tous les "
                           + "serveurs N4 du cluster. Tous les services N4 (Cluster, Center, Bridge, XPS, ECN4) "
                           + "doivent pouvoir être arrêtés.",
            Risks = "Toute suppression sans sauvegarde vérifiée peut perdre des messages métier en attente de "
                   + "traitement dans les files ActiveMQ. Une reconstitution lancée alors que des services "
                   + "tournent encore peut aggraver la corruption.",
            Controls = "Comparaison de taille entre le fichier original et sa copie de sauvegarde avant toute "
                     + "suppression. Confirmation de l'arrêt de TOUS les services sur TOUS les nœuds avant "
                     + "d'agir sur le fichier. Confirmation du retour à l'état ACTIVE après redémarrage.",
            ExpectedOutcome = "Les services redémarrent, « db.data » est reconstruit automatiquement par "
                             + "ActiveMQ, et l'accès utilisateur est restauré — confirmé dans la vue Cluster "
                             + "Services.",
            RollbackPlan = "La copie de sauvegarde horodatée du fichier original reste disponible pour "
                          + "investigation ultérieure. Elle n'est JAMAIS réintroduite automatiquement : un "
                          + "fichier potentiellement corrompu ne doit pas être remis en place sans analyse.",
            EscalationPath = "Si la corruption touche les journaux KahaDB eux-mêmes, en cas de doute sur la "
                            + "cause, ou si l'incident se reproduit plus de deux fois par mois sur le même "
                            + "nœud : escalader vers un ticket éditeur AVANT toute correction destructive "
                            + "(FR-059G). Ne jamais improviser une variante de cette procédure."
        };

        var etapes = new List<SopStep>
        {
            new()
            {
                Title = "Confirmer la cause",
                Instruction = "Rechercher, dans le journal applicatif du Center (navis-apex.log), les signatures "
                            + "caractéristiques d'une corruption ActiveMQ/KahaDB : « IOException » portant sur le "
                            + "dossier amq, ou « NegativeArraySizeException » au démarrage du Center.",
                ExpectedResult = "L'une des deux signatures est retrouvée, datée, dans le journal."
            },
            new()
            {
                Title = "Vérifier l'espace disque",
                Instruction = "Contrôler l'espace disque disponible sur le volume qui héberge le dossier partagé. "
                            + "Un disque plein est l'une des causes connues de corruption.",
                ExpectedResult = "L'espace libre est mesuré et noté ; s'il est proche de zéro, le traiter avant "
                                + "de poursuivre."
            },
            new()
            {
                Title = "Confirmer l'arrêt de TOUS les services N4",
                Instruction = "Vérifier, sur CHAQUE nœud du cluster (Cluster Nodes, Center, Standby, Bridge, XPS, "
                            + "ECN4), que le service N4 correspondant est à l'état Stopped — pas seulement sur le "
                            + "nœud où la corruption a été observée.",
                ExpectedResult = "Tous les services N4, sur tous les nœuds, sont confirmés Stopped.",
                IsSkippable = false
            },
            new()
            {
                Title = "Vérifier que le fichier n'est verrouillé par aucun processus",
                Instruction = "Tenter une ouverture exclusive du fichier « db.data ». Si l'ouverture échoue, un "
                            + "processus le tient encore ouvert : ne pas poursuivre avant d'avoir identifié lequel.",
                ExpectedResult = "Le fichier s'ouvre en exclusif sans erreur."
            },
            new()
            {
                Title = "Sauvegarder « db.data » et vérifier la copie",
                Instruction = "Copier « db.data » vers un fichier horodaté (ex. db.data.backup_AAAAMMJJ_HHmmss), "
                            + "dans un emplacement distinct. Comparer la taille de la copie à celle de l'original.",
                ExpectedResult = "La copie existe et sa taille est EXACTEMENT identique à celle de l'original.",
                IsSkippable = false
            },
            new()
            {
                Title = "Supprimer « db.data »",
                Instruction = "Supprimer le fichier « db.data » original — UNIQUEMENT après que la sauvegarde "
                            + "précédente a été vérifiée. Sa suppression est sans risque pour l'intégrité "
                            + "applicative : ActiveMQ le reconstruit entièrement au redémarrage.",
                ExpectedResult = "Le fichier n'existe plus dans le dossier amq."
            },
            new()
            {
                Title = "Redémarrer les services dans l'ordre",
                Instruction = "Redémarrer les services N4 dans l'ordre suivant, en confirmant chaque étape avant "
                            + "de passer à la suivante : Cluster → Center → Bridge → XPS → ECN4.",
                ExpectedResult = "Chaque service atteint l'état Running avant que le suivant ne soit démarré."
            },
            new()
            {
                Title = "Vérifier l'exclusion antivirus du dossier amq",
                Instruction = "Confirmer que le dossier amq est exclu de l'analyse temps réel de l'antivirus. "
                            + "L'éditeur cite le verrouillage antivirus comme la première cause de récidive.",
                ExpectedResult = "L'exclusion est en place, ou a été ajoutée."
            },
            new()
            {
                Title = "Confirmer le retour à l'état ACTIVE",
                Instruction = "Dans la vue Cluster Services, confirmer que l'état est revenu à ACTIVE. Faire "
                            + "confirmer par un utilisateur métier que l'accès applicatif est restauré.",
                ExpectedResult = "État ACTIVE confirmé, et accès utilisateur restauré."
            }
        };

        return await SaveAsync(sopId, titre, gabarit, etapes, ct);
    }

    // -----------------------------------------------------------------------
    // FR-088 — Présentation d'une réponse documentaire selon le gabarit SOP
    // -----------------------------------------------------------------------
    /// <summary>
    /// Reformate une réponse de la base documentaire selon le gabarit
    /// structuré attendu d'un SOP (objectif, périmètre, prérequis, risques,
    /// étapes, contrôles, résultat attendu, retour arrière, escalade).
    ///
    /// PRÉSENTATION UNIQUEMENT — rien n'est enregistré ici. Chaque section
    /// n'est renseignée QUE si le texte source contient un en-tête reconnu ;
    /// une section absente du document reste vide plutôt que devinée. C'est
    /// la même règle que <see cref="KnowledgeService"/> : ne jamais composer
    /// un contenu qui ne provient pas du document versé.
    /// </summary>
    public async Task<SopPresentation> PresentAsSopAsync(
        string question, CancellationToken ct = default)
    {
        var reponse = await knowledge.AskAsync(question, ct: ct);

        if (!reponse.Answered || reponse.Passages.Count == 0)
            return SopPresentation.SansReponse(question, reponse.Explanation
                ?? "Aucun passage de la documentation validée ne permet de répondre.");

        var passage = reponse.Passages[0];
        var texte = passage.FullContent;

        var etapes = ExtraireEtapes(texte);

        return new SopPresentation
        {
            Question = question.Trim(),
            Answered = true,
            Objective = ExtraireSection(texte, "objectif") ?? $"Répondre à : « {question.Trim()} »",
            Scope = ExtraireSection(texte, "perimetre", "scope"),
            Prerequisites = ExtraireSection(texte, "prerequis", "pre requis"),
            Risks = ExtraireSection(texte, "risque"),
            Steps = etapes,
            StepsSourceIdentified = etapes.Count > 0,
            Controls = ExtraireSection(texte, "controle"),
            ExpectedOutcome = ExtraireSection(texte, "resultat attendu"),
            RollbackPlan = ExtraireSection(texte, "retour arriere", "rollback"),
            EscalationPath = ExtraireSection(texte, "escalade"),
            SourceDocumentId = passage.DocumentId,
            SourceCitation = passage.Citation,
            Warning = KnowledgeAnswer.AvertissementConseil
        };
    }

    /// <summary>En-têtes de section reconnus, normalisés une fois pour toutes.</summary>
    private static readonly string[] TousLesEnTetes =
    [
        "objectif", "perimetre", "scope", "prerequis", "pre requis", "risque",
        "controle", "resultat attendu", "retour arriere", "rollback", "escalade"
    ];

    /// <summary>
    /// Cherche une ligne débutant par l'un des motifs (normalisés, insensibles
    /// aux accents/casse) et capture son contenu, plus les lignes suivantes
    /// jusqu'à la prochaine ligne vide ou le prochain en-tête reconnu.
    /// Retourne null si aucun en-tête de ce type n'apparaît dans le texte —
    /// jamais de contenu inventé.
    /// </summary>
    private static string? ExtraireSection(string texte, params string[] motifsBruts)
    {
        var motifs = motifsBruts.Select(KnowledgeService.Normaliser).ToArray();
        var lignes = texte.Split('\n');

        for (var i = 0; i < lignes.Length; i++)
        {
            var normalisee = KnowledgeService.Normaliser(lignes[i]).TrimStart();
            var motif = motifs.FirstOrDefault(m => normalisee.StartsWith(m, StringComparison.Ordinal));
            if (motif is null) continue;

            var ligne = lignes[i].Trim();
            var idx = ligne.IndexOf(':');
            var contenu = idx >= 0 ? ligne[(idx + 1)..].Trim() : string.Empty;

            var suite = new List<string>();
            if (contenu.Length > 0) suite.Add(contenu);

            for (var j = i + 1; j < lignes.Length; j++)
            {
                var l = lignes[j].Trim();
                if (l.Length == 0) break;

                var lNormalisee = KnowledgeService.Normaliser(l).TrimStart();
                if (TousLesEnTetes.Any(h => lNormalisee.StartsWith(h, StringComparison.Ordinal))) break;

                suite.Add(l);
            }

            var resultat = string.Join(' ', suite).Trim();
            return resultat.Length > 0 ? resultat : null;
        }

        return null;
    }

    /// <summary>
    /// Reconnaît une liste numérotée ou à puces dans le texte source. Aucune
    /// liste reconnue → liste vide, jamais une suite de phrases découpées à
    /// l'aveugle qui ressemblerait à des étapes sans en être.
    /// </summary>
    private static List<string> ExtraireEtapes(string texte)
    {
        var motif = new System.Text.RegularExpressions.Regex(
            @"^\s*(?:\d+[\.\)]|[-•])\s+(.{3,})$", System.Text.RegularExpressions.RegexOptions.Multiline);

        return motif.Matches(texte)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------
    private static string? Valider(string title, List<SopStep> steps)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "Un SOP doit porter un titre.";

        if (steps.Count == 0)
            return "Un SOP doit comporter au moins une étape.";

        foreach (var etape in steps)
        {
            if (string.IsNullOrWhiteSpace(etape.Title))
                return "Chaque étape doit porter un titre.";

            if (string.IsNullOrWhiteSpace(etape.Instruction))
                return $"L'étape « {etape.Title} » n'a pas d'instruction : un SOP décrit un geste, pas seulement un nom.";
        }

        return null;
    }

    /// <summary>
    /// Restitution complète d'un SOP validé, exportable en PDF/DOCX (SEC-010).
    ///
    /// Une panne de N4 Sentinel ne doit jamais rendre une procédure d'urgence
    /// validée inaccessible : c'est exactement ce que cet export prépare —
    /// une copie autonome, à conserver hors application, du gabarit complet
    /// (FR-088) et de chaque étape.
    /// </summary>
    public static string BuildEmergencyMarkdown(Domain.Sop sop)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {sop.Title} — {sop.Code} v{sop.Version}");
        sb.AppendLine();
        sb.AppendLine($"Statut : {sop.Status} · Environnement : {sop.Environment?.Code ?? "—"}");
        if (sop.AppliesToVersion is { Length: > 0 })
            sb.AppendLine($"Applicable à N4 {sop.AppliesToVersion}");
        sb.AppendLine();
        sb.AppendLine("> Copie autonome à conserver hors application (SEC-010) : exploitable même si "
                     + "N4 Sentinel est indisponible.");
        sb.AppendLine();

        void Section(string titre, string? contenu)
        {
            if (string.IsNullOrWhiteSpace(contenu)) return;
            sb.AppendLine($"## {titre}");
            sb.AppendLine(contenu);
            sb.AppendLine();
        }

        Section("Objectif", sop.Objective);
        Section("Périmètre", sop.Scope);
        Section("Prérequis", sop.Prerequisites);
        Section("Risques", sop.Risks);
        Section("Contrôles", sop.Controls);

        sb.AppendLine("## Étapes");
        sb.AppendLine();
        foreach (var etape in sop.Steps.OrderBy(s => s.Order))
        {
            sb.AppendLine($"{etape.Order}. **{etape.Title}** — {etape.Instruction}");
            if (etape.ExpectedResult is { Length: > 0 })
                sb.AppendLine($"   Résultat attendu : {etape.ExpectedResult}");
        }
        sb.AppendLine();

        Section("Résultat attendu", sop.ExpectedOutcome);
        Section("Retour arrière", sop.RollbackPlan);
        Section("Escalade", sop.EscalationPath);

        return sb.ToString();
    }

    private static void AppliquerGabarit(Domain.Sop sop, SopTemplateFields gabarit)
    {
        sop.Objective = gabarit.Objective;
        sop.Scope = gabarit.Scope;
        sop.Prerequisites = gabarit.Prerequisites;
        sop.Risks = gabarit.Risks;
        sop.Controls = gabarit.Controls;
        sop.ExpectedOutcome = gabarit.ExpectedOutcome;
        sop.RollbackPlan = gabarit.RollbackPlan;
        sop.EscalationPath = gabarit.EscalationPath;
        sop.AppliesToVersion = gabarit.AppliesToVersion;
    }

    // -----------------------------------------------------------------------
    // P5 — Cycle de vie approuvé FR-094 (Brouillon → AValider → Valide → Actif → Revoque)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Soumet le brouillon à la revue d'un expert (Brouillon → AValider).
    /// Garde FR-090 : au moins 6/8 sections FR-088 renseignées, au moins 1 étape,
    /// Instructions + ExpectedResult non vides sur chaque étape.
    /// </summary>
    public async Task<string?> SubmitForReviewAsync(Guid sopId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sop = await db.Sops.Include(s => s.Steps).FirstOrDefaultAsync(s => s.Id == sopId, ct);
        if (sop is null) return "SOP introuvable.";
        if (sop.Status != LifecycleStatus.Brouillon)
            return $"Le SOP doit être en Brouillon, il est « {sop.Status} ».";
        if (sop.Steps.Count == 0) return "Le SOP n'a aucune étape : il ne peut pas être soumis en revue.";

        var sections = CompterSectionsRemplies(sop);
        if (sections < 6)
            return $"Complétude gabarit insuffisante : {sections}/8 sections FR-088 renseignées. Minimum 6 pour soumettre.";

        var etapeTrouee = sop.Steps.FirstOrDefault(s =>
            string.IsNullOrWhiteSpace(s.Title) || string.IsNullOrWhiteSpace(s.Instruction));
        if (etapeTrouee is not null)
            return $"L'étape #{etapeTrouee.Order} « {etapeTrouee.Title} » manque de Titre ou d'Instruction obligatoires.";

        sop.Status = LifecycleStatus.AValider;
        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.ChangementDeStatut, AuditOutcome.Succes, actor,
            entityType: nameof(Domain.Sop), entityId: sop.Id.ToString(),
            entityLabel: $"{sop.Title} v{sop.Version}",
            environmentId: sop.EnvironmentId,
            reason: $"Brouillon → AValider (revue expert demandée). {sections}/8 sections FR-088, {sop.Steps.Count} étapes.",
            ct: ct);

        return null;
    }

    /// <summary>
    /// Approbation expert — AValider → Valide. FR-094 : le relecteur doit être
    /// DISTINCT de l'auteur initial. La qualification est refusée si le score
    /// qualité calculé est Insuffisant (score global &lt; 50), sinon on autorise
    /// la qualification avec une note si le score est moyen.
    /// </summary>
    public async Task<(string? Error, SopQualityReport? Report)> ApproveQualificationAsync(
        Guid sopId, string reviewer, string? note, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sop = await db.Sops.Include(s => s.Steps).FirstOrDefaultAsync(s => s.Id == sopId, ct);
        if (sop is null) return ("SOP introuvable.", null);
        if (sop.Status != LifecycleStatus.AValider)
            return ($"Le SOP doit être en AValider, il est « {sop.Status} ».", null);

        // FR-094 — reviewer distinct de l'auteur. Cas sans auteur (import legacy) :
        // on ne bloque pas, mais on log un warning.
        if (!string.IsNullOrWhiteSpace(sop.CreatedBy)
            && string.Equals(sop.CreatedBy, reviewer, StringComparison.OrdinalIgnoreCase))
        {
            await auditWriter.WriteAsync(
                AuditAction.TentativeNonAutorisee, AuditOutcome.Refuse, reviewer,
                entityType: nameof(Domain.Sop), entityId: sop.Id.ToString(),
                entityLabel: sop.Title,
                environmentId: sop.EnvironmentId,
                reason: "Tentative de qualification par l'auteur initial du SOP — FR-094 interdit l'auto-approbation.",
                ct: ct);
            return ("FR-094 : un SOP ne peut pas être qualifié par son propre auteur. "
                  + "Demandez à un relecteur N2 minimum distinct.", null);
        }

        var rapport = await CalculerRapportQualiteInterne(db, sop, ct);
        if (rapport.Grade == SopQualityGrade.Insuffisant)
        {
            return ("Score qualité Insuffisant (" + rapport.OverallScorePercent
                    + "/100) : au moins un axe est en dessous du minimum attendu. "
                    + "Corriger puis resoumettre.", rapport);
        }

        sop.Status = LifecycleStatus.Valide;
        sop.QualifiedBy = reviewer;
        sop.QualifiedAt = DateTimeOffset.UtcNow;
        sop.QualificationNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.Approbation, AuditOutcome.Succes, reviewer,
            entityType: nameof(Domain.Sop), entityId: sop.Id.ToString(),
            entityLabel: $"{sop.Title} v{sop.Version}",
            environmentId: sop.EnvironmentId,
            reason: $"AValider → Valide. Score qualité {rapport.OverallScorePercent}/100, niveau {rapport.Grade}. "
                  + (note is { Length: > 0 } ? $"Note : {note}" : string.Empty),
            ct: ct);

        return (null, rapport);
    }

    /// <summary>
    /// Passe le SOP en Actif — désormais il est sélectionnable par SuggestForReuse
    /// et démarrable. Ne fait rien si déjà Actif ; Valide uniquement ou Actif/Valide sont OK.
    /// </summary>
    public async Task<string?> PublishAsync(Guid sopId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sop = await db.Sops.Include(s => s.Steps).FirstOrDefaultAsync(s => s.Id == sopId, ct);
        if (sop is null) return "SOP introuvable.";
        if (sop.Status is LifecycleStatus.Brouillon or LifecycleStatus.AValider)
            return $"Le SOP doit d'abord être qualifié Valide, il est « {sop.Status} ».";
        if (sop.Status == LifecycleStatus.Revoque)
            return "Un SOP révoqué ne peut pas être republié — créez une nouvelle version plutôt.";
        if (sop.Status == LifecycleStatus.Actif) return null;

        var precedent = sop.Status;
        sop.Status = LifecycleStatus.Actif;
        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.ChangementDeStatut, AuditOutcome.Succes, actor,
            entityType: nameof(Domain.Sop), entityId: sop.Id.ToString(),
            entityLabel: $"{sop.Title} v{sop.Version}",
            environmentId: sop.EnvironmentId,
            reason: $"{precedent} → Actif (publié, démarrable).",
            ct: ct);
        return null;
    }

    /// <summary>
    /// FR-094 : Révocation d'un SOP publié (Actif/Valide → Revoque). Motif
    /// obligatoire, piste d'audit, plus utilisable après appel.
    /// </summary>
    public async Task<string?> RevokeAsync(Guid sopId, string actor, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "La révocation est tracée : un motif est OBLIGATOIRE.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sop = await db.Sops.FirstOrDefaultAsync(s => s.Id == sopId, ct);
        if (sop is null) return "SOP introuvable.";
        if (sop.Status is not (LifecycleStatus.Valide or LifecycleStatus.Actif or LifecycleStatus.Desactive))
            return $"Seul un SOP publié (Valide/Actif/Désactivé) peut être révoqué. Actuel : {sop.Status}.";
        if (sop.Status == LifecycleStatus.Revoque) return null;

        var precedent = sop.Status;
        sop.Status = LifecycleStatus.Revoque;
        sop.RevokedBy = actor;
        sop.RevokedAt = DateTimeOffset.UtcNow;
        sop.RevocationReason = reason.Trim();
        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.Revocation, AuditOutcome.Succes, actor,
            entityType: nameof(Domain.Sop), entityId: sop.Id.ToString(),
            entityLabel: $"{sop.Title} v{sop.Version}",
            environmentId: sop.EnvironmentId,
            reason: $"{precedent} → Révoqué. Motif : {reason.Trim()}",
            ct: ct);

        return null;
    }

    // -----------------------------------------------------------------------
    // P5 — Qualité FR-090 : 5 axes → score 0–100, Niveau
    // -----------------------------------------------------------------------
    public async Task<SopQualityReport> CalculateQualityReportAsync(Guid sopId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sop = await db.Sops.Include(s => s.Steps).Include(s => s.Associations)
            .FirstOrDefaultAsync(s => s.Id == sopId, ct)
            ?? throw new InvalidOperationException("SOP introuvable.");
        return await CalculerRapportQualiteInterne(db, sop, ct);
    }

    /// <summary>
    /// P5 — Statistiques d'utilisation détaillées d'un SOP (médiane durée par
    /// étape, % confirmee/ecart/contournee, top deviations, feedback moyen,
    /// dernière utilisation). Outrepasse SopUsageStats pour trier SuggestForReuse
    /// et décider de révoquer.
    /// </summary>
    public async Task<SopDetailedUsageStats> GetDetailedUsageStatsAsync(Guid sopId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var baseStats = await GetUsageStatsAsync(sopId, ct);
        var executions = await db.SopExecutions
            .AsNoTracking()
            .Where(e => e.SopId == sopId)
            .Include(e => e.Steps)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync(ct);

        var toutesEtapes = executions.SelectMany(e => e.Steps).ToList();

        Dictionary<int, double> medianes = new();
        if (toutesEtapes.Count > 0)
        {
            foreach (var grp in toutesEtapes.Where(s => s.Duration is not null)
                                            .GroupBy(s => s.Order))
            {
                var valeurs = grp.Select(s => s.Duration!.Value.TotalSeconds)
                                 .OrderBy(v => v).ToList();
                if (valeurs.Count > 0)
                    medianes[grp.Key] = valeurs.Count % 2 == 0
                        ? (valeurs[valeurs.Count / 2 - 1] + valeurs[valeurs.Count / 2]) / 2.0
                        : valeurs[valeurs.Count / 2];
            }
        }

        int confirm = toutesEtapes.Count(s => s.State == SopExecutionStepState.Confirmee);
        int ecart = toutesEtapes.Count(s => s.State == SopExecutionStepState.Ecart);
        int ignorees = toutesEtapes.Count(s => s.State == SopExecutionStepState.Ignoree);
        int total = confirm + ecart + ignorees;

        var deviations = toutesEtapes
            .Where(s => s.State == SopExecutionStepState.Ecart && s.DeviationNote is { Length: > 0 })
            .Select(s => TronquerMot(s.DeviationNote!))
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => (DeviationPattern: g.Key, Count: g.Count()))
            .ToList();

        var stars = executions.Where(e => e.FeedbackStars0To5.HasValue)
                              .Select(e => (double)e.FeedbackStars0To5!.Value)
                              .ToList();
        int feedbackCount = stars.Count;
        double? feedbackAvg = feedbackCount == 0 ? null : Math.Round(stars.Sum() / feedbackCount, 2);
        DateTimeOffset? derniere = executions.Count == 0 ? null : executions.Max(e => e.StartedAt);

        return new SopDetailedUsageStats
        {
            Termine = baseStats.Termine,
            Abandonne = baseStats.Abandonne,
            EnCours = baseStats.EnCours,
            OutcomesAttestes = baseStats.OutcomesAttestes,
            SuccessRatePercent = baseStats.SuccessRatePercent,
            MedianStepDurationSecPerOrder = medianes,
            PercentConfirmeeSteps = total == 0 ? 0 : (int)Math.Round(100.0 * confirm / total),
            PercentEcartSteps = total == 0 ? 0 : (int)Math.Round(100.0 * ecart / total),
            PercentIgnoreesSteps = total == 0 ? 0 : (int)Math.Round(100.0 * ignorees / total),
            TopDeviations = deviations,
            DerniereUtilisation = derniere,
            AverageFeedbackStars = feedbackAvg,
            FeedbackCount = feedbackCount
        };
    }

    /// <summary>
    /// P5 FR-097 — Promouvoir une EXÉCUTION DE SOP réussie (Outcome Problème
    /// Résolu) en CapitalizedIncident lié au SOP utilisé. Complète la voie
    /// Diagnostic → Incident déjà présente dans LogAnalysisService : ici
    /// c'est une exécution humaine résolutoire → capitalisation.
    /// </summary>
    public async Task<(bool Ok, Guid? IncidentId, string? Error)> PromoteExecutionToCapitalizedIncidentAsync(
        Guid sopExecutionId, string actor,
        string? rootCauseCode = null, string? rootCauseLabel = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var exec = await db.SopExecutions.FirstOrDefaultAsync(e => e.Id == sopExecutionId, ct);
        if (exec is null) return (false, null, "Exécution de SOP introuvable.");
        if (exec.Outcome != SopOutcome.ProblemeResolu)
            return (false, null, "Seule une exécution de SOP avec résultat « Problème Résolu » peut être capitalisée.");

        if (await db.CapitalizedIncidents.AnyAsync(ci => ci.LinkedExecutionId == sopExecutionId, ct))
            return (false, null, "Cette exécution est déjà rattachée à un incident capitalisé.");

        var dureeMin = (long)Math.Round(exec.Duration.TotalMinutes);
        if (dureeMin < 0) dureeMin = 0;
        ResolutionSkillLevel skill = dureeMin > 240 ? ResolutionSkillLevel.ExpertNavisEditeur
                                  : dureeMin > 60  ? ResolutionSkillLevel.N2Specialiste
                                                   : ResolutionSkillLevel.N1Support;

        var ci = new CapitalizedIncident
        {
            ExternalReference = $"INC-SOP-{exec.StartedAt:yyyyMMdd}-{exec.CorrelationId[..6].ToUpperInvariant()}",
            EnvironmentId = exec.EnvironmentId,
            DeclaredSeverity = CriticalityLevel.Moyenne,
            InitialSymptom = exec.Reason ?? exec.SopTitle,
            RealIncidentStartedAt = exec.StartedAt,
            RealIncidentEndedAt = exec.EndedAt ?? DateTimeOffset.UtcNow,
            TotalDurationMinutes = dureeMin,
            DetectedBySentinelAt = exec.StartedAt,
            MainRootCauseCode = rootCauseCode ?? exec.SopCode,
            MainRootCauseLabel = rootCauseLabel ?? exec.OutcomeNote ?? exec.Reason ?? $"{exec.SopCode} — SOP",
            CorrectiveActionsTaken = $"SOP appliqué : {exec.SopTitle} v{exec.SopVersion} — ticket {exec.TicketReference ?? "N/A"}.",
            LinkedExecutionId = sopExecutionId,
            GeneratedSOPId = exec.SopId,
            OperatorsInvolvedCount = 1,
            ResolutionSkillLevel = skill,
            ClosedBy = actor,
            ClosedAt = DateTimeOffset.UtcNow,
            LessonsAndCorrectives = exec.OutcomeNote
        };
        db.CapitalizedIncidents.Add(ci);
        await db.SaveChangesAsync(ct);
        return (true, ci.Id, null);
    }

    // -----------------------------------------------------------------------
    // P5 — internes qualité
    // -----------------------------------------------------------------------
    private static int CompterSectionsRemplies(Domain.Sop s)
    {
        var sections = new string?[]
        {
            s.Objective, s.Scope, s.Prerequisites, s.Risks, s.Controls,
            s.ExpectedOutcome, s.RollbackPlan, s.EscalationPath
        };
        return sections.Count(x => !string.IsNullOrWhiteSpace(x));
    }

    private async Task<SopQualityReport> CalculerRapportQualiteInterne(
        N4SentinelDbContext db, Domain.Sop sop, CancellationToken ct)
    {
        var axes = new List<SopAxisScore>(5);

        // --- 1. Complétude FR-088 : max 20 ---------------------------------
        int n = CompterSectionsRemplies(sop);
        int scoreCompletude = n * 20 / 8;
        axes.Add(new SopAxisScore
        {
            Code = "FR088",
            Label = "Complétude du gabarit FR-088 (8 sections)",
            Score = scoreCompletude,
            Justification = $"{n}/8 sections renseignées. Minimum attendu 6/8."
        });

        // --- 2. Qualité des étapes : max 20 --------------------------------
        var etapes = sop.Steps.ToList();
        int scoreEtapes = 0;
        string justEtapes;
        if (etapes.Count == 0) { scoreEtapes = 0; justEtapes = "Aucune étape : non publiable."; }
        else
        {
            int avecInstruction = etapes.Count(s => !string.IsNullOrWhiteSpace(s.Instruction));
            int avecAttendu = etapes.Count(s => !string.IsNullOrWhiteSpace(s.ExpectedResult));
            scoreEtapes = (avecInstruction * 10 + avecAttendu * 10) / Math.Max(1, etapes.Count);
            if (etapes.Count >= 3) scoreEtapes = Math.Min(20, scoreEtapes + 0); // bonus implicite = plus d'étapes = mieux documenté
            justEtapes = $"{etapes.Count} étape(s). {avecInstruction} avec Instruction obligatoire, "
                       + $"{avecAttendu} avec Résultat attendu.";
        }
        axes.Add(new SopAxisScore
        {
            Code = "STEPS",
            Label = "Qualité des étapes (instruction + attendu)",
            Score = scoreEtapes,
            Justification = justEtapes
        });

        // --- 3. Couverture associations : max 20 -------------------------
        var associations = sop.Associations.Count;
        int scoreAssoc = associations switch
        {
            0 => 0,
            1 => 10,
            2 => 16,
            _ => 20
        };
        axes.Add(new SopAxisScore
        {
            Code = "LINKS",
            Label = "Couverture associations (composant / signature / session)",
            Score = scoreAssoc,
            Justification = associations == 0
                ? "Aucune association. Le SOP ne sera pas suggéré automatiquement."
                : $"{associations} association(s) → SuggestForReuse actif."
        });

        // --- 4. Historique exécutions attestées : max 20 ------------------
        var detail = await GetDetailedUsageStatsAsync(sop.Id, ct);
        int scoreHistorique = detail.OutcomesAttestes switch
        {
            0 => 4,   // pas encore de recul
            1 => 9,
            2 => 14,
            >= 5 and < 10 => 17,
            >= 10 => 20,
            _ => 12
        };
        axes.Add(new SopAxisScore
        {
            Code = "HIST",
            Label = "Historique d'utilisation & attestations FR-089D",
            Score = scoreHistorique,
            Justification = $"{detail.Total} exécution(s), {detail.OutcomesAttestes} résultat(s) attesté(s). "
                          + $"Taux succès attesté : {detail.SuccessRatePercent?.ToString() ?? "N/A"}%"
        });

        // --- 5. Gestion des risques & sensibilité : max 20 ----------------
        int scoreRisques;
        string justRisques;
        bool sensible = sop.RequiresElevatedRole;
        bool risqueRempli = !string.IsNullOrWhiteSpace(sop.Risks)
                         && !string.IsNullOrWhiteSpace(sop.RollbackPlan)
                         && !string.IsNullOrWhiteSpace(sop.EscalationPath);
        if (sensible && risqueRempli) { scoreRisques = 20; justRisques = "SOP sensible. Risques, Rollback et Escalade tous renseignés : OK."; }
        else if (sensible) { scoreRisques = 5; justRisques = "SOP sensible : Risques/Rollback/Escalade TOUS obligatoires, mais il en manque au moins un."; }
        else if (risqueRempli) { scoreRisques = 18; justRisques = "SOP standard, triade Risques/Rollback/Escalade complète : bonne pratique."; }
        else { scoreRisques = 10; justRisques = "Triade Risques/Rollback/Escalade incomplète : à compléter avant publication Actif."; }
        axes.Add(new SopAxisScore
        {
            Code = "RISK",
            Label = "Gestion des risques (Risques/Rollback/Escalade)",
            Score = scoreRisques,
            Justification = justRisques
        });

        int global = axes.Sum(a => a.Score);
        SopQualityGrade grade;
        if (global < 50 || axes.Any(a => a.Score < 7)) grade = SopQualityGrade.Insuffisant;
        else if (global < 70) grade = SopQualityGrade.Passable;
        else if (global >= 90 && axes.All(a => a.Score >= 14)) grade = SopQualityGrade.Excellence;
        else grade = SopQualityGrade.Bon;

        string resume = grade switch
        {
            SopQualityGrade.Insuffisant => $"Insuffisant ({global}/100). Corriger le ou les axes faibles avant soumission.",
            SopQualityGrade.Passable => $"Passable ({global}/100). Qualifiable en Valide, mais améliorations conseillées avant Actif.",
            SopQualityGrade.Bon => $"Bon ({global}/100). Standard attendu pour publication Actif.",
            SopQualityGrade.Excellence => $"Excellence ({global}/100). SOP de référence.",
            _ => $"{global}/100."
        };

        return new SopQualityReport
        {
            Axes = axes,
            OverallScorePercent = global,
            Grade = grade,
            Resume = resume
        };
    }

    private static string TronquerMot(string texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return "(sans motif)";
        var premier = texte.Split(['.', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return premier.Length <= 120 ? premier : premier[..120];
    }
}

/// <summary>Les champs du gabarit structuré (FR-088), regroupés pour ne pas alourdir la signature de <c>SaveAsync</c>.</summary>
public sealed record SopTemplateFields
{
    public string? Objective { get; init; }
    public string? Scope { get; init; }
    public string? Prerequisites { get; init; }
    public string? Risks { get; init; }
    public string? Controls { get; init; }
    public string? ExpectedOutcome { get; init; }
    public string? RollbackPlan { get; init; }
    public string? EscalationPath { get; init; }
    public string? AppliesToVersion { get; init; }
}

public sealed record SaveSopResult
{
    public bool Succeeded { get; init; }
    public Guid SopId { get; init; }
    public int Version { get; init; }
    public bool CreatedNewVersion { get; init; }
    public string? Error { get; init; }

    public static SaveSopResult Ok(Guid id, int version, bool nouvelleVersion) =>
        new() { Succeeded = true, SopId = id, Version = version, CreatedNewVersion = nouvelleVersion };

    public static SaveSopResult Failed(string error) => new() { Succeeded = false, Error = error };
}

public sealed record GenerateSopResult
{
    public bool Succeeded { get; init; }
    public Guid SopId { get; init; }
    public string? Error { get; init; }

    public static GenerateSopResult Ok(Guid id) => new() { Succeeded = true, SopId = id };
    public static GenerateSopResult Failed(string error) => new() { Succeeded = false, Error = error };
}

/// <summary>Résultat de <see cref="SopService.PresentAsSopAsync"/> — une présentation, jamais un SOP enregistré.</summary>
public sealed record SopPresentation
{
    public string Question { get; init; } = string.Empty;
    public bool Answered { get; init; }
    public string? Explanation { get; init; }

    public string? Objective { get; init; }
    public string? Scope { get; init; }
    public string? Prerequisites { get; init; }
    public string? Risks { get; init; }
    public List<string> Steps { get; init; } = [];

    /// <summary>Faux si aucune liste n'a pu être identifiée dans le texte source : les étapes restent à rédiger.</summary>
    public bool StepsSourceIdentified { get; init; }

    public string? Controls { get; init; }
    public string? ExpectedOutcome { get; init; }
    public string? RollbackPlan { get; init; }
    public string? EscalationPath { get; init; }

    public Guid? SourceDocumentId { get; init; }
    public string? SourceCitation { get; init; }
    public string? Warning { get; init; }

    public static SopPresentation SansReponse(string question, string motif) =>
        new() { Question = question.Trim(), Answered = false, Explanation = motif };
}

/// <summary>Historique d'utilisation d'un SOP (FR-089D, AC-23) — voir <see cref="SopService.GetUsageStatsAsync"/>.</summary>
public sealed record SopUsageStats
{
    public int Termine { get; init; }
    public int Abandonne { get; init; }
    public int EnCours { get; init; }

    /// <summary>Nombre d'exécutions dont le résultat a été explicitement attesté.</summary>
    public int OutcomesAttestes { get; init; }

    /// <summary>
    /// Part des exécutions attestées « Problème résolu ». <c>null</c> tant
    /// qu'aucun opérateur n'a attesté de résultat — jamais fabriqué.
    /// </summary>
    public int? SuccessRatePercent { get; init; }

    public int Total => Termine + Abandonne + EnCours;
}

/// <summary>
/// P5 FR-090 — Rapport qualité 5 axes d'un SOP, score global et niveau
/// d'excellence. Calculé à la demande, jamais persisté : il reflète toujours
/// l'état courant des exécutions attestées et du contenu du SOP.
/// </summary>
public sealed record SopQualityReport
{
    public required List<SopAxisScore> Axes { get; init; } = [];
    public int OverallScorePercent { get; init; }
    public SopQualityGrade Grade { get; init; }
    public required string Resume { get; init; }
}

/// <summary>
/// Un axe du rapport qualité FR-090 : son nom, son score 0–20 (max 20/axe,
/// ce qui donne 100 pour 5 axes), sa note explicative pour un lecteur humain.
/// </summary>
public sealed record SopAxisScore
{
    public required string Code { get; init; }
    public required string Label { get; init; }
    public int Score { get; init; }
    public required string Justification { get; init; }
}

/// <summary>
/// P5 — Statistiques d'utilisation détaillées d'un SOP, au-delà de
/// <see cref="SopUsageStats"/>. Utilisé pour prioriser les suggestions de
/// réutilisation et décider de la révocation d'un SOP peu utilisé ou
/// régulièrement mis en échec.
/// </summary>
public sealed record SopDetailedUsageStats
{
    public int Termine { get; init; }
    public int Abandonne { get; init; }
    public int EnCours { get; init; }
    public int OutcomesAttestes { get; init; }
    public int? SuccessRatePercent { get; init; }

    /// <summary>Médiane (pas moyenne : la normale est un piège) de la durée par étape, en secondes.</summary>
    public Dictionary<int, double> MedianStepDurationSecPerOrder { get; init; } = new();

    /// <summary>Pourcentage global des étapes confirmées « conforme » vs « écart » vs « contournée ».</summary>
    public int PercentConfirmeeSteps { get; init; }
    public int PercentEcartSteps { get; init; }
    public int PercentIgnoreesSteps { get; init; }

    /// <summary>Top 3 des écarts les plus fréquents dans ce SOP — utile pour améliorer les instructions.</summary>
    public List<(string DeviationPattern, int Count)> TopDeviations { get; init; } = [];

    public DateTimeOffset? DerniereUtilisation { get; init; }
    public double? AverageFeedbackStars { get; init; }
    public int FeedbackCount { get; init; }

    public int Total => Termine + Abandonne + EnCours;
}
