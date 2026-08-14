using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests du moteur d'orchestration — sprint 4 (ORC-01 à ORC-05, REF-10).
///
/// Ce qui est vérifié ici n'est pas que « ça marche quand tout va bien », mais
/// les quelques comportements dont dépend la confiance qu'on peut accorder à
/// l'outil un dimanche de reprise après incident :
///   — un workflow qui a servi ne se modifie plus ;
///   — une seconde opération mutative sur le même environnement est refusée ;
///   — une étape non contournable ne peut être ignorée par personne ;
///   — une séquence qui viole les dépendances N4 est refusée ;
///   — une exécution interrompue en vol ne reprend PAS toute seule.
/// </summary>
public sealed class OrchestrationTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _keyPath = string.Empty;
    private TestDbContextFactory _factory = null!;
    private WorkflowService _workflows = null!;
    private ExecutionService _executions = null!;
    private EnvironmentLockService _locks = null!;
    private SequenceValidator _validator = null!;
    private PreflightService _preflight = null!;
    private AdHocOperationService _adhoc = null!;
    private ExecutionReportService _report = null!;

    private Guid _envId;
    private Guid _bridgeId;
    private Guid _xpsId;
    private Guid _cluster1Id;
    private Guid _cluster2Id;

    public async Task InitializeAsync()
    {
        var cs = $"Server=localhost;Database={_databaseName};Trusted_Connection=True;"
               + "TrustServerCertificate=True;MultipleActiveResultSets=True";

        _factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<N4SentinelDbContext>().UseSqlServer(cs).Options);

        _locks = new EnvironmentLockService(_factory, NullLogger<EnvironmentLockService>.Instance);
        _workflows = new WorkflowService(_factory, NullLogger<WorkflowService>.Instance);
        _executions = new ExecutionService(_factory, _locks, NullLogger<ExecutionService>.Instance);
        _validator = new SequenceValidator(_factory);
        _adhoc = new AdHocOperationService(_factory, NullLogger<AdHocOperationService>.Instance);
        _report = new ExecutionReportService(_factory);

        _keyPath = Path.Combine(Path.GetTempPath(), $"n4-cles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keyPath);

        var store = new CredentialStore(_factory,
            DataProtectionProvider.Create(new DirectoryInfo(_keyPath)),
            NullLogger<CredentialStore>.Instance);

        _preflight = new PreflightService(
            _factory,
            new ConnectorTargetFactory(_factory, store, NullLogger<ConnectorTargetFactory>.Instance),
            new ConnecteurMuet(),
            _locks,
            NullLogger<PreflightService>.Instance);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        var env = new N4Environment { Code = "UAT", Name = "Recette", Kind = EnvironmentKind.UAT };
        db.Environments.Add(env);
        await db.SaveChangesAsync();
        _envId = env.Id;

        _bridgeId = await AjouterComposantAsync(db, "Bridge Daemon", ComponentRole.BridgeDaemon, 10);
        _xpsId = await AjouterComposantAsync(db, "XPS", ComponentRole.Xps, 20);
        _cluster1Id = await AjouterComposantAsync(db, "Cluster Node 1", ComponentRole.ClusterNode, 1);
        _cluster2Id = await AjouterComposantAsync(db, "Cluster Node 2", ComponentRole.ClusterNode, 2);

        // XPS depend du Bridge : c'est la dependance N4 que le cahier des
        // charges cite explicitement (FR-044).
        db.ComponentDependencies.Add(new ComponentDependency
        {
            ComponentId = _xpsId,
            DependsOnComponentId = _bridgeId,
            Kind = DependencyKind.RequisAuDemarrage
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AjouterComposantAsync(
        N4SentinelDbContext db, string nom, ComponentRole role, int ordre)
    {
        var composant = new N4Component
        {
            EnvironmentId = db.Environments.First().Id,
            LogicalName = nom,
            Role = role,
            StartOrder = ordre,
            WindowsServiceName = $"Navis {nom}",
            ControlMode = ControlMode.Pilotable,
            Status = LifecycleStatus.Valide
        };
        db.Components.Add(composant);
        await db.SaveChangesAsync();
        return composant.Id;
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_keyPath)) try { Directory.Delete(_keyPath, true); } catch { }

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
    // ORC-01 — Versionnement
    // =======================================================================
    [Fact]
    public async Task Un_Workflow_Jamais_Execute_Se_Modifie_Sur_Place()
    {
        var id = await CreerWorkflowAsync("DEM", WorkflowKind.DemarrageComplet);

        var r = await _workflows.SaveAsync(id, "Démarrage complet — révisé", null,
            [EtapeDemarrage("Démarrer le Bridge", _bridgeId, 1)]);

        Assert.True(r.Succeeded, r.Error);
        Assert.False(r.CreatedNewVersion);
        Assert.Equal(1, r.Version);
        Assert.Equal(id, r.WorkflowId);
    }

    [Fact]
    public async Task Un_Workflow_Deja_Execute_Produit_Une_Nouvelle_Version()
    {
        var id = await CreerWorkflowAsync("DEM", WorkflowKind.DemarrageComplet);
        await ValiderAsync(id);

        // Une preparation d'execution suffit a rendre le workflow immuable :
        // des lors qu'une execution s'y rattache, le modifier reecrirait
        // l'histoire de cette execution.
        var prep = await _executions.PrepareAsync(id, "op1", "Test de versionnement", null, isSimulation: true);
        Assert.True(prep.Succeeded, prep.Error);

        var r = await _workflows.SaveAsync(id, "Démarrage complet v2", null,
            [EtapeDemarrage("Démarrer le Bridge", _bridgeId, 1),
             EtapeDemarrage("Démarrer XPS", _xpsId, 2)]);

        Assert.True(r.Succeeded, r.Error);
        Assert.True(r.CreatedNewVersion);
        Assert.Equal(2, r.Version);
        Assert.NotEqual(id, r.WorkflowId);

        // La version 1 reste intacte, avec SES etapes d'origine.
        var v1 = await _workflows.GetAsync(id);
        Assert.NotNull(v1);
        Assert.Single(v1!.Steps);
        Assert.Equal("Démarrage complet", v1.Name);
    }

    [Fact]
    public async Task Le_Rapport_Reste_Rattache_A_La_Version_Qui_A_Tourne()
    {
        var id = await CreerWorkflowAsync("DEM", WorkflowKind.DemarrageComplet);
        await ValiderAsync(id);

        var prep = await _executions.PrepareAsync(id, "op1", "Opération de référence", null, isSimulation: true);
        await _workflows.SaveAsync(id, "Nom radicalement différent", null,
            [EtapeDemarrage("Autre étape", _xpsId, 1)]);

        var execution = await _executions.GetAsync(prep.ExecutionId);

        Assert.NotNull(execution);
        Assert.Equal(1, execution!.WorkflowVersion);
        Assert.Equal("Démarrage complet", execution.WorkflowName);
        Assert.Single(execution.Steps);
        Assert.Equal("Démarrer le Bridge", execution.Steps.First().Name);
    }

    [Fact]
    public async Task Une_Nouvelle_Tentative_Automatique_Sur_Un_Arret_Est_Refusee()
    {
        var id = await CreerWorkflowAsync("ARR", WorkflowKind.ArretComplet);

        var etape = new WorkflowStep
        {
            Name = "Arrêter le Bridge",
            Action = StepAction.Arreter,
            ComponentId = _bridgeId,
            Order = 1,
            AutomaticRetry = true,
            MaxRetries = 3
        };

        var r = await _workflows.SaveAsync(id, "Arrêt complet", null, [etape]);

        Assert.False(r.Succeeded);
        Assert.Contains("arrêt qui échoue doit être compris", r.Error!);
    }

    [Fact]
    public async Task Une_Intervention_Manuelle_Sans_Consigne_Est_Refusee()
    {
        var id = await CreerWorkflowAsync("MAN", WorkflowKind.OperationPartielle);

        var r = await _workflows.SaveAsync(id, "Opération", null,
            [new WorkflowStep { Name = "Faire le nécessaire", Action = StepAction.InterventionManuelle, Order = 1 }]);

        Assert.False(r.Succeeded);
        Assert.Contains("sans dire lequel", r.Error!);
    }

    [Fact]
    public async Task Les_Sequences_Generees_Sont_En_Brouillon()
    {
        var r = await _workflows.GenerateDefaultsAsync(_envId);
        Assert.True(r.Succeeded, r.Error);

        var tous = await _workflows.GetForEnvironmentAsync(_envId);

        // Une sequence deduite d'un referentiel reste une PROPOSITION. La
        // livrer executable sur un ecosysteme jamais vu serait exactement le
        // raccourci que ce projet existe pour eviter.
        Assert.All(tous, w => Assert.Equal(LifecycleStatus.Brouillon, w.Status));
        Assert.Empty(await _workflows.GetRunnableAsync(_envId));
    }

    [Fact]
    public async Task L_Ordre_D_Arret_N_Est_Pas_L_Inverse_De_L_Ordre_De_Demarrage()
    {
        await _workflows.GenerateDefaultsAsync(_envId);

        var tous = await _workflows.GetForEnvironmentAsync(_envId);
        var demarrage = tous.First(w => w.Code == "DEMARRAGE-COMPLET");
        var arret = tous.First(w => w.Code == "ARRET-COMPLET");

        // Au demarrage, le Bridge precede XPS ; a l'arret, XPS precede le
        // Bridge — le dependant lache son prerequis en premier.
        Assert.True(Rang(demarrage, _bridgeId) < Rang(demarrage, _xpsId));
        Assert.True(Rang(arret, _xpsId) < Rang(arret, _bridgeId));

        static int Rang(Workflow w, Guid composantId) =>
            w.Steps.First(s => s.ComponentId == composantId).Order;
    }

    [Fact]
    public async Task Une_Sequence_Generee_Respecte_Le_Graphe_De_Dependances()
    {
        await _workflows.GenerateDefaultsAsync(_envId);

        var tous = await _workflows.GetForEnvironmentAsync(_envId);

        foreach (var workflow in tous)
        {
            var violations = await _validator.ValidateAsync(_envId, workflow.Steps.OrderBy(s => s.Order).ToList());
            Assert.DoesNotContain(violations, v => v.Blocking);
        }
    }

    [Fact]
    public async Task Les_Sequences_Ne_Sont_Pas_Regenerees_Par_Dessus_Les_Existantes()
    {
        await _workflows.GenerateDefaultsAsync(_envId);
        var second = await _workflows.GenerateDefaultsAsync(_envId);

        Assert.False(second.Succeeded);
        Assert.Contains("existent déjà", second.Error!);
    }

    // =======================================================================
    // ORC-04 — Verrou d'environnement (recette AC-12)
    // =======================================================================
    [Fact]
    public async Task Une_Seconde_Operation_Mutative_Est_Refusee()
    {
        var premier = await _locks.AcquireAsync(_envId, Guid.NewGuid(), "op1", "Arrêt complet");
        Assert.True(premier.Succeeded);

        var second = await _locks.AcquireAsync(_envId, Guid.NewGuid(), "op2", "Démarrage complet");

        Assert.False(second.Succeeded);
        Assert.Contains("déjà en cours", second.Error!);
        Assert.Contains("diagnostics en lecture restent possibles", second.Error!);
    }

    [Fact]
    public async Task Un_Verrou_Expire_Est_Repris()
    {
        var executionId = Guid.NewGuid();
        await _locks.AcquireAsync(_envId, executionId, "op1", "Opération abandonnée");

        // Un detenteur qui ne bat plus n'occupe plus l'environnement : sans
        // cette reprise, une panne du serveur applicatif bloquerait la
        // Production jusqu'a intervention en base.
        await using (var db = _factory.CreateDbContext())
        {
            var verrou = await db.EnvironmentLocks.FirstAsync(l => l.EnvironmentId == _envId);
            verrou.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var second = await _locks.AcquireAsync(_envId, Guid.NewGuid(), "op2", "Nouvelle opération");
        Assert.True(second.Succeeded, second.Error);
    }

    [Fact]
    public async Task Une_Simulation_Ne_Prend_Pas_Le_Verrou()
    {
        var id = await CreerWorkflowAsync("DEM", WorkflowKind.DemarrageComplet);
        await ValiderAsync(id);

        var prep = await _executions.PrepareAsync(id, "op1", "Répétition avant intervention", null, isSimulation: true);
        await LancerAsync(prep.ExecutionId);

        Assert.Null(await _locks.GetAsync(_envId));
    }

    [Fact]
    public async Task Le_Verrou_Est_Libere_A_La_Fin()
    {
        var executionId = Guid.NewGuid();
        await _locks.AcquireAsync(_envId, executionId, "op1", "Opération");
        Assert.NotNull(await _locks.GetAsync(_envId));

        await _locks.ReleaseAsync(executionId);
        Assert.Null(await _locks.GetAsync(_envId));
    }

    // =======================================================================
    // ORC-05 — Motif, approbation, contournement
    // =======================================================================
    [Fact]
    public async Task Une_Execution_Sans_Motif_Est_Refusee()
    {
        var id = await CreerWorkflowAsync("DEM", WorkflowKind.DemarrageComplet);
        await ValiderAsync(id);

        var prep = await _executions.PrepareAsync(id, "op1", "   ", null, isSimulation: false);

        Assert.False(prep.Succeeded);
        Assert.Contains("motif est obligatoire", prep.Error!);
    }

    [Fact]
    public async Task Le_Demandeur_Ne_Peut_Pas_Approuver_Sa_Propre_Operation()
    {
        var id = await CreerWorkflowAsync("ARR", WorkflowKind.ArretComplet, approbation: true);
        await ValiderAsync(id);

        var prep = await _executions.PrepareAsync(id, "m.konate", "Maintenance planifiée", "INC-42", isSimulation: false);
        Assert.True(prep.RequiresApproval);

        var refus = await _executions.ApproveAsync(prep.ExecutionId, "m.konate");
        Assert.NotNull(refus);
        Assert.Contains("ne peut pas approuver sa propre", refus!);

        var accord = await _executions.ApproveAsync(prep.ExecutionId, "chef.exploitation");
        Assert.Null(accord);
    }

    [Fact]
    public async Task Une_Operation_En_Attente_D_Approbation_Ne_Se_Lance_Pas()
    {
        var id = await CreerWorkflowAsync("ARR", WorkflowKind.ArretComplet, approbation: true);
        await ValiderAsync(id);

        var prep = await _executions.PrepareAsync(id, "op1", "Maintenance", null, isSimulation: false);
        var erreur = await _executions.StartAsync(prep.ExecutionId, "op1");

        Assert.NotNull(erreur);
        Assert.Contains("approbation", erreur!);
    }

    [Fact]
    public async Task Une_Etape_Non_Contournable_Ne_Peut_Etre_Ignoree_Par_Personne()
    {
        var executionId = await PreparerExecutionAsync();

        await using var db = _factory.CreateDbContext();
        var etape = await db.ExecutionSteps.FirstAsync(s => s.ExecutionId == executionId);
        Assert.False(etape.IsSkippable);

        var erreur = await _executions.SkipStepAsync(etape.Id, "administrateur", "Je suis pressé");

        Assert.NotNull(erreur);
        Assert.Contains("quel que soit le profil", erreur!);
    }

    [Fact]
    public async Task Un_Contournement_Sans_Justification_Est_Refuse()
    {
        var executionId = await PreparerExecutionAsync(contournable: true);

        await using var db = _factory.CreateDbContext();
        var etape = await db.ExecutionSteps.FirstAsync(s => s.ExecutionId == executionId);

        var erreur = await _executions.SkipStepAsync(etape.Id, "op1", "  ");

        Assert.NotNull(erreur);
        Assert.Contains("trou dans la traçabilité", erreur!);
    }

    [Fact]
    public async Task Un_Contournement_Justifie_Enregistre_Qui_Et_Pourquoi()
    {
        var executionId = await PreparerExecutionAsync(contournable: true);

        Guid etapeId;
        await using (var db = _factory.CreateDbContext())
            etapeId = (await db.ExecutionSteps.FirstAsync(s => s.ExecutionId == executionId)).Id;

        var erreur = await _executions.SkipStepAsync(
            etapeId, "m.konate", "Composant déjà arrêté manuellement avant l'opération.");
        Assert.Null(erreur);

        await using var relecture = _factory.CreateDbContext();
        var etape = await relecture.ExecutionSteps.FirstAsync(s => s.Id == etapeId);

        Assert.Equal(ExecutionStepState.Ignore, etape.State);
        Assert.Equal("m.konate", etape.SkippedBy);
        Assert.Contains("déjà arrêté manuellement", etape.SkipReason!);
    }

    [Fact]
    public async Task L_Annulation_Avant_Lancement_Est_Immediate()
    {
        var executionId = await PreparerExecutionAsync();

        var erreur = await _executions.RequestCancelAsync(executionId, "op1");
        Assert.Null(erreur);

        var execution = await _executions.GetAsync(executionId);
        Assert.Equal(ExecutionStatus.Annule, execution!.Status);
        Assert.All(execution.Steps, s => Assert.Equal(ExecutionStepState.Annule, s.State));
    }

    // =======================================================================
    // REF-10 / FR-044 — Refus des séquences invalides
    // =======================================================================
    [Fact]
    public async Task Demarrer_XPS_Avant_Le_Bridge_Est_Refuse()
    {
        var violations = await _validator.ValidateAsync(_envId,
        [
            EtapeDemarrage("Démarrer XPS", _xpsId, 1),
            EtapeDemarrage("Démarrer le Bridge", _bridgeId, 2)
        ]);

        var bloquante = violations.FirstOrDefault(v => v.Blocking);
        Assert.NotNull(bloquante);
        Assert.Contains("XPS", bloquante!.Message);
        Assert.Contains("Bridge", bloquante.Message);
    }

    [Fact]
    public async Task Demarrer_Le_Bridge_Puis_XPS_Est_Accepte()
    {
        var violations = await _validator.ValidateAsync(_envId,
        [
            EtapeDemarrage("Démarrer le Bridge", _bridgeId, 1),
            EtapeDemarrage("Démarrer XPS", _xpsId, 2)
        ]);

        Assert.DoesNotContain(violations, v => v.Blocking);
    }

    [Fact]
    public async Task Arreter_Le_Bridge_Avant_XPS_Est_Refuse()
    {
        // L'ordre d'arret n'est pas l'inverse mecanique de l'ordre de
        // demarrage, mais un composant ne peut pas perdre son prerequis en
        // cours de fonctionnement.
        var violations = await _validator.ValidateAsync(_envId,
        [
            new WorkflowStep { Name = "Arrêter le Bridge", Action = StepAction.Arreter, ComponentId = _bridgeId, Order = 1 },
            new WorkflowStep { Name = "Arrêter XPS", Action = StepAction.Arreter, ComponentId = _xpsId, Order = 2 }
        ]);

        Assert.Contains(violations, v => v.Blocking && v.Message.Contains("privé de son prérequis"));
    }

    [Fact]
    public async Task Un_Prerequis_Absent_De_La_Sequence_Est_Signale_Sans_Bloquer()
    {
        var violations = await _validator.ValidateAsync(_envId,
            [EtapeDemarrage("Démarrer XPS", _xpsId, 1)]);

        var signalement = Assert.Single(violations);
        Assert.False(signalement.Blocking);
        Assert.Contains("n'est pas démarré par cette séquence", signalement.Message);
    }

    [Fact]
    public async Task Deux_Noeuds_Cluster_En_Parallele_Sont_Refuses()
    {
        var e1 = EtapeDemarrage("Démarrer Cluster 1", _cluster1Id, 1);
        var e2 = EtapeDemarrage("Démarrer Cluster 2", _cluster2Id, 2);
        e1.CanRunInParallel = true;
        e2.CanRunInParallel = true;

        var violations = await _validator.ValidateAsync(_envId, [e1, e2]);

        Assert.Contains(violations, v => v.Blocking && v.Message.Contains("UN PAR UN"));
    }

    [Fact]
    public async Task Une_Dependance_Circulaire_Est_Detectee()
    {
        // Bridge dependrait de XPS, qui depend deja du Bridge.
        var message = await _validator.DetectCycleAsync(_envId, _bridgeId, _xpsId);

        Assert.NotNull(message);
        Assert.Contains("cycle", message!);
    }

    [Fact]
    public async Task Une_Dependance_Sur_Soi_Meme_Est_Refusee()
    {
        var message = await _validator.DetectCycleAsync(_envId, _bridgeId, _bridgeId);

        Assert.NotNull(message);
        Assert.Contains("lui-même", message!);
    }

    [Fact]
    public async Task Une_Dependance_Acceptable_Ne_Declenche_Aucun_Cycle()
    {
        var message = await _validator.DetectCycleAsync(_envId, _xpsId, _cluster1Id);
        Assert.Null(message);
    }

    // =======================================================================
    // NFR-002 — Reprise après redémarrage
    // =======================================================================
    [Fact]
    public async Task Une_Etape_En_Vol_Au_Moment_De_L_Arret_Impose_Une_Reconciliation()
    {
        var executionId = await PreparerExecutionAsync();
        await LancerAsync(executionId);

        // On simule un arret brutal PENDANT une etape.
        await using (var db = _factory.CreateDbContext())
        {
            var etape = await db.ExecutionSteps.FirstAsync(s => s.ExecutionId == executionId);
            etape.State = ExecutionStepState.EnCours;
            etape.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        var moteur = new OrchestrationEngine(
            new TestScopeFactory(_factory, _locks), NullLogger<OrchestrationEngine>.Instance);

        await moteur.ReconcileAtStartupAsync(CancellationToken.None);

        var execution = await _executions.GetAsync(executionId);
        Assert.Equal(ExecutionStatus.ReconciliationRequise, execution!.Status);
        Assert.Contains("impossible de savoir si l'action a été émise", execution.Outcome!);
    }

    [Fact]
    public async Task Une_Interruption_Entre_Deux_Etapes_Reprend_Sans_Reconciliation()
    {
        var executionId = await PreparerExecutionAsync();
        await LancerAsync(executionId);

        // Rien n'etait en vol : l'etape precedente etait terminee, la suivante
        // pas encore commencee. La reprise est sure.
        await using (var db = _factory.CreateDbContext())
        {
            var etape = await db.ExecutionSteps.FirstAsync(s => s.ExecutionId == executionId);
            etape.State = ExecutionStepState.Reussi;
            etape.Evidence = "Marqueur reconnu.";
            await db.SaveChangesAsync();
        }

        var moteur = new OrchestrationEngine(
            new TestScopeFactory(_factory, _locks), NullLogger<OrchestrationEngine>.Instance);

        await moteur.ReconcileAtStartupAsync(CancellationToken.None);

        var execution = await _executions.GetAsync(executionId);
        Assert.Equal(ExecutionStatus.EnCours, execution!.Status);
    }

    [Fact]
    public async Task La_Reprise_Apres_Reconciliation_Repart_De_L_Etape_Suspendue()
    {
        var executionId = await PreparerExecutionAsync();
        await LancerAsync(executionId);

        Guid etapeId;
        await using (var db = _factory.CreateDbContext())
        {
            var etape = await db.ExecutionSteps.FirstAsync(s => s.ExecutionId == executionId);
            etape.State = ExecutionStepState.EnCours;
            etapeId = etape.Id;

            var execution = await db.Executions.FirstAsync(x => x.Id == executionId);
            execution.Status = ExecutionStatus.ReconciliationRequise;
            await db.SaveChangesAsync();
        }

        var erreur = await _executions.ResumeAsync(executionId, "m.konate");
        Assert.Null(erreur);

        await using var relecture = _factory.CreateDbContext();
        var reprise = await relecture.ExecutionSteps.FirstAsync(s => s.Id == etapeId);

        Assert.Equal(ExecutionStepState.AVenir, reprise.State);
        Assert.Contains("après constat de l'état réel", reprise.Evidence!);
    }

    // =======================================================================
    // ORC-07 — Pré-check (FR-012)
    // =======================================================================
    [Fact]
    public async Task Une_Execution_Sans_Pre_Check_Ne_Se_Lance_Pas()
    {
        var executionId = await PreparerExecutionAsync();

        var erreur = await _executions.StartAsync(executionId, "op1");

        Assert.NotNull(erreur);
        Assert.Contains("contrôles préalables n'ont pas été passés", erreur!);
    }

    [Fact]
    public async Task Un_Echec_Bloquant_Interdit_Le_Lancement_Sans_Contournement_Possible()
    {
        var executionId = await PreparerExecutionAsync();

        // Le pre-check a tourne, et il a bloque.
        await using (var db = _factory.CreateDbContext())
        {
            var execution = await db.Executions.FirstAsync(x => x.Id == executionId);
            execution.PreflightAt = DateTimeOffset.UtcNow;
            execution.PreflightBlocked = true;
            await db.SaveChangesAsync();
        }

        var erreur = await _executions.StartAsync(executionId, "op1");

        Assert.NotNull(erreur);
        Assert.Contains("ne se contourne pas", erreur!);
    }

    [Fact]
    public async Task Un_Pre_Check_Sans_Echec_Bloquant_Autorise_Le_Lancement()
    {
        var executionId = await PreparerExecutionAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var execution = await db.Executions.FirstAsync(x => x.Id == executionId);
            execution.PreflightAt = DateTimeOffset.UtcNow;
            execution.PreflightBlocked = false;
            await db.SaveChangesAsync();
        }

        Assert.Null(await _executions.StartAsync(executionId, "op1"));
    }

    [Fact]
    public async Task Un_Composant_Non_Pilotable_Fait_Echouer_Le_Pre_Check()
    {
        // Le Bridge repasse en brouillon : le referentiel n'autorise plus
        // aucune commande, quelle que soit l'urgence.
        await using (var db = _factory.CreateDbContext())
        {
            var bridge = await db.Components.FirstAsync(c => c.Id == _bridgeId);
            bridge.Status = LifecycleStatus.Brouillon;
            await db.SaveChangesAsync();
        }

        var executionId = await PreparerExecutionAsync();
        var rapport = await _preflight.RunAsync(executionId);

        Assert.True(rapport.HasBlockingFailure);
        Assert.False(rapport.Cleared);

        var controle = rapport.Checks.First(c => c.Name == "Composants pilotables");
        Assert.Equal(PreflightOutcome.Echec, controle.Outcome);
        Assert.True(controle.IsBlocking);
        Assert.Contains("Bridge", controle.Detail);
    }

    [Fact]
    public async Task Un_Composant_Sans_Marqueur_Produit_Une_Reserve_Et_Non_Un_Blocage()
    {
        var executionId = await PreparerExecutionAsync();
        var rapport = await _preflight.RunAsync(executionId);

        // Interdire l'operation faute de marqueur rendrait l'outil inutilisable
        // sur un site qui n'a pas encore fait son releve. La reserve est dite,
        // elle ne bloque pas.
        var controle = rapport.Checks.First(c => c.Name == "Preuve de démarrage");
        Assert.Equal(PreflightOutcome.Avertissement, controle.Outcome);
        Assert.False(controle.IsBlocking);
    }

    [Fact]
    public async Task Le_Rapport_De_Pre_Check_Est_Conserve_Avec_L_Execution()
    {
        var executionId = await PreparerExecutionAsync();
        await _preflight.RunAsync(executionId);

        await using var db = _factory.CreateDbContext();
        var execution = await db.Executions.AsNoTracking().FirstAsync(x => x.Id == executionId);

        Assert.NotNull(execution.PreflightAt);
        Assert.NotNull(execution.PreflightJson);

        var relu = PreflightService.Relire(execution.PreflightJson);
        Assert.NotEmpty(relu);
    }

    [Fact]
    public async Task Une_Simulation_Ne_Bute_Pas_Sur_Le_Verrou()
    {
        // Verrou pose par une autre operation.
        await _locks.AcquireAsync(_envId, Guid.NewGuid(), "op-autre", "Arrêt complet");

        var id = await CreerWorkflowAsync("SIM", WorkflowKind.DemarrageComplet);
        await ValiderAsync(id);
        var prep = await _executions.PrepareAsync(id, "op1", "Répétition", null, isSimulation: true);

        var rapport = await _preflight.RunAsync(prep.ExecutionId);

        var controle = rapport.Checks.First(c => c.Name == "Verrou d'environnement");
        Assert.Equal(PreflightOutcome.NonApplicable, controle.Outcome);
    }

    // =======================================================================
    // ORC-10 — Opérations ponctuelles (FR-040 à FR-045)
    // =======================================================================
    [Fact]
    public async Task L_Analyse_D_Impact_Nomme_Ce_Qui_Tombe_Avec_Le_Composant_Arrete()
    {
        // XPS depend du Bridge : arreter le Bridge rend XPS inoperant.
        var impact = await _adhoc.AnalyseImpactAsync(_envId, [_bridgeId], StepAction.Arreter);

        Assert.Single(impact.Targeted);
        Assert.True(impact.HasCollateral);
        Assert.Contains(impact.Collateral, c => c.Id == _xpsId);
    }

    [Fact]
    public async Task L_Analyse_D_Impact_Signale_Un_Prerequis_Absent_De_La_Selection()
    {
        var impact = await _adhoc.AnalyseImpactAsync(_envId, [_xpsId], StepAction.Demarrer);

        Assert.Contains(impact.MissingPrerequisites, c => c.Id == _bridgeId);
    }

    [Fact]
    public async Task Une_Operation_Ponctuelle_Sur_Un_Composant_Non_Pilotable_Est_Refusee()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var bridge = await db.Components.FirstAsync(c => c.Id == _bridgeId);
            bridge.ControlMode = ControlMode.SuperviseSeulement;
            await db.SaveChangesAsync();
        }

        var r = await _adhoc.BuildAsync(_envId, [_bridgeId], StepAction.Arreter, AdHocShape.Unitaire, "op1");

        Assert.False(r.Succeeded);
        Assert.Contains("pas pilotables", r.Error!);
    }

    [Fact]
    public async Task Un_Redemarrage_Tournant_Traite_Les_Noeuds_Un_Par_Un()
    {
        var r = await _adhoc.BuildAsync(
            _envId, [_cluster1Id, _cluster2Id], StepAction.Redemarrer, AdHocShape.RollingRestart, "op1");

        Assert.True(r.Succeeded, r.Error);

        var workflow = await _workflows.GetAsync(r.WorkflowId);
        var etapes = workflow!.Steps.OrderBy(s => s.Order).ToList();

        // Arret, demarrage, confirmation pour chacun : le second noeud n'est
        // pas touche avant que le premier soit confirme reparti.
        Assert.Equal(6, etapes.Count);
        Assert.Equal(_cluster1Id, etapes[0].ComponentId);
        Assert.Equal(StepAction.Arreter, etapes[0].Action);
        Assert.Equal(StepAction.Demarrer, etapes[1].Action);
        Assert.Equal(StepAction.Verifier, etapes[2].Action);
        Assert.Equal(_cluster2Id, etapes[3].ComponentId);

        Assert.All(etapes, e => Assert.False(e.CanRunInParallel));
    }

    [Fact]
    public async Task Une_Operation_Ponctuelle_Passe_Par_Le_Moteur_Comme_Les_Autres()
    {
        var r = await _adhoc.BuildAsync(_envId, [_bridgeId], StepAction.Arreter, AdHocShape.Unitaire, "op1");
        Assert.True(r.Succeeded, r.Error);

        // Elle est lancable, donc soumise au verrou, au pre-check et au moteur.
        var prep = await _executions.PrepareAsync(r.WorkflowId, "op1", "Maintenance ciblée", "INC-7", false);
        Assert.True(prep.Succeeded, prep.Error);

        // Et le pre-check reste infranchissable.
        Assert.NotNull(await _executions.StartAsync(prep.ExecutionId, "op1"));
    }

    // =======================================================================
    // ORC-11 — Rapport d'exécution (FR-028, AC-14)
    // =======================================================================
    [Fact]
    public async Task Le_Rapport_Nomme_Qui_A_Contourne_Une_Etape_Et_Pourquoi()
    {
        var executionId = await PreparerExecutionAsync(contournable: true);

        Guid etapeId;
        await using (var db = _factory.CreateDbContext())
            etapeId = (await db.ExecutionSteps.FirstAsync(s => s.ExecutionId == executionId)).Id;

        await _executions.SkipStepAsync(etapeId, "m.konate", "Composant déjà arrêté manuellement.");

        var rapport = await _report.BuildMarkdownAsync(executionId);

        Assert.NotNull(rapport);
        Assert.Contains("Contournée par m.konate", rapport!);
        Assert.Contains("déjà arrêté manuellement", rapport);
        Assert.Contains("1 contournée(s)", rapport);
    }

    [Fact]
    public async Task Le_Rapport_Distingue_Une_Etape_Prouvee_D_Une_Etape_A_Confirmer()
    {
        var executionId = await PreparerExecutionAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var etape = await db.ExecutionSteps.FirstAsync(s => s.ExecutionId == executionId);
            etape.State = ExecutionStepState.Avertissement;
            etape.Evidence = "Service Running, aucun marqueur configuré : à confirmer.";
            await db.SaveChangesAsync();
        }

        var rapport = await _report.BuildMarkdownAsync(executionId);

        Assert.Contains("À confirmer", rapport!);
        Assert.Contains("sans que le résultat ait pu être prouvé", rapport);
    }

    [Fact]
    public async Task Le_Rapport_Dit_Qu_Une_Sequence_Interrompue_N_Est_Pas_Defaite()
    {
        var executionId = await PreparerExecutionAsync();
        await _executions.RequestCancelAsync(executionId, "op1");

        var rapport = await _report.BuildMarkdownAsync(executionId);

        Assert.Contains("Séquence incomplète", rapport!);
        Assert.Contains("ne sont PAS défaites", rapport);
    }

    [Fact]
    public async Task Le_Rapport_Reprend_Les_Controles_Prealables()
    {
        var executionId = await PreparerExecutionAsync();
        await _preflight.RunAsync(executionId);

        var rapport = await _report.BuildMarkdownAsync(executionId);

        Assert.Contains("Contrôles préalables", rapport!);
        Assert.Contains("Composants pilotables", rapport);
    }

    // =======================================================================
    // Aides
    // =======================================================================
    private static WorkflowStep EtapeDemarrage(string nom, Guid composantId, int ordre) => new()
    {
        Name = nom,
        Action = StepAction.Demarrer,
        ComponentId = composantId,
        Order = ordre
    };

    private async Task<Guid> CreerWorkflowAsync(
        string code, WorkflowKind nature, bool approbation = false)
    {
        var workflow = new Workflow
        {
            EnvironmentId = _envId,
            Code = code,
            Name = nature == WorkflowKind.ArretComplet ? "Arrêt complet" : "Démarrage complet",
            Kind = nature,
            RequiresApproval = approbation
        };

        var id = await _workflows.CreateAsync(workflow);

        await using var db = _factory.CreateDbContext();
        db.WorkflowSteps.Add(new WorkflowStep
        {
            WorkflowId = id,
            Name = "Démarrer le Bridge",
            Action = nature == WorkflowKind.ArretComplet ? StepAction.Arreter : StepAction.Demarrer,
            ComponentId = _bridgeId,
            Order = 1
        });
        await db.SaveChangesAsync();

        return id;
    }

    /// <summary>
    /// Passe le pré-check puis lance. Depuis le sprint 5, aucune exécution ne
    /// démarre sans contrôles préalables — les tests doivent suivre le même
    /// chemin que l'application, sinon ils ne prouvent plus rien d'elle.
    /// </summary>
    private async Task LancerAsync(Guid executionId, string acteur = "op1")
    {
        var rapport = await _preflight.RunAsync(executionId);
        Assert.False(rapport.HasBlockingFailure, rapport.Checks
            .Where(c => c.Outcome == PreflightOutcome.Echec)
            .Select(c => c.Detail)
            .FirstOrDefault());

        Assert.Null(await _executions.StartAsync(executionId, acteur));
    }

    private async Task ValiderAsync(Guid workflowId)
    {
        var erreur = await _workflows.ChangeStatusAsync(workflowId, LifecycleStatus.Valide);
        Assert.Null(erreur);
    }

    private async Task<Guid> PreparerExecutionAsync(bool contournable = false)
    {
        var id = await CreerWorkflowAsync($"W{Guid.NewGuid():N}"[..8], WorkflowKind.DemarrageComplet);

        if (contournable)
        {
            await using var db = _factory.CreateDbContext();
            var etape = await db.WorkflowSteps.FirstAsync(s => s.WorkflowId == id);
            etape.IsSkippable = true;
            await db.SaveChangesAsync();
        }

        await ValiderAsync(id);

        var prep = await _executions.PrepareAsync(id, "op1", "Test", null, isSimulation: false);
        Assert.True(prep.Succeeded, prep.Error);
        return prep.ExecutionId;
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }

    /// <summary>
    /// Connecteur qui ne joint rien. Les composants de ces tests n'ont pas de
    /// serveur rattaché : le pré-check n'a donc aucune machine à contacter, et
    /// cette doublure ne sert qu'à satisfaire le constructeur.
    /// </summary>
    private sealed class ConnecteurMuet : IN4Connector
    {
        private static ConnectorResult<T> Injoignable<T>() =>
            ConnectorResult<T>.Fail(ConnectorFailure.Injoignable, "Aucun serveur dans ce test.", TimeSpan.Zero);

        public Task<ConnectorResult<string>> PingAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<string>());

        public Task<ConnectorResult<ServiceSnapshot>> GetServiceAsync(ConnectorTarget t, string n, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<ServiceSnapshot>());

        public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> GetServicesAsync(ConnectorTarget t, IReadOnlyCollection<string> n, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<IReadOnlyList<ServiceSnapshot>>());

        public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> ListServicesAsync(ConnectorTarget t, IReadOnlyCollection<string> m, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<IReadOnlyList<ServiceSnapshot>>());

        public Task<ConnectorResult<SystemSnapshot>> GetSystemAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<SystemSnapshot>());

        public Task<ConnectorResult<LogDelta>> ReadLogDeltaAsync(ConnectorTarget t, string p, long o, int m = 262144, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<LogDelta>());

        public Task<ConnectorResult<LogFileInfo>> ResolveLogAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<LogFileInfo>());

        public Task<ConnectorResult<ServiceSnapshot>> ControlServiceAsync(ConnectorTarget t, string n, ServiceControlAction a, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<ServiceSnapshot>());
    }

    /// <summary>
    /// Fabrique de portées minimale : le moteur ouvre les siennes, il faut donc
    /// lui en fournir une capable de résoudre ce qu'il demande.
    /// </summary>
    private sealed class TestScopeFactory(
        IDbContextFactory<N4SentinelDbContext> factory, EnvironmentLockService locks)
        : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory,
          Microsoft.Extensions.DependencyInjection.IServiceScope,
          IServiceProvider
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IDbContextFactory<N4SentinelDbContext>)) return factory;
            if (serviceType == typeof(EnvironmentLockService)) return locks;
            return null;
        }
    }
}
