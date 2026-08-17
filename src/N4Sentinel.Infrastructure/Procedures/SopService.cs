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

        return associations
            .Select(a => a.Sop!)
            .Where(s => s.Steps.Count > 0)
            .GroupBy(s => s.Code)
            .Select(g => g.OrderByDescending(s => s.Version).First())
            .OrderByDescending(s => s.ModifiedAt ?? s.CreatedAt)
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
