using Xunit;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Tests;

/// <summary>
/// Recette contre le simulateur — les sept scénarios du sprint 5.
///
/// CE QUI EST RÉEL ICI, ET CE QUI NE L'EST PAS.
///
/// Réel : les journaux sont de VRAIS fichiers sur le disque, lus par le VRAI
/// connecteur PowerShell en session locale — donc avec le partage
/// lecture/écriture, la résolution des chemins génériques et la détection de
/// rotation exactement telle qu'elle tournera en production. Le moteur
/// d'orchestration, le pré-check, les garde-fous de séquence, la base et les
/// workflows sont ceux de l'application.
///
/// Simulé : le gestionnaire de services Windows. Créer de vrais services
/// exigerait une console élevée, et n'ajouterait rien à ce que ces scénarios
/// éprouvent — la preuve de démarrage ne vient pas du service, elle vient du
/// journal. La doublure écrit dans les vrais fichiers, avec des délais, comme
/// le ferait une JVM N4.
///
/// Ces tests ne remplacent pas une recette contre un vrai N4 avant mise en
/// production. Ils remplacent la dépendance à un vrai N4 pendant le
/// développement.
/// </summary>
public sealed class RecetteSimulateurTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _racine = string.Empty;
    private string _keyPath = string.Empty;

    private TestDbContextFactory _factory = null!;
    private SimulateurConnector _connecteur = null!;
    private WorkflowService _workflows = null!;
    private ExecutionService _executions = null!;
    private EnvironmentLockService _verrous = null!;
    private PreflightService _precheck = null!;
    private StepExecutor _executeur = null!;
    private OrchestrationEngine _moteur = null!;
    private ExecutionReportService _rapports = null!;
    private SupervisionService _supervision = null!;

    private Guid _envId;
    private readonly Dictionary<string, Guid> _composants = [];

    // -----------------------------------------------------------------------
    // Mise en place
    // -----------------------------------------------------------------------
    public async Task InitializeAsync()
    {
        TestConnectionHelper.SkipIfUnavailable();
        var cs = TestConnectionHelper.BuildDatabaseConnectionString(_databaseName);

        _factory = new TestDbContextFactory(TestDbContextOptions.Builder(cs).Options);

        await using (var db = _factory.CreateDbContext())
            await db.Database.EnsureCreatedAsync();

        _racine = Path.Combine(Path.GetTempPath(), $"n4-recette-{Guid.NewGuid():N}");
        _keyPath = Path.Combine(Path.GetTempPath(), $"n4-cles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keyPath);

        SimulateurConnector.FixerRacine(_racine);

        await PreparerReferentielAsync();

        var store = new CredentialStore(_factory,
            DataProtectionProvider.Create(new DirectoryInfo(_keyPath)),
            NullLogger<CredentialStore>.Instance);

        var cibles = new ConnectorTargetFactory(_factory, store, NullLogger<ConnectorTargetFactory>.Instance);

        // Le VRAI connecteur PowerShell pour tout ce qui touche aux journaux ;
        // le gestionnaire de services est seul simule.
        _connecteur = new SimulateurConnector(
            new PowerShellConnector(NullLogger<PowerShellConnector>.Instance));

        var supervision = new SupervisionService(_factory, cibles, _connecteur,
            NullLogger<SupervisionService>.Instance);
        _supervision = supervision;

        _verrous = new EnvironmentLockService(_factory, NullLogger<EnvironmentLockService>.Instance);
        _workflows = new WorkflowService(_factory, NullLogger<WorkflowService>.Instance, new AuditWriter(_factory));
        _rapports = new ExecutionReportService(_factory);

        _executeur = new StepExecutor(_factory, cibles, _connecteur, supervision,
            NullLogger<StepExecutor>.Instance);

        _precheck = new PreflightService(_factory, cibles, _connecteur, _verrous,
            new SequenceValidator(_factory), supervision,
            new CenterContinuityService(_factory, supervision),
            new N4Sentinel.Infrastructure.Security.EnvironmentAccessService(
                _factory,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                NullLogger<N4Sentinel.Infrastructure.Security.EnvironmentAccessService>.Instance),
            NullLogger<PreflightService>.Instance);

        _moteur = new OrchestrationEngine(
            new PorteeDeTest(_factory, _verrous, _executeur, supervision),
            new N4Sentinel.Infrastructure.Observability.MetricsService(),
            NullLogger<OrchestrationEngine>.Instance);

        _executions = new ExecutionService(_factory, _verrous, new AuditWriter(_factory),
            NullLogger<ExecutionService>.Instance, _moteur, supervision: supervision);
    }

    /// <summary>
    /// Référentiel minimal mais fidèle : deux nœuds Cluster, le Center, le
    /// Bridge et XPS, avec la dépendance N4 que le cahier des charges cite —
    /// XPS ne démarre pas sans Bridge.
    /// </summary>
    private async Task PreparerReferentielAsync()
    {
        await using var db = _factory.CreateDbContext();

        var env = new N4Environment
        {
            Code = "SIM",
            Name = "Simulateur de recette",
            Kind = EnvironmentKind.Test,
            Status = LifecycleStatus.Actif
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync();
        _envId = env.Id;

        var serveur = new N4Server
        {
            EnvironmentId = _envId,
            HostName = Environment.MachineName,
            Status = LifecycleStatus.Actif
        };
        db.Servers.Add(serveur);
        await db.SaveChangesAsync();

        foreach (var d in Definitions())
        {
            var dossier = Path.Combine(_racine, d.Dossier);
            Directory.CreateDirectory(dossier);

            var composant = new N4Component
            {
                EnvironmentId = _envId,
                ServerId = serveur.Id,
                LogicalName = d.Nom,
                Role = d.Role,
                WindowsServiceName = d.Service,
                StartOrder = d.Ordre,
                ControlMode = ControlMode.Pilotable,
                Status = LifecycleStatus.Valide,
                Readiness = new ReadinessProfile
                {
                    LogPath = Path.Combine(dossier, d.Journal),
                    ReadyPatterns = [d.Marqueur],
                    ErrorPatterns = ["FATAL", "SEVERE", @"Startup failed"],
                    ServiceRunningTimeoutSeconds = 20,
                    LogReadyTimeoutSeconds = 25,
                    StopTimeoutSeconds = 12,
                    PollIntervalSeconds = 1,
                    ProgressEverySeconds = 5
                }
            };

            db.Components.Add(composant);
            await db.SaveChangesAsync();
            _composants[d.Nom] = composant.Id;
        }

        // XPS depend du Bridge : la dependance N4 explicitement citee (FR-044).
        db.ComponentDependencies.Add(new ComponentDependency
        {
            ComponentId = _composants["XPS"],
            DependsOnComponentId = _composants["Bridge"],
            Kind = DependencyKind.RequisAuDemarrage
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Composants du simulateur, avec leurs vrais marqueurs Navis.</summary>
    private static (string Nom, string Service, ComponentRole Role, int Ordre,
                    string Dossier, string Journal, string Marqueur)[] Definitions() =>
    [
        ("Cluster 1", "N4Sim Cluster Node 1", ComponentRole.ClusterNode, 1,
            @"Navis\cluster1\logs", "navis-apex.log", @"Web tier servlet 'action' initialized"),
        ("Cluster 2", "N4Sim Cluster Node 2", ComponentRole.ClusterNode, 2,
            @"Navis\cluster2\logs", "navis-apex.log", @"Web tier servlet 'action' initialized"),
        ("Center", "N4Sim Center Node", ComponentRole.CenterNode, 3,
            @"Navis\center\logs", "navis-apex.log", @"Web tier servlet 'action' initialized"),
        ("Bridge", "N4Sim XPS Bridge Daemon", ComponentRole.BridgeDaemon, 4,
            @"Navis\bridge\logs", "navis-bridged.log", @"bridge is ACTIVE"),
        ("XPS", "N4Sim XPS Service", ComponentRole.Xps, 5,
            @"Navis\xps\log", "xps_*.log", @"XPS initialization complete")
    ];

    public async Task DisposeAsync()
    {
        foreach (var d in new[] { _racine, _keyPath })
            if (Directory.Exists(d)) try { Directory.Delete(d, true); } catch { }

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
    // Scénario 1 — Simulation : aucune commande n'est émise (AC-01)
    // =======================================================================
    [SkippableFact]
    public async Task Scenario_1_Une_Simulation_N_Emet_Aucune_Commande()
    {
        var workflowId = await CreerWorkflowAsync("SIM-DEM", WorkflowKind.DemarrageComplet,
            [("Cluster 1", StepAction.Demarrer), ("Center", StepAction.Demarrer)]);

        var execution = await PreparerEtLancerAsync(workflowId, simulation: true);
        await DeroulerAsync(execution);

        var fin = await _executions.GetAsync(execution);

        Assert.Equal(ExecutionStatus.TermineSucces, fin!.Status);
        Assert.All(fin.Steps, e => Assert.Contains("[Simulation]", e.Evidence ?? string.Empty));

        // LE POINT DE LA SIMULATION : rien n'a bouge.
        Assert.Empty(_connecteur.CommandesEmises);
        await AttendreLiberationDuVerrouAsync();
    }

    // =======================================================================
    // Scénario 2 — Démarrage nominal, prouvé par le journal
    // =======================================================================
    [SkippableFact]
    public async Task Scenario_2_Demarrage_Nominal_Prouve_Par_Le_Journal()
    {
        var workflowId = await CreerWorkflowAsync("SIM-NOM", WorkflowKind.DemarrageComplet,
            [("Cluster 1", StepAction.Demarrer), ("Cluster 2", StepAction.Demarrer),
             ("Center", StepAction.Demarrer)]);

        var execution = await PreparerEtLancerAsync(workflowId);
        await DeroulerAsync(execution);

        var fin = await _executions.GetAsync(execution);

        Assert.Equal(ExecutionStatus.TermineSucces, fin!.Status);
        Assert.All(fin.Steps, e => Assert.Equal(ExecutionStepState.Reussi, e.State));

        // La preuve cite le marqueur reellement lu dans le fichier.
        Assert.All(fin.Steps, e => Assert.Contains("Marqueur d'initialisation reconnu", e.Evidence!));
        Assert.Contains(fin.Steps, e => e.Evidence!.Contains("Web tier servlet 'action' initialized"));

        // Le verrou est rendu a la fin.
        await AttendreLiberationDuVerrouAsync();
    }

    // =======================================================================
    // Scénario 3 — Le marqueur du démarrage PRÉCÉDENT ne compte pas
    // =======================================================================
    [SkippableFact]
    public async Task Scenario_3_Le_Marqueur_Du_Demarrage_Precedent_Ne_Vaut_Pas_Preuve()
    {
        // LE SCENARIO QUI JUSTIFIE TOUT LE PROJET. Le journal contient deja le
        // marqueur d'un demarrage passe. Un composant qui n'ecrit plus rien ne
        // doit PAS etre declare operationnel a cause de cette vieille ligne.
        var journal = Journal("Cluster 1");
        await File.AppendAllTextAsync(journal,
            $"{DateTime.Now.AddHours(-3):yyyy-MM-dd HH:mm:ss,fff} INFO  [main] "
            + "c.n.a.WebTier - Web tier servlet 'action' initialized in 45000 ms\n");

        _connecteur.Muet("N4Sim Cluster Node 1");

        var workflowId = await CreerWorkflowAsync("SIM-VIEUX", WorkflowKind.DemarrageComplet,
            [("Cluster 1", StepAction.Demarrer)]);

        var execution = await PreparerEtLancerAsync(workflowId);
        await DeroulerAsync(execution);

        var fin = await _executions.GetAsync(execution);
        var etape = fin!.Steps.First();

        Assert.Equal(ExecutionStatus.Echec, fin.Status);
        Assert.Equal(ExecutionStepState.Echec, etape.State);
        Assert.Contains("Aucun marqueur d'initialisation", etape.Error!);
        Assert.Contains("n'est PAS prouvé", etape.Error!);
    }

    // =======================================================================
    // Scénario 4 — XPS refuse de démarrer sans Bridge prouvé (FR-044)
    // =======================================================================
    [SkippableFact]
    public async Task Scenario_4_XPS_Refuse_De_Demarrer_Sans_Bridge_Prouve()
    {
        var workflowId = await CreerWorkflowAsync("SIM-XPS", WorkflowKind.OperationPartielle,
            [("XPS", StepAction.Demarrer)]);

        var execution = await PreparerEtLancerAsync(workflowId);
        await DeroulerAsync(execution);

        var fin = await _executions.GetAsync(execution);
        var etape = fin!.Steps.First();

        Assert.Equal(ExecutionStatus.Echec, fin.Status);
        Assert.Contains("dépend de", etape.Error!);
        Assert.Contains("Bridge", etape.Error!);

        // Aucune commande n'a ete emise vers XPS : le barrage tombe AVANT.
        Assert.DoesNotContain(_connecteur.CommandesEmises,
            c => c.Contains("XPS Service") && c.StartsWith("Demarrer"));
    }

    [SkippableFact]
    public async Task Scenario_4bis_XPS_Demarre_Une_Fois_Le_Bridge_Prouve()
    {
        var workflowId = await CreerWorkflowAsync("SIM-SEQ", WorkflowKind.OperationPartielle,
            [("Bridge", StepAction.Demarrer), ("XPS", StepAction.Demarrer)]);

        var execution = await PreparerEtLancerAsync(workflowId);
        await DeroulerAsync(execution);

        var fin = await _executions.GetAsync(execution);

        Assert.Equal(ExecutionStatus.TermineSucces, fin!.Status);
        Assert.Contains(fin.Steps, e => e.Evidence!.Contains("bridge is ACTIVE"));
        Assert.Contains(fin.Steps, e => e.Evidence!.Contains("XPS initialization complete"));
    }

    // =======================================================================
    // Scénario 5 — Arrêt complet
    // =======================================================================
    [SkippableFact]
    public async Task Scenario_5_Arret_Complet_Suit_Sa_Propre_Sequence()
    {
        _connecteur.Demarre("N4Sim XPS Bridge Daemon", "N4Sim Center Node", "N4Sim Cluster Node 1");

        var workflowId = await CreerWorkflowAsync("SIM-ARR", WorkflowKind.ArretComplet,
            [("Bridge", StepAction.Arreter), ("Center", StepAction.Arreter),
             ("Cluster 1", StepAction.Arreter)]);

        var execution = await PreparerEtLancerAsync(workflowId);
        await DeroulerAsync(execution);

        var fin = await _executions.GetAsync(execution);

        Assert.Equal(ExecutionStatus.TermineSucces, fin!.Status);

        // L'ordre d'arret est celui du workflow, pas l'inverse du demarrage :
        // le Bridge tombe avant le Center.
        var arrets = _connecteur.CommandesEmises.Where(c => c.StartsWith("Arreter")).ToList();
        Assert.Equal(3, arrets.Count);
        Assert.Contains("Bridge", arrets[0]);
        Assert.Contains("Center", arrets[1]);
    }

    // =======================================================================
    // FR-025 — Annulation sûre : recollecte de l'état réel
    // =======================================================================
    [SkippableFact]
    public async Task Annulation_Apres_Une_Etape_Executee_Recollecte_L_Etat_Reel()
    {
        var workflowId = await CreerWorkflowAsync("SIM-ANN", WorkflowKind.OperationPartielle,
            [("Bridge", StepAction.Demarrer), ("Center", StepAction.Demarrer)]);

        var executionId = await PreparerEtLancerAsync(workflowId);

        using (var jeton = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            await _moteur.PickUpAsync(jeton.Token);

            while (!jeton.IsCancellationRequested)
            {
                var courant = await _executions.GetAsync(executionId, CancellationToken.None);
                var etapeBridge = courant!.Steps.First(s => s.Name.Contains("Bridge"));
                if (etapeBridge.State == ExecutionStepState.Reussi) break;
                await Task.Delay(200, jeton.Token);
            }
        }

        Assert.Null(await _executions.RequestCancelAsync(executionId, "recette"));

        await DeroulerAsync(executionId);

        var fin = await _executions.GetAsync(executionId);
        Assert.Equal(ExecutionStatus.Annule, fin!.Status);

        // FR-025 : l'état réel du composant réellement démarré (Bridge) a été
        // recollecté — ce n'est pas une phrase générique sur un état
        // "intermédiaire", c'est un constat nommé.
        Assert.NotNull(fin.PostCancellationReport);
        Assert.Contains("Bridge", fin.PostCancellationReport!);
        Assert.False(fin.RequiresManualInterventionAfterCancel);
        Assert.Contains("État des composants touchés confirmé stable", fin.Outcome!);
    }

    // =======================================================================
    // FR-024 — Nouvelle tentative : recollecte de l'état réel
    // =======================================================================
    [SkippableFact]
    public async Task RetryStepAsync_Annule_La_Reprise_Si_L_Etat_Reel_Montre_Que_L_Action_A_Deja_Reussi()
    {
        var marqueur = Journal("Bridge");
        await File.AppendAllTextAsync(marqueur,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} INFO  [main] c.n.b.Bridge - bridge is ACTIVE\n");
        _connecteur.Demarre("N4Sim XPS Bridge Daemon");

        var workflowId = await CreerWorkflowAsync("SIM-RETRY", WorkflowKind.OperationPartielle,
            [("Bridge", StepAction.Demarrer)]);

        // Préparée mais JAMAIS lancée (pas de StartAsync) : le moteur réel ne
        // doit jamais toucher cette exécution, pour ne pas entrer en course
        // avec la falsification manuelle de l'état ci-dessous.
        var prep = await _executions.PrepareAsync(
            workflowId, "recette", "Test FR-024", "REC-02", isSimulation: false);
        Assert.True(prep.Succeeded, prep.Error);
        var executionId = prep.ExecutionId;

        Guid etapeId;
        await using (var db = _factory.CreateDbContext())
        {
            var etape = await db.ExecutionSteps.FirstAsync(s => s.ExecutionId == executionId);
            etape.State = ExecutionStepState.Bloque;
            etape.Error = "Erreur de commande rapportée (fausse alerte à vérifier).";
            await db.SaveChangesAsync();
            etapeId = etape.Id;
        }

        var erreur = await _executions.RetryStepAsync(etapeId, "recette");
        Assert.Null(erreur);

        await using var relecture = _factory.CreateDbContext();
        var apres = await relecture.ExecutionSteps.FirstAsync(s => s.Id == etapeId);

        // L'état réel recollecté montre que le composant est déjà démarré :
        // la reprise est annulée, pas rejouée aveuglément.
        Assert.Equal(ExecutionStepState.Avertissement, apres.State);
        Assert.Contains("déjà Disponible", apres.Evidence!);
        Assert.DoesNotContain(_connecteur.CommandesEmises, c => c.StartsWith("Demarrer"));
    }

    // =======================================================================
    // Scénario 6 — Le service était déjà à l'arrêt
    // =======================================================================
    [SkippableFact]
    public async Task Scenario_6_Un_Service_Deja_Arrete_N_Est_Pas_Un_Echec()
    {
        // Le composant est deja arrete. Ce n'est pas une anomalie : c'est
        // l'etat recherche. La sequence doit aboutir sans drame.
        var workflowId = await CreerWorkflowAsync("SIM-DEJA", WorkflowKind.OperationUnitaire,
            [("Center", StepAction.Arreter)]);

        var execution = await PreparerEtLancerAsync(workflowId);
        await DeroulerAsync(execution);

        var fin = await _executions.GetAsync(execution);

        Assert.Equal(ExecutionStatus.TermineSucces, fin!.Status);
        Assert.Contains("arrêté", fin.Steps.First().Evidence!);
    }

    // =======================================================================
    // Scénario 7 — Service bloqué en StopPending
    // =======================================================================
    [SkippableFact]
    public async Task Scenario_7_Un_Service_Bloque_En_StopPending_Est_Nomme()
    {
        // Cas documente du Standby Center Node : le processus ne rend jamais la
        // main. L'application doit le NOMMER plutot que d'afficher un delai
        // depasse anonyme, et surtout ne pas tuer le processus d'elle-meme.
        _connecteur.Demarre("N4Sim Center Node");
        _connecteur.BloqueEnArret("N4Sim Center Node");

        var workflowId = await CreerWorkflowAsync("SIM-BLOQ", WorkflowKind.OperationUnitaire,
            [("Center", StepAction.Arreter)]);

        var execution = await PreparerEtLancerAsync(workflowId);
        await DeroulerAsync(execution);

        var fin = await _executions.GetAsync(execution);
        var etape = fin!.Steps.First();

        Assert.Equal(ExecutionStatus.Echec, fin.Status);
        Assert.Contains("StopPending", etape.Error!);
        Assert.Contains("Standby Center Node", etape.Error!);
        Assert.Contains("doit être décidé par un opérateur", etape.Error!);

        // Le rapport transmet la cause telle quelle.
        var rapport = await _rapports.BuildMarkdownAsync(execution);
        Assert.Contains("StopPending", rapport!);
    }

    // =======================================================================
    // Bonus — rotation du journal en cours d'attente
    // =======================================================================
    [SkippableFact]
    public async Task Scenario_8_Une_Rotation_De_Journal_Ne_Perd_Pas_La_Preuve()
    {
        // Le journal est recree pendant l'attente, comme le fait XPS a chaque
        // demarrage. La lecture incrementale doit repartir de zero au lieu de
        // rester bloquee sur un offset devenu faux.
        _connecteur.RotationAuDemarrage("N4Sim Cluster Node 2");

        var journal = Journal("Cluster 2");
        await File.AppendAllTextAsync(journal, new string('x', 4000) + "\n");

        var workflowId = await CreerWorkflowAsync("SIM-ROT", WorkflowKind.OperationUnitaire,
            [("Cluster 2", StepAction.Demarrer)]);

        var execution = await PreparerEtLancerAsync(workflowId);
        await DeroulerAsync(execution);

        var fin = await _executions.GetAsync(execution);

        Assert.Equal(ExecutionStatus.TermineSucces, fin!.Status);
        Assert.Contains("Marqueur d'initialisation reconnu", fin.Steps.First().Evidence!);
    }

    // =======================================================================
    // FR-023 — Exécution réellement parallèle
    // =======================================================================
    [Fact(DisplayName = "Deux étapes indépendantes déclarées parallélisables s'exécutent en même temps, pas l'une après l'autre")]
    public async Task Deux_Etapes_Parallelisables_S_Executent_Simultanement()
    {
        // Deux temporisations sans composant : aucune commande n'est emise,
        // seul le VRAI delai (StepExecutor.AttendreAsync) compte. Si le moteur
        // les lancait toujours l'une apres l'autre, le total avoisinerait 2×3 s ;
        // lancees ensemble via Task.WhenAll, il avoisine 3 s.
        var workflow = new Workflow
        {
            EnvironmentId = _envId,
            Code = $"PAR{Guid.NewGuid():N}"[..8],
            Name = "Deux temporisations indépendantes",
            Kind = WorkflowKind.OperationPartielle
        };
        var workflowId = await _workflows.CreateAsync(workflow);

        await using (var db = _factory.CreateDbContext())
        {
            db.WorkflowSteps.AddRange(
                new WorkflowStep
                {
                    WorkflowId = workflowId, Order = 1, Name = "Temporisation A",
                    Action = StepAction.Attendre, ExpectedSeconds = 3, TimeoutSeconds = 30,
                    CanRunInParallel = true, FailurePolicy = StepFailurePolicy.Bloquer
                },
                new WorkflowStep
                {
                    WorkflowId = workflowId, Order = 2, Name = "Temporisation B",
                    Action = StepAction.Attendre, ExpectedSeconds = 3, TimeoutSeconds = 30,
                    CanRunInParallel = true, FailurePolicy = StepFailurePolicy.Bloquer
                });
            await db.SaveChangesAsync();
        }

        Assert.Null(await _workflows.ChangeStatusAsync(workflowId, LifecycleStatus.Valide, "test"));

        var execution = await PreparerEtLancerAsync(workflowId);

        var chrono = System.Diagnostics.Stopwatch.StartNew();
        await DeroulerAsync(execution);
        chrono.Stop();

        var fin = await _executions.GetAsync(execution);
        Assert.Equal(ExecutionStatus.TermineSucces, fin!.Status);
        Assert.All(fin.Steps, s => Assert.Equal(ExecutionStepState.Reussi, s.State));

        // Marge large (5 s pour deux etapes de 3 s) : loin des ~6 s qu'un
        // sequencement aurait produit, sans rendre le test fragile sous charge.
        Assert.True(chrono.Elapsed < TimeSpan.FromSeconds(5),
            $"Les deux étapes ont pris {chrono.Elapsed.TotalSeconds:F1} s au total : "
            + "elles semblent s'être exécutées l'une après l'autre, pas en parallèle.");
    }

    [Fact(DisplayName = "Une étape non déclarée parallélisable attend son tour même si celle qui la précède l'est")]
    public async Task Une_Etape_Non_Parallelisable_N_Est_Jamais_Groupee()
    {
        // La contiguïté s'arrête a la premiere etape non marquee : meme si
        // A et C sont toutes deux parallelisables, B au milieu ne l'est pas,
        // et personne apres B ne doit etre lance avec A.
        var workflow = new Workflow
        {
            EnvironmentId = _envId,
            Code = $"SEQ{Guid.NewGuid():N}"[..8],
            Name = "Parallélisable puis pas",
            Kind = WorkflowKind.OperationPartielle
        };
        var workflowId = await _workflows.CreateAsync(workflow);

        await using (var db = _factory.CreateDbContext())
        {
            db.WorkflowSteps.AddRange(
                new WorkflowStep
                {
                    WorkflowId = workflowId, Order = 1, Name = "A",
                    Action = StepAction.Attendre, ExpectedSeconds = 1, TimeoutSeconds = 30,
                    CanRunInParallel = true, FailurePolicy = StepFailurePolicy.Bloquer
                },
                new WorkflowStep
                {
                    WorkflowId = workflowId, Order = 2, Name = "B",
                    Action = StepAction.Attendre, ExpectedSeconds = 1, TimeoutSeconds = 30,
                    CanRunInParallel = false, FailurePolicy = StepFailurePolicy.Bloquer
                },
                new WorkflowStep
                {
                    WorkflowId = workflowId, Order = 3, Name = "C",
                    Action = StepAction.Attendre, ExpectedSeconds = 1, TimeoutSeconds = 30,
                    CanRunInParallel = true, FailurePolicy = StepFailurePolicy.Bloquer
                });
            await db.SaveChangesAsync();
        }

        Assert.Null(await _workflows.ChangeStatusAsync(workflowId, LifecycleStatus.Valide, "test"));

        var execution = await PreparerEtLancerAsync(workflowId);
        await DeroulerAsync(execution);

        var fin = await _executions.GetAsync(execution);
        Assert.Equal(ExecutionStatus.TermineSucces, fin!.Status);

        var (a, b, c) = (
            fin.Steps.Single(s => s.Name == "A"),
            fin.Steps.Single(s => s.Name == "B"),
            fin.Steps.Single(s => s.Name == "C"));

        // B ne peut pas avoir demarre avant que A soit terminee : elle n'a
        // jamais ete groupee avec elle.
        Assert.True(b.StartedAt >= a.EndedAt);
        // C, elle, est parallelisable — mais seule dans son lot puisque rien
        // ne la suit : elle demarre normalement des que B est terminee.
        Assert.True(c.StartedAt >= b.EndedAt);
    }

    // =======================================================================
    // Aides
    // =======================================================================
    private string Journal(string composant)
    {
        var d = Definitions().First(x => x.Nom == composant);
        return Path.Combine(_racine, d.Dossier, d.Journal.Replace("*", "20260814"));
    }

    private async Task<Guid> CreerWorkflowAsync(
        string code, WorkflowKind nature, (string Composant, StepAction Action)[] etapes)
    {
        var id = await _workflows.CreateAsync(new Workflow
        {
            EnvironmentId = _envId,
            Code = code,
            Name = $"Recette {code}",
            Kind = nature
        });

        await using var db = _factory.CreateDbContext();

        var ordre = 1;
        foreach (var (composant, action) in etapes)
        {
            var d = Definitions().First(x => x.Nom == composant);

            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = id,
                Order = ordre++,
                Name = $"{action} {composant}",
                Action = action,
                ComponentId = _composants[composant],
                ExpectedSeconds = 2,
                TimeoutSeconds = 30,
                FailurePolicy = StepFailurePolicy.Bloquer
            });
        }

        await db.SaveChangesAsync();

        Assert.Null(await _workflows.ChangeStatusAsync(id, LifecycleStatus.Valide, "test"));
        return id;
    }

    private async Task<Guid> PreparerEtLancerAsync(Guid workflowId, bool simulation = false)
    {
        var prep = await _executions.PrepareAsync(
            workflowId, "recette", "Rejeu de recette contre le simulateur", "REC-01", simulation);

        Assert.True(prep.Succeeded, prep.Error);

        // Inoffensif quand la sequence ne vise pas le Center (le champ reste
        // simplement inutilise) ; necessaire des qu'elle l'arrete ou le
        // redemarre (FR-046/047). Ces scenarios de recette ne testent pas la
        // bascule elle-meme, donc le choix par defaut le plus sur convient.
        Assert.Null(await _executions.SetContinuityChoiceAsync(
            prep.ExecutionId, CenterContinuityChoice.ResterActif, "recette"));

        var rapport = await _precheck.RunAsync(prep.ExecutionId);
        Assert.False(rapport.HasBlockingFailure, string.Join(" | ",
            rapport.Checks.Where(c => c.Outcome == PreflightOutcome.Echec).Select(c => c.Detail)));

        Assert.Null(await _executions.StartAsync(prep.ExecutionId, "recette"));
        return prep.ExecutionId;
    }

    /// <summary>
    /// Déroule l'exécution par le VRAI moteur, jusqu'à son terme.
    ///
    /// Le moteur pilote en tâche de fond : on attend qu'il conclue, avec une
    /// borne pour qu'un blocage se traduise par un échec de test lisible plutôt
    /// que par une suite qui ne rend jamais la main.
    /// </summary>
    /// <summary>
    /// Attend que le verrou d'environnement soit rendu, au lieu de le supposer
    /// rendu à l'instant même où l'exécution passe en état terminal.
    ///
    /// Le moteur écrit le statut PUIS relâche le verrou, et c'est le bon ordre :
    /// relâcher d'abord déclarerait l'environnement libre alors que l'exécution
    /// écrit encore, et une autre opération pourrait démarrer dessus. Le test
    /// doit donc laisser passer ces quelques millisecondes — sous forte charge
    /// parallèle, l'ordonnanceur les étire assez pour faire échouer une
    /// assertion immédiate.
    ///
    /// L'attente reste bornée : si le verrou n'est jamais rendu, c'est un vrai
    /// défaut et le test doit le dire.
    /// </summary>
    private async Task AttendreLiberationDuVerrouAsync()
    {
        var limite = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < limite)
        {
            if (await _verrous.GetAsync(_envId) is null) return;
            await Task.Delay(50);
        }

        var reste = await _verrous.GetAsync(_envId);
        Assert.Fail(
            "Le verrou d'environnement n'a pas été rendu dans les 10 s suivant la fin "
            + $"de l'exécution : {reste}. L'environnement resterait bloqué pour toute "
            + "opération suivante.");
    }

    private async Task DeroulerAsync(Guid executionId)
    {
        using var jeton = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        await _moteur.PickUpAsync(jeton.Token);

        while (!jeton.IsCancellationRequested)
        {
            var execution = await _executions.GetAsync(executionId, CancellationToken.None);
            if (execution is not null && execution.IsFinished) return;

            try { await Task.Delay(200, jeton.Token); }
            catch (OperationCanceledException) { break; }
        }

        var dernier = await _executions.GetAsync(executionId, CancellationToken.None);

        var detail = dernier is null
            ? "exécution introuvable"
            : string.Join(" | ", dernier.Steps.OrderBy(s => s.Order)
                .Select(s => $"{s.Order}.{s.Name}={s.State}"
                           + (s.ProgressMessage is { Length: > 0 } p ? $" ({p})" : string.Empty)
                           + (s.Error is { Length: > 0 } e ? $" ERR:{e}" : string.Empty)));

        Assert.Fail($"L'exécution n'a pas abouti (état : {dernier?.Status}). Étapes : {detail}");
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }

    /// <summary>Portée minimale : le moteur ouvre les siennes pour chaque passe.</summary>
    private sealed class PorteeDeTest(
        IDbContextFactory<N4SentinelDbContext> factory,
        EnvironmentLockService verrous,
        StepExecutor executeur,
        SupervisionService supervision)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }

        public object? GetService(Type type)
        {
            if (type == typeof(IDbContextFactory<N4SentinelDbContext>)) return factory;
            if (type == typeof(EnvironmentLockService)) return verrous;
            if (type == typeof(StepExecutor)) return executeur;
            if (type == typeof(SupervisionService)) return supervision;
            return null;
        }
    }
}

