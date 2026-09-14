using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Gestion des workflows versionnés (FR-003, FR-004).
///
/// Règle centrale : UN WORKFLOW QUI A SERVI NE SE MODIFIE PLUS. Toute
/// modification produit une nouvelle version, et l'ancienne reste attachée aux
/// exécutions qu'elle a produites.
///
/// Sans cela, le rapport d'une opération de la semaine dernière décrirait des
/// étapes qui ne sont pas celles qui ont tourné. La traçabilité s'effondrerait
/// précisément le jour où elle compte : celui où l'on cherche à comprendre ce
/// qui s'est passé.
/// </summary>
public sealed class WorkflowService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<WorkflowService> logger,
    IAuditWriter auditWriter,
    Palier2ApprovalService? palier2Approval = null)
{
    // -----------------------------------------------------------------------
    // Lecture
    // -----------------------------------------------------------------------
    public async Task<List<Workflow>> GetForEnvironmentAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Workflows
            .AsNoTracking()
            .Include(w => w.Steps)
            .Where(w => w.EnvironmentId == environmentId)
            .OrderBy(w => w.Kind).ThenBy(w => w.Code).ThenByDescending(w => w.Version)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Dernière version utilisable de chaque workflow. C'est ce qu'on propose
    /// au lancement : les versions antérieures existent pour l'historique, pas
    /// pour être rejouées.
    /// </summary>
    public async Task<List<Workflow>> GetRunnableAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var tous = await db.Workflows
            .AsNoTracking()
            .Include(w => w.Steps)
            .Where(w => w.EnvironmentId == environmentId
                        && (w.Status == LifecycleStatus.Valide || w.Status == LifecycleStatus.Actif))
            .ToListAsync(ct);

        return tous
            .GroupBy(w => w.Code)
            .Select(g => g.OrderByDescending(w => w.Version).First())
            .Where(w => w.Steps.Count > 0)
            .OrderBy(w => w.Kind)
            .ToList();
    }

    public async Task<Workflow?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Workflows
            .AsNoTracking()
            .Include(w => w.Steps.OrderBy(s => s.Order))
            .Include(w => w.Environment)
            .FirstOrDefaultAsync(w => w.Id == id, ct);
    }

    // -----------------------------------------------------------------------
    // Écriture
    // -----------------------------------------------------------------------
    public async Task<Guid> CreateAsync(Workflow workflow, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        workflow.Version = 1;
        workflow.Status = LifecycleStatus.Brouillon;
        workflow.HasBeenExecuted = false;

        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);
        return workflow.Id;
    }

    /// <summary>
    /// Enregistre une modification.
    ///
    /// Tant que le workflow n'a jamais servi, il est modifié sur place — un
    /// brouillon n'a pas d'historique à préserver. Dès qu'une exécution s'en
    /// est servie, une NOUVELLE VERSION est créée et l'ancienne reste intacte.
    /// </summary>
    public async Task<SaveWorkflowResult> SaveAsync(
        Guid workflowId, string name, string? description, List<WorkflowStep> steps,
        bool requiresApproval, bool requiresDoubleApproval,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existant = await db.Workflows
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId, ct);

        if (existant is null) return SaveWorkflowResult.Failed("Workflow introuvable.");

        var erreur = Valider(steps);
        if (erreur is not null) return SaveWorkflowResult.Failed(erreur);

        if (existant.AutomationLevel == AutomationLevel.AutomatiqueBoutEnBout
            && palier2Approval is not null
            && !await palier2Approval.IsApprovedForEnv(existant.EnvironmentId, ct))
        {
            await auditWriter.WriteAsync(
                AuditAction.TentativeNonAutorisee, AuditOutcome.Refuse,
                actor: "systeme",
                entityType: nameof(Workflow), entityId: existant.Id.ToString(),
                entityLabel: $"{existant.Code} v{existant.Version} — {existant.Name}",
                environmentId: existant.EnvironmentId,
                reason: "[BLOCAGE PALIER2 NON APPROUVÉ] Sauvegarde d'un workflow en AutomatiqueBoutEnBout sur un environnement non approuvé.",
                ct: ct);

            logger.LogWarning(
                "[BLOCAGE PALIER2 NON APPROUVÉ] Workflow {Code} v{Version} sauvegardé en AutomatiqueBoutEnBout sur Env {EnvId} : refusé.",
                existant.Code, existant.Version, existant.EnvironmentId);

            return SaveWorkflowResult.Failed(
                "Ce workflow est positionné en Palier 2 (AutomatiqueBoutEnBout), mais l'environnement "
                + $"associé n'a pas reçu d'approbation Palier 2. Basculez le workflow en Palier 1 "
                + $"({nameof(AutomationLevel.SemiAutomatique)}) ou demandez à un AdminN4 d'approuver "
                + "l'environnement pour le Palier 2.");
        }

        // Une double approbation sans approbation simple n'aurait pas de sens.
        if (requiresDoubleApproval) requiresApproval = true;

        if (!existant.HasBeenExecuted)
        {
            existant.Name = name;
            existant.Description = description;
            existant.RequiresApproval = requiresApproval;
            existant.RequiresDoubleApproval = requiresDoubleApproval;

            db.WorkflowSteps.RemoveRange(existant.Steps);
            foreach (var (etape, index) in steps.Select((s, i) => (s, i)))
            {
                etape.Id = Guid.NewGuid();
                etape.WorkflowId = existant.Id;
                etape.Order = index + 1;
                db.WorkflowSteps.Add(etape);
            }

            await db.SaveChangesAsync(ct);
            return SaveWorkflowResult.Ok(existant.Id, existant.Version, nouvelleVersion: false);
        }

        // Le workflow a servi : on ne le touche pas. Nouvelle version.
        var derniereVersion = await db.Workflows
            .Where(w => w.EnvironmentId == existant.EnvironmentId && w.Code == existant.Code)
            .MaxAsync(w => w.Version, ct);

        var nouveau = new Workflow
        {
            EnvironmentId = existant.EnvironmentId,
            Code = existant.Code,
            Name = name,
            Kind = existant.Kind,
            Version = derniereVersion + 1,
            Status = LifecycleStatus.Brouillon,
            Description = description,
            RequiresApproval = requiresApproval,
            RequiresDoubleApproval = requiresDoubleApproval,
            HasBeenExecuted = false
        };

        db.Workflows.Add(nouveau);
        await db.SaveChangesAsync(ct);

        foreach (var (etape, index) in steps.Select((s, i) => (s, i)))
        {
            etape.Id = Guid.NewGuid();
            etape.WorkflowId = nouveau.Id;
            etape.Order = index + 1;
            db.WorkflowSteps.Add(etape);
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Workflow {Code} : version {Ancienne} déjà exécutée, version {Nouvelle} créée. "
            + "L'ancienne reste attachée à ses exécutions.",
            existant.Code, existant.Version, nouveau.Version);

        return SaveWorkflowResult.Ok(nouveau.Id, nouveau.Version, nouvelleVersion: true);
    }

    // -----------------------------------------------------------------------
    // Génération des séquences N4 de référence
    // -----------------------------------------------------------------------
    /// <summary>
    /// Construit les trois séquences de base — démarrage, arrêt, contrôle — à
    /// partir du référentiel de l'environnement.
    ///
    /// Elles sont créées en BROUILLON, jamais validées d'office. Une séquence
    /// déduite d'un référentiel reste une proposition : c'est l'exploitation du
    /// site qui la relit, l'ajuste et la valide. Livrer une séquence
    /// automatiquement exécutable sur un écosystème qu'on n'a jamais vu serait
    /// exactement le genre de raccourci que ce projet existe pour éviter.
    ///
    /// L'ORDRE D'ARRÊT N'EST PAS L'INVERSE DE L'ORDRE DE DÉMARRAGE. Sur N4,
    /// l'arrêt suit sa propre séquence : les composants périphériques d'abord
    /// (ECN4Web, ECN4, XPS, Bridge), puis le Standby, puis le Center, puis les
    /// nœuds Cluster. On s'en approche par les rôles, pas par un tri inversé.
    /// </summary>
    public async Task<GenerationResult> GenerateDefaultsAsync(
        Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composants = await db.Components
            .AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId && c.ControlMode != ControlMode.NonSupervise)
            .ToListAsync(ct);

        if (composants.Count == 0)
            return GenerationResult.Failed(
                "Aucun composant supervisé n'est déclaré pour cet environnement. "
                + "Le référentiel doit être renseigné avant de pouvoir en déduire une séquence.");

        var existants = await db.Workflows
            .Where(w => w.EnvironmentId == environmentId)
            .Select(w => w.Code)
            .ToListAsync(ct);

        var crees = new List<string>();

        if (!existants.Contains("DEMARRAGE-COMPLET"))
        {
            var ordonnes = composants
                .OrderBy(c => c.StartOrder)
                .ThenBy(c => RangDemarrage(c.Role))
                .ThenBy(c => c.LogicalName)
                .ToList();

            await CreerAsync(db, environmentId, "DEMARRAGE-COMPLET", "Démarrage complet de l'écosystème",
                WorkflowKind.DemarrageComplet, ordonnes, StepAction.Demarrer, ct);

            crees.Add("Démarrage complet");
        }

        if (!existants.Contains("ARRET-COMPLET"))
        {
            var ordonnes = composants
                .OrderBy(c => RangArret(c.Role))
                .ThenByDescending(c => c.StartOrder)
                .ThenBy(c => c.LogicalName)
                .ToList();

            await CreerAsync(db, environmentId, "ARRET-COMPLET", "Arrêt complet de l'écosystème",
                WorkflowKind.ArretComplet, ordonnes, StepAction.Arreter, ct);

            crees.Add("Arrêt complet");
        }

        if (!existants.Contains("CONTROLE-COMPLET"))
        {
            var ordonnes = composants.OrderBy(c => c.StartOrder).ThenBy(c => c.LogicalName).ToList();

            await CreerAsync(db, environmentId, "CONTROLE-COMPLET", "Contrôle de l'état de l'écosystème",
                WorkflowKind.ControleSeul, ordonnes, StepAction.Verifier, ct);

            crees.Add("Contrôle complet");
        }

        // FR-043 : Scénarios prédéfinis ciblés
        if (!existants.Contains("REDEMARRAGE-INTERFACES"))
        {
            var ordonnes = composants
                .Where(c => c.Role is ComponentRole.BridgeDaemon or ComponentRole.Xps)
                .OrderBy(c => RangArret(c.Role)).ThenBy(c => c.LogicalName).ToList(); // Arrêt
                
            var ordonnesDemarrage = composants
                .Where(c => c.Role is ComponentRole.BridgeDaemon or ComponentRole.Xps)
                .OrderBy(c => RangDemarrage(c.Role)).ThenBy(c => c.LogicalName).ToList(); // Redémarrage

            if (ordonnes.Count > 0)
            {
                await CreerRedemarragePartielAsync(db, environmentId, "REDEMARRAGE-INTERFACES", 
                    "Redémarrage des interfaces (Bridge, XPS)", WorkflowKind.OperationPartielle, ordonnes, ordonnesDemarrage, ct);
                crees.Add("Redémarrage Interfaces");
            }
        }

        if (!existants.Contains("REDEMARRAGE-PUPITRE"))
        {
            var ordonnes = composants
                .Where(c => c.Role is ComponentRole.Ecn4 or ComponentRole.Ecn4Web)
                .OrderBy(c => RangArret(c.Role)).ThenBy(c => c.LogicalName).ToList(); // Arrêt
                
            var ordonnesDemarrage = composants
                .Where(c => c.Role is ComponentRole.Ecn4 or ComponentRole.Ecn4Web)
                .OrderBy(c => RangDemarrage(c.Role)).ThenBy(c => c.LogicalName).ToList(); // Redémarrage

            if (ordonnes.Count > 0)
            {
                await CreerRedemarragePartielAsync(db, environmentId, "REDEMARRAGE-PUPITRE", 
                    "Redémarrage du pupitre (ECN4, ECN4Web)", WorkflowKind.OperationPartielle, ordonnes, ordonnesDemarrage, ct);
                crees.Add("Redémarrage Pupitre");
            }
        }

        // FR-046 : Séquence de bascule Center
        if (!existants.Contains("BASCULE-CENTER"))
        {
            var standby = composants.FirstOrDefault(c => c.Role == ComponentRole.StandbyCenterNode);
            var center = composants.FirstOrDefault(c => c.Role == ComponentRole.CenterNode);

            if (standby is null || center is null)
            {
                logger.LogWarning("Impossible de générer BASCULE-CENTER : l'environnement {Env} n'a pas de Standby ou de Center déclaré.", environmentId);
            }
            else
            {
                await CreerBasculeCenterAsync(db, environmentId, "BASCULE-CENTER", 
                    "Bascule du Center (Standby devient Primaire)", standby, center, ct);
                crees.Add("Bascule Center");
            }
        }

        // P6 — REDEMARRAGE-COMPLET §3.10.2 (ordre arrêt Kaleris PUIS ordre démarrage exact)
        if (!existants.Contains("REDEMARRAGE-COMPLET"))
        {
            var ordresArret = composants
                .OrderBy(c => RangArret(c.Role))
                .ThenBy(c => c.LogicalName)
                .ToList();
            var ordresDemarrage = composants
                .OrderBy(c => RangDemarrage(c.Role))
                .ThenBy(c => c.LogicalName)
                .ToList();

            if (ordresArret.Count > 0)
            {
                await CreerRedemarrageCompletN4Async(db, environmentId, "REDEMARRAGE-COMPLET",
                    "Redémarrage complet N4 §3.10.2 — arrêt Kaleris précis PUIS démarrage (Cluster → Center → Standby → Bridge → XPS → ECN4)",
                    WorkflowKind.RedemarrageComplet, ordresArret, ordresDemarrage, ct);
                crees.Add("Redémarrage complet §3.10.2");
            }
        }

        // P6 — CONTRÔLE HEBDOMADAIRE (état de santé complet + alertes ouvertes revue + DB + EDI sync)
        if (!existants.Contains("CONTROLE-HEBDOMADAIRE"))
        {
            var controles = composants.OrderBy(c => RangDemarrage(c.Role)).ThenBy(c => c.LogicalName).ToList();
            await CreerControleHebdomadaireAsync(db, environmentId, controles, ct);
            crees.Add("Contrôle hebdomadaire");
        }

        // P6 — MAINTENANCE MENSUELLE N4 (snapshots, purge logs, redémarrage interfaces si > 15j uptime)
        if (!existants.Contains("MAINTENANCE-MENSUELLE-N4"))
        {
            var interfaces = composants
                .Where(c => c.Role is ComponentRole.BridgeDaemon or ComponentRole.Xps or ComponentRole.InterfaceEdi)
                .OrderBy(c => RangArret(c.Role)).ThenBy(c => c.LogicalName).ToList();
            await CreerMaintenanceMensuelleN4Async(db, environmentId, interfaces, ct);
            crees.Add("Maintenance mensuelle N4");
        }

        if (crees.Count == 0)
            return GenerationResult.Failed(
                "Les séquences de référence existent déjà pour cet environnement. "
                + "Elles ne sont pas régénérées : ce sont peut-être des versions relues et ajustées sur place.");

        return GenerationResult.Ok(crees);
    }

    private async Task CreerAsync(
        N4SentinelDbContext db, Guid environmentId, string code, string nom, WorkflowKind nature,
        List<N4Component> composants, StepAction action, CancellationToken ct)
    {
        var workflow = new Workflow
        {
            EnvironmentId = environmentId,
            Code = code,
            Name = nom,
            Kind = nature,
            Version = 1,
            Status = LifecycleStatus.Brouillon,
            Description =
                "Séquence déduite du référentiel : ordre de démarrage déclaré, rôles N4 et dépendances. "
                + "À RELIRE ET AJUSTER avant validation — elle n'a pas été vérifiée sur cet écosystème."
        };

        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);

        var ordre = 1;

        if (nature == WorkflowKind.DemarrageComplet)
        {
            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id,
                Order = ordre++,
                Name = "Confirmation préalable client 1",
                Action = StepAction.InterventionManuelle,
                Instruction = "Obtenir la confirmation d'entame du démarrage complet auprès du client.",
                ExpectedSeconds = 60,
                TimeoutSeconds = 3600,
                FailurePolicy = StepFailurePolicy.Bloquer,
                CanRunInParallel = false
            });

            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id,
                Order = ordre++,
                Name = "Confirmation préalable client 2",
                Action = StepAction.InterventionManuelle,
                Instruction = "Double vérification (Confirmation 2) avant le lancement des services critiques.",
                ExpectedSeconds = 60,
                TimeoutSeconds = 3600,
                FailurePolicy = StepFailurePolicy.Bloquer,
                CanRunInParallel = false
            });
        }

        foreach (var composant in composants)
        {
            // Un composant non pilotable est CONTROLE, jamais commande : le
            // rattachement a la cartographie ne vaut pas autorisation d'agir.
            var actionReelle = composant.CanBeControlled ? action : StepAction.Verifier;

            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id,
                Order = ordre++,
                Name = $"{Verbe(actionReelle)} {composant.LogicalName}",
                Action = actionReelle,
                ComponentId = composant.Id,
                ExpectedSeconds = actionReelle == StepAction.Demarrer ? 180 : 60,
                TimeoutSeconds = actionReelle switch
                {
                    StepAction.Demarrer => composant.Readiness.LogReadyTimeoutSeconds,
                    StepAction.Arreter => composant.Readiness.StopTimeoutSeconds,
                    _ => 120
                },
                FailurePolicy = StepFailurePolicy.Bloquer,
                CanRunInParallel = false
            });
        }

        if (nature == WorkflowKind.DemarrageComplet)
        {
            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id,
                Order = ordre++,
                Name = "Contrôle 10 (Post-Démarrage)",
                Action = StepAction.InterventionManuelle,
                Instruction = "Effectuer les vérifications de validation (Contrôle 10) pour confirmer l'opérationnalité globale.",
                ExpectedSeconds = 300,
                TimeoutSeconds = 7200,
                FailurePolicy = StepFailurePolicy.Bloquer,
                CanRunInParallel = false
            });
            
            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id,
                Order = ordre++,
                Name = "Validation fonctionnelle finale (FR-035)",
                Action = StepAction.InterventionManuelle,
                Instruction = "Confirmer avec les équipes métier que les opérations fonctionnelles peuvent reprendre sans risque.",
                ExpectedSeconds = 300,
                TimeoutSeconds = 7200,
                FailurePolicy = StepFailurePolicy.Bloquer,
                CanRunInParallel = false
            });

            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id,
                Order = ordre++,
                Name = "Recette technique consolidée (FR-037)",
                Action = StepAction.InterventionManuelle,
                Instruction = "Rédiger et joindre la recette technique post-démarrage attestant de la complétude du workflow.",
                ExpectedSeconds = 300,
                TimeoutSeconds = 7200,
                FailurePolicy = StepFailurePolicy.Bloquer,
                CanRunInParallel = false
            });
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Séquence « {Nom} » générée pour l'environnement {Env} : {Etapes} étapes, en brouillon.",
            nom, environmentId, composants.Count);
    }

    private async Task CreerRedemarragePartielAsync(
        N4SentinelDbContext db, Guid environmentId, string code, string nom, WorkflowKind nature,
        List<N4Component> composantsArret, List<N4Component> composantsDemarrage, CancellationToken ct)
    {
        var workflow = new Workflow
        {
            EnvironmentId = environmentId,
            Code = code,
            Name = nom,
            Kind = nature,
            Version = 1,
            Status = LifecycleStatus.Brouillon,
            Description = "Séquence prédéfinie de redémarrage partiel. À RELIRE ET AJUSTER avant validation."
        };

        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);

        var ordre = 1;

        // Arrêt
        foreach (var composant in composantsArret)
        {
            var actionReelle = composant.CanBeControlled ? StepAction.Arreter : StepAction.Verifier;
            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id, Order = ordre++, Name = $"Arrêter {composant.LogicalName}",
                Action = actionReelle, ComponentId = composant.Id, ExpectedSeconds = 60,
                TimeoutSeconds = composant.Readiness.StopTimeoutSeconds, FailurePolicy = StepFailurePolicy.Bloquer
            });
        }

        // Démarrage
        foreach (var composant in composantsDemarrage)
        {
            var actionReelle = composant.CanBeControlled ? StepAction.Demarrer : StepAction.Verifier;
            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id, Order = ordre++, Name = $"Démarrer {composant.LogicalName}",
                Action = actionReelle, ComponentId = composant.Id, ExpectedSeconds = 180,
                TimeoutSeconds = composant.Readiness.LogReadyTimeoutSeconds, FailurePolicy = StepFailurePolicy.Bloquer
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Séquence « {Nom} » générée pour l'environnement {Env}.", nom, environmentId);
    }

    private async Task CreerBasculeCenterAsync(
        N4SentinelDbContext db, Guid environmentId, string code, string nom, N4Component standby, N4Component center, CancellationToken ct)
    {
        var workflow = new Workflow
        {
            EnvironmentId = environmentId, Code = code, Name = nom, Kind = WorkflowKind.OperationPartielle,
            Version = 1, Status = LifecycleStatus.Brouillon, RequiresApproval = true, RequiresDoubleApproval = true,
            Description = "Séquence complète de bascule du Center Node. (FR-046)"
        };

        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);

        var ordre = 1;
        
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++, Name = $"Arrêter {standby.LogicalName}",
            Action = StepAction.Arreter, ComponentId = standby.Id, ExpectedSeconds = 60,
            TimeoutSeconds = standby.Readiness.StopTimeoutSeconds, FailurePolicy = StepFailurePolicy.Bloquer
        });

        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++, Name = "Attente IP Virtuelle / Bascule DNS",
            Action = StepAction.InterventionManuelle, Instruction = "Vérifier que la bascule de l'IP ou du DNS a bien été effectuée vers le nœud désigné actif.",
            ExpectedSeconds = 60, TimeoutSeconds = 3600, FailurePolicy = StepFailurePolicy.Bloquer
        });

        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++, Name = $"Démarrer {center.LogicalName}",
            Action = StepAction.Demarrer, ComponentId = center.Id, ExpectedSeconds = 180,
            TimeoutSeconds = center.Readiness.LogReadyTimeoutSeconds, FailurePolicy = StepFailurePolicy.Bloquer
        });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Séquence « {Nom} » générée pour l'environnement {Env}.", nom, environmentId);
    }

    private async Task CreerRedemarrageCompletN4Async(
        N4SentinelDbContext db, Guid environmentId, string code, string nom, WorkflowKind nature,
        List<N4Component> composantsArret, List<N4Component> composantsDemarrage, CancellationToken ct)
    {
        var workflow = new Workflow
        {
            EnvironmentId = environmentId, Code = code, Name = nom, Kind = nature,
            Version = 1, Status = LifecycleStatus.Brouillon, RequiresApproval = true,
            Description = "Redémarrage complet N4 conforme §3.10.2 guide Kaleris N4 IT Admin Day 1 : ordre arrêt Standby/Clusters/Center exact PUIS ordre démarrage BDD → AMQ → Cluster → Center → Standby → Bridge → XPS → ECN4 → Web. À RELIRE ET AJUSTER."
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);
        var ordre = 1;

        // Pré-confirmations client (avant arrêt complet, comme Démarrage)
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Confirmation client 1 — entente redémarrage complet N4",
            Action = StepAction.InterventionManuelle,
            Instruction = "Obtenir la confirmation écrite du client avant tout arrêt de service N4.",
            ExpectedSeconds = 60, TimeoutSeconds = 3600, FailurePolicy = StepFailurePolicy.Bloquer
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Confirmation client 2 — double vérification impact",
            Action = StepAction.InterventionManuelle,
            Instruction = "Double validation : plus aucun mouvement EN COURS de chargement/déchargement, pics métier terminés.",
            ExpectedSeconds = 60, TimeoutSeconds = 3600, FailurePolicy = StepFailurePolicy.Bloquer
        });

        // Phase 1 : Arrêt complet ordre Kaleris §3.10.2
        foreach (var c in composantsArret)
        {
            var act = c.CanBeControlled ? StepAction.Arreter : StepAction.Verifier;
            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id, Order = ordre++,
                Name = $"[ARRÊT §3.10.2] {Verbe(act)} {c.LogicalName}",
                Action = act, ComponentId = c.Id,
                ExpectedSeconds = act == StepAction.Arreter ? 60 : 15,
                TimeoutSeconds = c.Readiness.StopTimeoutSeconds,
                FailurePolicy = StepFailurePolicy.Bloquer
            });
        }

        // Temporisation entre arrêt et démarrage pour que les connexions TCP se ferment proprement
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Temporisation post-arrêt (2 min pour fermeture connexions TCP/AMQ)",
            Action = StepAction.Attendre,
            Instruction = "Attendre 2 minutes complètes entre la fin de l'arrêt et le début du démarrage. Nécessaire pour fermer KahaDB locks et éviter split-brain BDD.",
            ExpectedSeconds = 120, TimeoutSeconds = 180, FailurePolicy = StepFailurePolicy.Continuer
        });

        // Phase 2 : Démarrage ordre exact
        foreach (var c in composantsDemarrage)
        {
            var act = c.CanBeControlled ? StepAction.Demarrer : StepAction.Verifier;
            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id, Order = ordre++,
                Name = $"[DÉMARRAGE §3.10.2] {Verbe(act)} {c.LogicalName}",
                Action = act, ComponentId = c.Id,
                ExpectedSeconds = act == StepAction.Demarrer ? 180 : 30,
                TimeoutSeconds = c.Readiness.LogReadyTimeoutSeconds,
                FailurePolicy = StepFailurePolicy.Bloquer
            });
        }

        // Contrôles post-démarrage (Contrôle 10 + validation métier + recette technique)
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Contrôle 10 (Post-Redémarrage) : état des composants & connectivité",
            Action = StepAction.InterventionManuelle,
            Instruction = "Effectuer les 10 points de contrôle standard Kaleris : services Running, logs sans FATAL, connecteurs EDI OK, XPS Bridge messages en transit, Heartbeat OK.",
            ExpectedSeconds = 300, TimeoutSeconds = 7200, FailurePolicy = StepFailurePolicy.Bloquer
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Validation fonctionnelle finale (FR-035)",
            Action = StepAction.InterventionManuelle,
            Instruction = "Confirmer avec les équipes métier : affichage ECRAN principal OK, chargement/déchargement repris, interfaces portail client OK.",
            ExpectedSeconds = 300, TimeoutSeconds = 7200, FailurePolicy = StepFailurePolicy.Bloquer
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Recette technique consolidée (FR-037) après redémarrage",
            Action = StepAction.InterventionManuelle,
            Instruction = "Joindre la recette technique : captures d'écran services Running, 0 erreur FATAL sur 10 min rolling, latence DB <50ms, latence AMQ <10ms, NTP synchro OK.",
            ExpectedSeconds = 300, TimeoutSeconds = 7200, FailurePolicy = StepFailurePolicy.Bloquer
        });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Séquence « {Nom} » REDÉMARRAGE COMPLET §3.10.2 générée pour l'environnement {Env} : {N} étapes.",
            nom, environmentId, ordre - 1);
    }

    private async Task CreerControleHebdomadaireAsync(
        N4SentinelDbContext db, Guid environmentId, List<N4Component> composants, CancellationToken ct)
    {
        var workflow = new Workflow
        {
            EnvironmentId = environmentId, Code = "CONTROLE-HEBDOMADAIRE",
            Name = "Contrôle hebdomadaire global & revue alertes N4",
            Kind = WorkflowKind.MaintenanceHebdomadaire,
            Version = 1, Status = LifecycleStatus.Brouillon,
            Description = "Contrôle hebdomadaire planifié : revue chaque composant, santé DB, synchronisation EDI/N4-XPS, disques libres, alertes ouvertes, horloges NTP. Idéalement exécuté vendredi matin ou lundi midi."
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);
        var ordre = 1;

        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Rappel : prendre screenshot de départ page d'accueil Sentinel",
            Action = StepAction.InterventionManuelle,
            Instruction = "Prendre une capture d'écran de la page d'accueil Dashboard N4 Sentinel AVANT toute intervention : historique comparatif hebdo.",
            ExpectedSeconds = 60, TimeoutSeconds = 300, FailurePolicy = StepFailurePolicy.Continuer
        });

        foreach (var c in composants)
        {
            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id, Order = ordre++,
                Name = $"Contrôler état {c.LogicalName} (Running, logs sans FATAL)",
                Action = StepAction.Verifier, ComponentId = c.Id,
                ExpectedSeconds = 30, TimeoutSeconds = 120, FailurePolicy = StepFailurePolicy.DemanderALOperateur
            });
        }

        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Santé Base de données N4 (FR-057) : latence, locks, slow queries",
            Action = StepAction.InterventionManuelle,
            Instruction = "Vérifier : requêtes lentes (>5s dernière heure) < 10, locks bloquants = 0, tables Fragmentation < 30%, Backup last successful < 24h old. Cocher si OK.",
            ExpectedSeconds = 300, TimeoutSeconds = 1200, FailurePolicy = StepFailurePolicy.DemanderALOperateur
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Synchro N4-XPS FR-056 : dernier échange normal confirmé récemment",
            Action = StepAction.InterventionManuelle,
            Instruction = "Contrôler les marqueurs d'échanges sains : timestamp dernière intégration réussie N4 ↔ XPS < 60 minutes. Sinon escalader.",
            ExpectedSeconds = 120, TimeoutSeconds = 600, FailurePolicy = StepFailurePolicy.DemanderALOperateur
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Espace disque FR-054 & vérifications Alertes ouvertes",
            Action = StepAction.InterventionManuelle,
            Instruction = "Vérifier disques > 20% libres sur TOUS les serveurs référencés. Revue Alertes Sentinel ouvertes : fermer les résolues ou faire une APR si signal faible répété.",
            ExpectedSeconds = 300, TimeoutSeconds = 1800, FailurePolicy = StepFailurePolicy.Bloquer
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Contrôle NTP & horloges (TOP cause P1 n°5 Navis)",
            Action = StepAction.InterventionManuelle,
            Instruction = "Vérifier écart NTP < ±2s sur TOUS les serveurs (BDD, Center, Standby, XPS, Bridge, ECN4). Si > ±5s → planifier une resynchronisation graduelle.",
            ExpectedSeconds = 180, TimeoutSeconds = 900, FailurePolicy = StepFailurePolicy.DemanderALOperateur
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "Recette hebdomadaire consolidée & envoi équipe référent",
            Action = StepAction.InterventionManuelle,
            Instruction = "Rédiger rapport hebdomadaire 1 page : état global / anomalies détectées / actions faites / alertes toujours ouvertes. Envoyer à l'équipe référent N4 + mailing exploitation.",
            ExpectedSeconds = 300, TimeoutSeconds = 3600, FailurePolicy = StepFailurePolicy.Continuer
        });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Contrôle hebdomadaire généré pour Env {Env} : {N} étapes.", environmentId, ordre - 1);
    }

    private async Task CreerMaintenanceMensuelleN4Async(
        N4SentinelDbContext db, Guid environmentId, List<N4Component> interfacesN4, CancellationToken ct)
    {
        var workflow = new Workflow
        {
            EnvironmentId = environmentId, Code = "MAINTENANCE-MENSUELLE-N4",
            Name = "Maintenance mensuelle N4 : snapshots, purge logs, redémarrage sélectif interfaces",
            Kind = WorkflowKind.MaintenanceMensuelle,
            Version = 1, Status = LifecycleStatus.Brouillon, RequiresApproval = true, RequiresDoubleApproval = true,
            Description = "Maintenance mensuelle lourde : snapshot AVANT (tous VMs), revue & application Windows Updates si applicable, purge logs/archive KahaDB/edi/archive, redémarrage sélectif Bridge+XPS si uptime > 15 jours, Contrôle 10 final."
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(ct);
        var ordre = 1;

        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "P1 — Snapshots VM AVANT TOUTE INTERVENTION",
            Action = StepAction.InterventionManuelle,
            Instruction = "Prendre un snapshot/backup applicatif ET système sur TOUTES les VMs N4 (hyperscaler ou hyperviseur). Noter les IDs de snapshot en commentaire. NE PAS commencer tant que les snapshots ne sont pas confirmés par l'équipe infra.",
            ExpectedSeconds = 1800, TimeoutSeconds = 7200, FailurePolicy = StepFailurePolicy.Bloquer
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "P2 — Revue & planification Windows Updates / patches OS",
            Action = StepAction.InterventionManuelle,
            Instruction = "Vérifier la stratégie correctives : patches du mois appliqués hors fenêtre métier ou a minima les patches critique de sécurité. Si nécessaire basculer en fenêtre de maintenance prévue pour application, PAS pendant cette maintenance sans approbation écrite.",
            ExpectedSeconds = 600, TimeoutSeconds = 1800, FailurePolicy = StepFailurePolicy.DemanderALOperateur
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "P3 — Purge fichiers logs archivés (> 90 jours) + nettoyage dossiers temporaires Bridge",
            Action = StepAction.InterventionManuelle,
            Instruction = "Nettoyer les répertoires de logs archivés, fichiers tmp, répertoires Bridge temp (> 90j). Vérifier taille répertoire KahaDB journal & data. Si CRITICAL (>80% sur disque), planifier compactage.",
            ExpectedSeconds = 600, TimeoutSeconds = 1800, FailurePolicy = StepFailurePolicy.Continuer
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "P4 — Vérification uptime des interfaces Bridge + XPS (redémarrer si > 15 jours)",
            Action = StepAction.InterventionManuelle,
            Instruction = "Lire l'uptime des services Windows BridgeDaemon + XPS. Si > 15 jours continus, procéder au redémarrage sélectif via le sous-ordre arrêt/démarrage. (Cette étape guide, les sous-étapes techniques existent déjà via sous-workflow Redémarrage interfaces.)",
            ExpectedSeconds = 600, TimeoutSeconds = 3600, FailurePolicy = StepFailurePolicy.DemanderALOperateur
        });

        // Si des interfaces sont connues, les redémarrer (sélectif via pattern partiel)
        if (interfacesN4.Count > 0)
        {
            // Arrêt
            foreach (var i in interfacesN4)
            {
                var act = i.CanBeControlled ? StepAction.Arreter : StepAction.Verifier;
                db.WorkflowSteps.Add(new WorkflowStep
                {
                    WorkflowId = workflow.Id, Order = ordre++,
                    Name = $"[Interfaces - arrêt pré-redémarrage] {Verbe(act)} {i.LogicalName}",
                    Action = act, ComponentId = i.Id,
                    ExpectedSeconds = 60, TimeoutSeconds = i.Readiness.StopTimeoutSeconds,
                    FailurePolicy = StepFailurePolicy.DemanderALOperateur
                });
            }
            // Temporisation
            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = workflow.Id, Order = ordre++,
                Name = "Temporisation entre arrêt et démarrage interfaces (60s)",
                Action = StepAction.Attendre,
                Instruction = "Attendre 60 secondes pour fermeture propre fichiers EDI lock.",
                ExpectedSeconds = 60, TimeoutSeconds = 90, FailurePolicy = StepFailurePolicy.Continuer
            });
            // Démarrage
            foreach (var i in interfacesN4.OrderBy(c => RangDemarrage(c.Role)).ThenBy(c => c.LogicalName).ToList())
            {
                var act = i.CanBeControlled ? StepAction.Demarrer : StepAction.Verifier;
                db.WorkflowSteps.Add(new WorkflowStep
                {
                    WorkflowId = workflow.Id, Order = ordre++,
                    Name = $"[Interfaces - démarrage] {Verbe(act)} {i.LogicalName}",
                    Action = act, ComponentId = i.Id,
                    ExpectedSeconds = 180, TimeoutSeconds = i.Readiness.LogReadyTimeoutSeconds,
                    FailurePolicy = StepFailurePolicy.Bloquer
                });
            }
        }

        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = workflow.Id, Order = ordre++,
            Name = "P5 — Contrôle 10 final et recette mensuelle FR-037",
            Action = StepAction.InterventionManuelle,
            Instruction = "Lancer les 10 points Contrôle N4, joindre captures services Running, intégrations OK, pas FATAL 10min rolling. Rapport mensuel signé envoyé au client/RACI. Nettoyage snapshots APRES validation métier.",
            ExpectedSeconds = 600, TimeoutSeconds = 3600, FailurePolicy = StepFailurePolicy.Bloquer
        });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Maintenance mensuelle générée Env {Env} : {N} étapes.", environmentId, ordre - 1);
    }

    /// <summary>
    /// Ordre de démarrage N4, conforme au guide Kaleris
    /// « N4 IT Administrator — Day 1: Installation, Startup » (2024).
    ///
    /// Cluster → Center → Standby → Bridge → XPS → ECN4 → ECN4Web.
    ///
    /// Les nœuds Cluster se démarrent AVANT le Center et affichent DISCONNECTED
    /// tant que celui-ci n'est pas monté : c'est le comportement normal décrit
    /// par l'éditeur, pas une anomalie.
    /// </summary>
    /// <summary>
    /// Interne plutôt que privé : réutilisé par <see cref="PreflightService"/>
    /// pour proposer l'ordre d'arrêt des composants encore actifs avant un
    /// démarrage complet (FR-036/AC-16), sans dupliquer cette table.
    /// </summary>
    internal static int RangDemarrage(ComponentRole role) => role switch
    {
        ComponentRole.BaseDeDonnees => 0,
        ComponentRole.ActiveMq => 1,
        ComponentRole.DossierPartage => 2,
        ComponentRole.ClusterNode => 3,
        ComponentRole.CenterNode => 4,
        ComponentRole.StandbyCenterNode => 5,
        ComponentRole.BridgeDaemon => 6,
        ComponentRole.Xps => 7,
        ComponentRole.Ecn4 => 8,
        ComponentRole.Ecn4Web => 9,
        ComponentRole.InterfaceEdi => 10,
        _ => 50
    };

    /// <summary>
    /// Ordre d'arrêt N4, conforme au guide Kaleris « N4 IT Administrator —
    /// Day 1 », module 1.8 « N4 Shutdown Process » :
    ///
    ///   ECN4 Web → ECN4 Daemon → XPS → XPS Bridge Daemon
    ///   → Standby Center Node → Cluster Nodes → Center Node
    ///
    /// CE N'EST PAS L'INVERSE DE L'ORDRE DE DÉMARRAGE, et deux inversions
    /// comptent :
    ///
    ///   — le Standby s'arrête AVANT le Center, alors qu'il démarre après lui ;
    ///   — les Cluster Nodes s'arrêtent AVANT le Center Node, qui part en
    ///     dernier. Le Center porte la file de travail et la connexion à la
    ///     base : le couper d'abord priverait les nœuds de ce dont ils ont
    ///     besoin pour se fermer proprement — sauver leurs données, fermer
    ///     leurs connexions, prévenir les autres services.
    ///
    /// L'éditeur classe « improper shutdown and restart process » parmi les dix
    /// premières causes d'incident critique. Cet ordre n'est donc pas une
    /// préférence de style.
    /// </summary>
    /// <summary>Interne : voir <see cref="RangDemarrage"/>.</summary>
    internal static int RangArret(ComponentRole role) => role switch
    {
        ComponentRole.InterfaceEdi => 0,
        ComponentRole.Ecn4Web => 1,
        ComponentRole.Ecn4 => 2,
        ComponentRole.Xps => 3,
        ComponentRole.BridgeDaemon => 4,
        ComponentRole.StandbyCenterNode => 5,
        ComponentRole.ClusterNode => 6,
        ComponentRole.CenterNode => 7,
        ComponentRole.ActiveMq => 8,
        ComponentRole.DossierPartage => 9,
        ComponentRole.BaseDeDonnees => 10,
        _ => 50
    };

    private static string Verbe(StepAction action) => action switch
    {
        StepAction.Demarrer => "Démarrer",
        StepAction.Arreter => "Arrêter",
        StepAction.Redemarrer => "Redémarrer",
        _ => "Contrôler"
    };

    public async Task<string?> ChangeStatusAsync(
        Guid workflowId, LifecycleStatus cible, string actor,
        string? justificationSansSimulation = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var workflow = await db.Workflows
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId, ct);

        if (workflow is null) return "Workflow introuvable.";

        if (cible is LifecycleStatus.Valide or LifecycleStatus.Actif && workflow.Steps.Count == 0)
            return "Un workflow sans étape ne peut pas être validé.";

        // FR-005 : simulation obligatoire avant validation. Valider un
        // workflow le rend lancable pour de vrai sur l'ecosysteme ; l'avoir
        // deroule au moins une fois a blanc est le minimum avant d'autoriser
        // cela.
        //
        // On interroge la PREUVE plutot qu'un drapeau : une simulation reussie
        // de CETTE version, enregistree comme execution. Un drapeau pose a la
        // main resterait vrai apres modification des etapes, ce qui reviendrait
        // a valider une sequence que personne n'a jamais deroulee.
        var derogationSimulation = false;

        if (cible is LifecycleStatus.Valide or LifecycleStatus.Actif)
        {
            var simulee = await db.Executions.AsNoTracking().AnyAsync(
                e => e.WorkflowId == workflowId
                  && e.WorkflowVersion == workflow.Version
                  && e.IsSimulation
                  && e.Status == ExecutionStatus.TermineSucces, ct);

            if (!simulee)
            {
                // FR-004 : l'interdiction se lève par une dérogation EXPLICITE
                // et TRACÉE, jamais par un réglage silencieux. Un site sans
                // écosystème de simulation doit pouvoir avancer — mais en
                // écrivant pourquoi, et en le laissant dans la piste d'audit.
                if (string.IsNullOrWhiteSpace(justificationSansSimulation))
                    return $"Ce workflow (version {workflow.Version}) n'a jamais été simulé avec "
                         + "succès. Lancez-le en simulation : elle n'émet aucune commande et ne "
                         + "touche à rien. Une séquence que personne n'a déroulée, même à blanc, "
                         + "ne doit pas devenir lançable en réel. À défaut, une dérogation "
                         + "justifiée est exigée — elle sera tracée.";

                derogationSimulation = true;
            }
        }

        var precedent = workflow.Status;
        workflow.Status = cible;
        await db.SaveChangesAsync(ct);

        // FR-091 : valider un workflow le rend lancable par tout titulaire du
        // role d'execution — ce changement de statut doit rester dans la
        // piste d'audit.
        await auditWriter.WriteAsync(
            AuditAction.ChangementDeStatut, AuditOutcome.Succes, actor,
            entityType: nameof(Workflow), entityId: workflow.Id.ToString(),
            entityLabel: $"{workflow.Name} v{workflow.Version}",
            environmentId: workflow.EnvironmentId,
            reason: derogationSimulation
                ? $"{precedent} → {cible}. VALIDÉ SANS SIMULATION PRÉALABLE (FR-005), "
                  + $"dérogation justifiée : {justificationSansSimulation!.Trim()}"
                : $"{precedent} → {cible}.",
            ct: ct);

        return null;
    }

    /// <summary>
    /// Contrôles de cohérence appliqués avant enregistrement.
    ///
    /// Ils portent sur ce qui rendrait une séquence dangereuse, pas sur la
    /// forme. Une séquence acceptée ici doit pouvoir être jouée sans surprise.
    /// </summary>
    private static string? Valider(List<WorkflowStep> steps)
    {
        if (steps.Count == 0)
            return "Un workflow doit comporter au moins une étape.";

        foreach (var etape in steps)
        {
            if (string.IsNullOrWhiteSpace(etape.Name))
                return "Chaque étape doit porter un nom.";

            if (etape.Action is StepAction.Demarrer or StepAction.Arreter or StepAction.Redemarrer
                && etape.ComponentId is null)
                return $"L'étape « {etape.Name} » agit sur un composant mais n'en désigne aucun.";

            if (etape.Action == StepAction.InterventionManuelle && string.IsNullOrWhiteSpace(etape.Instruction))
                return $"L'étape « {etape.Name} » attend un geste humain sans dire lequel. "
                     + "Une intervention manuelle sans consigne reporte le travail sur l'opérateur.";

            // Reessayer un arret en boucle sans comprendre pourquoi il a echoue
            // est le meilleur moyen d'aggraver un incident (FR-004).
            if (etape.AutomaticRetry
                && etape.Action is StepAction.Arreter or StepAction.Redemarrer
                && etape.MaxRetries > 0)
                return $"L'étape « {etape.Name} » autorise une nouvelle tentative automatique sur une action "
                     + "d'arrêt. C'est interdit : un arrêt qui échoue doit être compris, pas rejoué.";

            if (etape.TimeoutSeconds <= 0)
                return $"L'étape « {etape.Name} » doit porter un délai maximal.";

            if (etape.WarningThresholdSeconds <= 0)
                return $"L'étape « {etape.Name} » doit porter un seuil d'avertissement (FR-004).";

            if (etape.ExpectedSeconds > etape.TimeoutSeconds)
                return $"L'étape « {etape.Name} » annonce une durée normale supérieure à son délai maximal.";

            if (etape.WarningThresholdSeconds > etape.TimeoutSeconds)
                return $"L'étape « {etape.Name} » porte un seuil d'avertissement supérieur à son délai maximal.";

            if (etape.RetryDelaySeconds < 0)
                return $"L'étape « {etape.Name} » ne peut pas porter un délai entre tentatives négatif.";
        }

        return null;
    }
}

public sealed record SaveWorkflowResult
{
    public bool Succeeded { get; init; }
    public Guid WorkflowId { get; init; }
    public int Version { get; init; }

    /// <summary>Vrai si l'enregistrement a produit une nouvelle version.</summary>
    public bool CreatedNewVersion { get; init; }

    public string? Error { get; init; }

    public static SaveWorkflowResult Ok(Guid id, int version, bool nouvelleVersion) =>
        new() { Succeeded = true, WorkflowId = id, Version = version, CreatedNewVersion = nouvelleVersion };

    public static SaveWorkflowResult Failed(string error) => new() { Succeeded = false, Error = error };
}

public sealed record GenerationResult
{
    public bool Succeeded { get; init; }
    public List<string> Created { get; init; } = [];
    public string? Error { get; init; }

    public static GenerationResult Ok(List<string> crees) => new() { Succeeded = true, Created = crees };
    public static GenerationResult Failed(string error) => new() { Succeeded = false, Error = error };
}