/// <summary>
/// Gestionnaire de services simulé, greffé sur le VRAI connecteur PowerShell.
///
/// Tout ce qui touche aux journaux — résolution des chemins génériques, lecture
/// incrémentale, détection de rotation — passe par le connecteur réel et donc
/// par les mêmes scripts PowerShell qu'en production. Seul le contrôle des
/// services est simulé, faute de pouvoir créer de vrais services sans console
/// élevée.
///
/// La doublure écrit dans les VRAIS fichiers de journal, après un délai, comme
/// le ferait une JVM N4 qui met du temps à s'initialiser.
/// </summary>
internal sealed class SimulateurConnector(IN4Connector reel) : IN4Connector
{
    private readonly ConcurrentDictionary<string, string> _etats = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _muets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bloques = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rotations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Commandes réellement émises, pour vérifier qu'une simulation n'en émet aucune.</summary>
    public List<string> CommandesEmises { get; } = [];

    /// <summary>Composant dont le service démarre mais qui n'écrit jamais son marqueur.</summary>
    public void Muet(params string[] services) { foreach (var s in services) _muets.Add(s); }

    /// <summary>Composant dont l'arrêt reste indéfiniment en StopPending.</summary>
    public void BloqueEnArret(params string[] services) { foreach (var s in services) _bloques.Add(s); }

    /// <summary>Composant dont le journal est recréé au démarrage, comme XPS.</summary>
    public void RotationAuDemarrage(params string[] services) { foreach (var s in services) _rotations.Add(s); }

    public void Demarre(params string[] services)
    {
        foreach (var s in services) _etats[s] = "Running";
    }

    /// <summary>Journal et marqueur attendus, par service.</summary>
    private static readonly Dictionary<string, (string Fragment, string Marqueur)> Ecritures = new()
    {
        ["N4Sim Cluster Node 1"] = (@"Navis\cluster1\logs\navis-apex.log", "Web tier servlet 'action' initialized in 42000 ms"),
        ["N4Sim Cluster Node 2"] = (@"Navis\cluster2\logs\navis-apex.log", "Web tier servlet 'action' initialized in 39000 ms"),
        ["N4Sim Center Node"] = (@"Navis\center\logs\navis-apex.log", "Web tier servlet 'action' initialized in 51000 ms"),
        ["N4Sim XPS Bridge Daemon"] = (@"Navis\bridge\logs\navis-bridged.log", "Connection established to Center node - bridge is ACTIVE"),
        ["N4Sim XPS Service"] = (@"Navis\xps\log\xps_20260814.log", "XPS initialization complete - 312 equipment loaded")
    };

    public async Task<ConnectorResult<ServiceSnapshot>> ControlServiceAsync(
        ConnectorTarget target, string serviceName, ServiceControlAction action, CancellationToken ct = default)
    {
        lock (CommandesEmises) CommandesEmises.Add($"{action} {serviceName}");

        if (action == ServiceControlAction.Arreter)
        {
            _etats[serviceName] = _bloques.Contains(serviceName) ? "StopPending" : "Stopped";
            return Ok(serviceName, _etats[serviceName]);
        }

        _etats[serviceName] = "Running";

        // La JVM ecrit son marqueur APRES un delai, dans le vrai fichier. C'est
        // ce decalage qui rend la preuve par le journal necessaire.
        if (!_muets.Contains(serviceName) && Ecritures.TryGetValue(serviceName, out var e))
        {
            var racine = RacineDepuisCible(target);
            var journal = Path.Combine(racine, e.Fragment);

            _ = Task.Run(async () =>
            {
                await Task.Delay(1200, CancellationToken.None);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(journal)!);

                    // Rotation : le fichier est recree, comme le fait XPS.
                    if (_rotations.Contains(serviceName) && File.Exists(journal))
                        File.WriteAllText(journal, string.Empty);

                    await File.AppendAllTextAsync(journal,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} INFO  [main] c.n.a.Startup - {e.Marqueur}\n",
                        CancellationToken.None);
                }
                catch (IOException) { /* le lecteur tient le fichier : la passe suivante reessaiera */ }
            }, CancellationToken.None);
        }

        return Ok(serviceName, "Running");
    }

    public Task<ConnectorResult<ServiceSnapshot>> GetServiceAsync(
        ConnectorTarget target, string serviceName, CancellationToken ct = default) =>
        Task.FromResult(Ok(serviceName, _etats.GetValueOrDefault(serviceName, "Stopped")));

    public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> GetServicesAsync(
        ConnectorTarget target, IReadOnlyCollection<string> noms, CancellationToken ct = default) =>
        Task.FromResult(ConnectorResult<IReadOnlyList<ServiceSnapshot>>.Ok(
            noms.Select(n => new ServiceSnapshot
            {
                Name = n,
                Status = _etats.GetValueOrDefault(n, "Stopped")
            }).ToList(), TimeSpan.Zero));

    // --- Tout le reste passe par le connecteur REEL -------------------------
    public Task<ConnectorResult<string>> PingAsync(ConnectorTarget t, CancellationToken ct = default) =>
        reel.PingAsync(t, ct);

    public Task<ConnectorResult<IReadOnlyList<ServiceSnapshot>>> ListServicesAsync(
        ConnectorTarget t, IReadOnlyCollection<string> m, CancellationToken ct = default) =>
        reel.ListServicesAsync(t, m, ct);

    public Task<ConnectorResult<SystemSnapshot>> GetSystemAsync(ConnectorTarget t, CancellationToken ct = default) =>
        reel.GetSystemAsync(t, ct);

    public Task<ConnectorResult<LogDelta>> ReadLogDeltaAsync(
        ConnectorTarget t, string p, long o, int m = 262144, CancellationToken ct = default) =>
        reel.ReadLogDeltaAsync(t, p, o, m, ct);

    public Task<ConnectorResult<LogFileInfo>> ResolveLogAsync(
        ConnectorTarget t, string p, CancellationToken ct = default) =>
        reel.ResolveLogAsync(t, p, ct);

    public Task<ConnectorResult<LiveMetrics>> GetLiveMetricsAsync(ConnectorTarget t, CancellationToken ct = default) =>
        reel.GetLiveMetricsAsync(t, ct);

    public Task<ConnectorResult<TimeSyncSnapshot>> GetTimeSyncAsync(ConnectorTarget t, CancellationToken ct = default) =>
        reel.GetTimeSyncAsync(t, ct);

    public Task<ConnectorResult<UpdateSnapshot>> GetPendingUpdatesAsync(ConnectorTarget t, CancellationToken ct = default) =>
        reel.GetPendingUpdatesAsync(t, ct);

    public Task<ConnectorResult<FolderSnapshot>> ListFilesAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
        reel.ListFilesAsync(t, p, ct);

    public Task<ConnectorResult<WriteProbeResult>> ProbeWriteAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
        reel.ProbeWriteAsync(t, p, ct);

    // -----------------------------------------------------------------------
    /// <summary>Racine du simulateur, déduite du chemin de journal en cours d'usage.</summary>
    private static string _racine = string.Empty;

    public static void FixerRacine(string racine) => _racine = racine;

    private static string RacineDepuisCible(ConnectorTarget _) => _racine;

    private static ConnectorResult<ServiceSnapshot> Ok(string nom, string statut) =>
        ConnectorResult<ServiceSnapshot>.Ok(
            new ServiceSnapshot { Name = nom, Status = statut }, TimeSpan.Zero);
}
