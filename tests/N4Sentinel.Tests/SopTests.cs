using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Knowledge;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Procedures;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests des procédures opérationnelles standard (SOP) — FR-087 à FR-089D.
///
/// Trois principes gouvernent ce module, et ce sont eux que ces tests protègent.
///
///   1. UN SOP QUI A SERVI NE SE MODIFIE PLUS — même règle de versionnement
///      que <c>Workflow</c> (voir WorkflowTests-adjacent coverage dans
///      OrchestrationTests.cs pour le pendant automatisé).
///
///   2. LA PREUVE EST OBLIGATOIRE. Une confirmation sans preuve n'est pas une
///      preuve, c'est une case cochée — et un retour en arrière n'efface
///      jamais ce qui a été constaté, il l'archive.
///
///   3. LA PRÉSENTATION SELON LE GABARIT SOP (FR-088) NE COMPOSE RIEN : une
///      section absente du texte source reste vide, jamais devinée.
/// </summary>
public sealed class SopTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private TestDbContextFactory _factory = null!;
    private SopService _sop = null!;
    private SopExecutionService _executions = null!;
    private KnowledgeService _knowledge = null!;

    private Guid _envId;
    private Guid _composantId;

    public async Task InitializeAsync()
    {
        var cs = $"Server=localhost;Database={_databaseName};Trusted_Connection=True;"
               + "TrustServerCertificate=True;MultipleActiveResultSets=True";

        _factory = new TestDbContextFactory(TestDbContextOptions.Builder(cs).Options);

        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();

            var env = new N4Environment { Code = "UAT", Name = "Recette", Kind = EnvironmentKind.UAT };
            db.Environments.Add(env);
            await db.SaveChangesAsync();
            _envId = env.Id;

            var composant = new N4Component
            {
                EnvironmentId = _envId,
                LogicalName = "Bridge",
                Role = ComponentRole.BridgeDaemon,
                WindowsServiceName = "Navis Bridge Daemon",
                ControlMode = ControlMode.Pilotable,
                Status = LifecycleStatus.Valide
            };
            db.Components.Add(composant);
            await db.SaveChangesAsync();
            _composantId = composant.Id;
        }

        _knowledge = new KnowledgeService(_factory, NullLogger<KnowledgeService>.Instance, new AuditWriter(_factory));
        _sop = new SopService(_factory, _knowledge, NullLogger<SopService>.Instance, new AuditWriter(_factory));
        _executions = new SopExecutionService(_factory, new AuditWriter(_factory), NullLogger<SopExecutionService>.Instance);
    }

    public async Task DisposeAsync()
    {
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
    // FR-087 — Cycle de vie et versionnement
    // =======================================================================
    [Fact(DisplayName = "Un SOP jamais exécuté se modifie sur place")]
    public async Task Un_Sop_Brouillon_Se_Modifie_Sur_Place()
    {
        var id = await CreerSopAsync("Redémarrage Bridge");

        var r = await _sop.SaveAsync(id, "Redémarrage Bridge (v2)", new SopTemplateFields(),
            [UneEtape("Étape unique modifiée")]);

        Assert.True(r.Succeeded, r.Error);
        Assert.False(r.CreatedNewVersion);
        Assert.Equal(1, r.Version);
    }

    [Fact(DisplayName = "Un SOP déjà exécuté crée une nouvelle version, l'ancienne restant intacte")]
    public async Task Un_Sop_Deja_Execute_Cree_Une_Nouvelle_Version()
    {
        var id = await ValiderSopAsync("Redémarrage Bridge");
        var demarrage = await _executions.StartAsync(id, "op1", "Test", null);
        Assert.True(demarrage.Succeeded, demarrage.Error);

        var r = await _sop.SaveAsync(id, "Redémarrage Bridge (corrigé)", new SopTemplateFields(),
            [UneEtape("Étape corrigée")]);

        Assert.True(r.Succeeded, r.Error);
        Assert.True(r.CreatedNewVersion);
        Assert.Equal(2, r.Version);

        var ancien = await _sop.GetAsync(id);
        Assert.Equal("Redémarrage Bridge", ancien!.Title);
        Assert.True(ancien.HasBeenExecuted);
    }

    [Fact(DisplayName = "Un SOP sans étape ne peut pas être validé")]
    public async Task Un_Sop_Sans_Etape_Ne_Peut_Pas_Etre_Valide()
    {
        var id = await _sop.CreateAsync(new Domain.Sop
        {
            EnvironmentId = _envId,
            Code = "VIDE",
            Title = "SOP sans étape"
        });

        var erreur = await _sop.ChangeStatusAsync(id, LifecycleStatus.Valide, "test");
        Assert.NotNull(erreur);
        Assert.Contains("sans étape", erreur!);
    }

    [Fact(DisplayName = "Valider un SOP laisse une trace d'audit (FR-091)")]
    public async Task Valider_Un_Sop_Est_Audite()
    {
        var id = await ValiderSopAsync("Redémarrage Bridge");

        await using var db = _factory.CreateDbContext();
        var trace = await db.AuditEntries
            .Where(a => a.Action == AuditAction.ChangementDeStatut)
            .Where(a => a.EntityId == id.ToString())
            .SingleOrDefaultAsync();

        Assert.NotNull(trace);
        Assert.Equal("test", trace!.Actor);
    }

    [Fact(DisplayName = "Une étape sans instruction est refusée à l'enregistrement")]
    public async Task SaveAsync_Refuse_Une_Etape_Sans_Instruction()
    {
        var id = await CreerSopAsync("Test");

        var r = await _sop.SaveAsync(id, "Test", new SopTemplateFields(),
            [new SopStep { Title = "Sans instruction", Instruction = "" }]);

        Assert.False(r.Succeeded);
        Assert.Contains("n'a pas d'instruction", r.Error!);
    }

    // =======================================================================
    // FR-089B — Génération depuis une exécution réussie
    // =======================================================================
    [Fact(DisplayName = "Générer un SOP depuis une exécution non terminée avec succès est refusé")]
    public async Task GenerateFromExecutionAsync_Refuse_Une_Execution_Non_Terminee_Sans_Reserve()
    {
        var executionId = await CreerExecutionWorkflowAsync(ExecutionStatus.TermineAvecAvertissements);

        var r = await _sop.GenerateFromExecutionAsync(executionId, "GEN-1", "SOP généré");

        Assert.False(r.Succeeded);
        Assert.Contains("SANS réserve", r.Error!);
    }

    [Fact(DisplayName = "Le SOP généré reprend la preuve observée comme résultat attendu, en brouillon")]
    public async Task GenerateFromExecutionAsync_Reprend_La_Preuve_Comme_Resultat_Attendu()
    {
        var executionId = await CreerExecutionWorkflowAsync(ExecutionStatus.TermineSucces);

        var r = await _sop.GenerateFromExecutionAsync(executionId, "GEN-1", "SOP généré");
        Assert.True(r.Succeeded, r.Error);

        var sop = await _sop.GetAsync(r.SopId);
        Assert.Equal(LifecycleStatus.Brouillon, sop!.Status);
        Assert.False(sop.HasBeenExecuted);
        Assert.Equal(executionId, sop.SourceExecutionId);

        var etape = Assert.Single(sop.Steps);
        Assert.Equal("bridge is ACTIVE and ready", etape.ExpectedResult);
        Assert.Contains("Démarrer", etape.Instruction);
        Assert.Equal(_composantId, etape.ComponentId);
    }

    // =======================================================================
    // FR-097 — Génération depuis un incident clos
    // =======================================================================
    [Fact(DisplayName = "Générer un SOP depuis une session de diagnostic non clôturée est refusé")]
    public async Task GenerateFromDiagnosticSessionAsync_Refuse_Une_Session_Non_Cloturee()
    {
        var sessionId = await CreerSessionClotureeAsync(cloturer: false, avecAction: true);

        var r = await _sop.GenerateFromDiagnosticSessionAsync(sessionId, "GEN-INC-1", "SOP généré");

        Assert.False(r.Succeeded);
        Assert.Contains("CLÔTURÉE", r.Error!);
    }

    [Fact(DisplayName = "Générer un SOP depuis un incident clos sans action déclarée est refusé")]
    public async Task GenerateFromDiagnosticSessionAsync_Refuse_Sans_Action_Declaree()
    {
        var sessionId = await CreerSessionClotureeAsync(cloturer: true, avecAction: false);

        var r = await _sop.GenerateFromDiagnosticSessionAsync(sessionId, "GEN-INC-1", "SOP généré");

        Assert.False(r.Succeeded);
        Assert.Contains("Aucune action", r.Error!);
    }

    [Fact(DisplayName = "Le SOP généré depuis un incident clos reprend les actions réellement déclarées, en brouillon")]
    public async Task GenerateFromDiagnosticSessionAsync_Reprend_Les_Actions_Declarees()
    {
        var sessionId = await CreerSessionClotureeAsync(cloturer: true, avecAction: true);

        var r = await _sop.GenerateFromDiagnosticSessionAsync(sessionId, "GEN-INC-1", "SOP généré");
        Assert.True(r.Succeeded, r.Error);

        var sop = await _sop.GetAsync(r.SopId);
        Assert.Equal(LifecycleStatus.Brouillon, sop!.Status);
        Assert.False(sop.HasBeenExecuted);
        Assert.Equal(sessionId, sop.SourceDiagnosticSessionId);

        var etape = Assert.Single(sop.Steps);
        Assert.Contains("Redémarrage manuel du service via la console serveur", etape.Instruction);
        Assert.Equal(_composantId, etape.ComponentId);
    }

    // =======================================================================
    // FR-059E/F/G — SOP guidé de reconstitution ActiveMQ/KahaDB
    // =======================================================================
    [Fact(DisplayName = "Le SOP de reconstitution ActiveMQ/KahaDB est créé en brouillon, avec toutes ses étapes")]
    public async Task SeedReconstitutionKahaDbAsync_Cree_Un_Brouillon_Complet()
    {
        var r = await _sop.SeedReconstitutionKahaDbAsync(_envId);
        Assert.True(r.Succeeded, r.Error);

        var sop = await _sop.GetAsync(r.SopId);
        Assert.Equal(LifecycleStatus.Brouillon, sop!.Status);
        Assert.False(sop.HasBeenExecuted);
        Assert.True(sop.Steps.Count > 0);

        // Chaque étape porte une instruction ET un résultat attendu : un SOP
        // décrit un geste vérifiable, pas seulement un intitulé.
        Assert.All(sop.Steps, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Instruction));
            Assert.False(string.IsNullOrWhiteSpace(s.ExpectedResult));
        });

        // Les garde-fous grounded dans le corpus doivent être présents,
        // textuellement, pas seulement dans l'esprit du gabarit.
        Assert.Contains(sop.Steps, s => s.Instruction.Contains("db.data"));
        Assert.Contains(sop.Steps, s => s.Title.Contains("TOUS les services"));
        Assert.Contains("KahaDB", sop.EscalationPath);
        Assert.Contains("db.data", sop.Objective);
    }

    [Fact(DisplayName = "Créer deux fois le SOP de reconstitution échoue sur le code déjà pris")]
    public async Task SeedReconstitutionKahaDbAsync_Deux_Fois_Echoue_Sur_Le_Code()
    {
        Assert.True((await _sop.SeedReconstitutionKahaDbAsync(_envId)).Succeeded);

        var r2 = await _sop.SeedReconstitutionKahaDbAsync(_envId);

        Assert.False(r2.Succeeded);
    }

    // =======================================================================
    // FR-089 — Démarrage d'une exécution
    // =======================================================================
    [Fact(DisplayName = "Un SOP non validé ne peut pas être démarré")]
    public async Task StartAsync_Refuse_Un_Sop_Non_Valide()
    {
        var id = await CreerSopAsync("Brouillon");

        var r = await _executions.StartAsync(id, "op1", null, null);

        Assert.False(r.Succeeded);
        Assert.Contains("pas validé", r.Error!);
    }

    [Fact(DisplayName = "Démarrer une exécution met la première étape en cours, les suivantes à venir")]
    public async Task StartAsync_Met_La_Premiere_Etape_En_Cours()
    {
        var id = await ValiderSopAsync("Deux étapes", "Première étape", "Seconde étape");

        var r = await _executions.StartAsync(id, "op1", "Test", "TCK-1");
        Assert.True(r.Succeeded, r.Error);

        var execution = await _executions.GetAsync(r.ExecutionId);
        Assert.Equal(SopExecutionStatus.EnCours, execution!.Status);

        var etapes = execution.Steps.OrderBy(s => s.Order).ToList();
        Assert.Equal(SopExecutionStepState.EnCours, etapes[0].State);
        Assert.NotNull(etapes[0].StartedAt);
        Assert.Equal(SopExecutionStepState.AVenir, etapes[1].State);
        Assert.Null(etapes[1].StartedAt);
    }

    // =======================================================================
    // FR-089A/C — Confirmation, écart, contournement
    // =======================================================================
    [Fact(DisplayName = "Une confirmation sans preuve est refusée")]
    public async Task ConfirmStepAsync_Exige_Une_Preuve()
    {
        var executionId = await DemarrerExecutionAsync("Une étape");
        var etapeId = (await _executions.GetAsync(executionId))!.Steps.Single().Id;

        var erreur = await _executions.ConfirmStepAsync(etapeId, "op1", "  ", null);

        Assert.NotNull(erreur);
        Assert.Contains("case cochée", erreur!);
    }

    [Fact(DisplayName = "Confirmer une étape fait avancer l'exécution à la suivante")]
    public async Task ConfirmStepAsync_Avance_A_L_Etape_Suivante()
    {
        var executionId = await DemarrerExecutionAsync("Une étape", "Étape suivante");
        var premiere = (await _executions.GetAsync(executionId))!.Steps.OrderBy(s => s.Order).First();

        var erreur = await _executions.ConfirmStepAsync(premiere.Id, "op1", "Constaté OK.", null);
        Assert.Null(erreur);

        var apres = await _executions.GetAsync(executionId);
        var etapes = apres!.Steps.OrderBy(s => s.Order).ToList();
        Assert.Equal(SopExecutionStepState.Confirmee, etapes[0].State);
        Assert.Equal("op1", etapes[0].ConfirmedBy);
        Assert.Equal(SopExecutionStepState.EnCours, etapes[1].State);
        Assert.Equal(SopExecutionStatus.EnCours, apres.Status);
    }

    [Fact(DisplayName = "Un écart constaté n'est pas un échec : l'exécution continue, l'écart reste visible")]
    public async Task ConfirmStepAsync_Avec_Ecart_Marque_L_Etape_Ecart_Sans_Bloquer()
    {
        var executionId = await DemarrerExecutionAsync("Une étape", "Étape suivante");
        var premiere = (await _executions.GetAsync(executionId))!.Steps.OrderBy(s => s.Order).First();

        var erreur = await _executions.ConfirmStepAsync(
            premiere.Id, "op1", "Le service a redémarré après 3 tentatives.", "Délai plus long que prévu.");
        Assert.Null(erreur);

        var apres = await _executions.GetAsync(executionId);
        var etapes = apres!.Steps.OrderBy(s => s.Order).ToList();
        Assert.Equal(SopExecutionStepState.Ecart, etapes[0].State);
        Assert.Equal("Délai plus long que prévu.", etapes[0].DeviationNote);
        Assert.Equal(SopExecutionStepState.EnCours, etapes[1].State);
    }

    [Fact(DisplayName = "Confirmer la dernière étape conclut l'exécution")]
    public async Task ConfirmStepAsync_De_La_Derniere_Etape_Conclut_L_Execution()
    {
        var executionId = await DemarrerExecutionAsync("Seule étape");
        var etape = (await _executions.GetAsync(executionId))!.Steps.Single();

        await _executions.ConfirmStepAsync(etape.Id, "op1", "Fait.", null);

        var apres = await _executions.GetAsync(executionId);
        Assert.Equal(SopExecutionStatus.Termine, apres!.Status);
        Assert.NotNull(apres.EndedAt);
    }

    [Fact(DisplayName = "Une étape non contournable ne peut jamais être ignorée")]
    public async Task SkipStepAsync_Refuse_Une_Etape_Non_Contournable()
    {
        var executionId = await DemarrerExecutionAsync("Étape stricte");
        var etape = (await _executions.GetAsync(executionId))!.Steps.Single();

        var erreur = await _executions.SkipStepAsync(etape.Id, "op1", "Motif quelconque.");

        Assert.NotNull(erreur);
        Assert.Contains("n'est pas déclarée contournable", erreur!);
    }

    [Fact(DisplayName = "Un contournement sans motif est refusé")]
    public async Task SkipStepAsync_Exige_Un_Motif()
    {
        var id = await ValiderSopAsync("Contournable", "Étape contournable");
        await using (var db = _factory.CreateDbContext())
        {
            var etape = await db.SopSteps.FirstAsync(s => s.SopId == id);
            etape.IsSkippable = true;
            await db.SaveChangesAsync();
        }

        var demarrage = await _executions.StartAsync(id, "op1", null, null);
        var etapeExec = (await _executions.GetAsync(demarrage.ExecutionId))!.Steps.Single();

        var erreur = await _executions.SkipStepAsync(etapeExec.Id, "op1", "   ");

        Assert.NotNull(erreur);
        Assert.Contains("trou dans la traçabilité", erreur!);
    }

    // =======================================================================
    // FR-089A — Retour en arrière
    // =======================================================================
    [Fact(DisplayName = "Un retour en arrière rouvre la dernière étape sans effacer ce qui a été constaté")]
    public async Task GoBackAsync_Rouvre_La_Derniere_Etape_Sans_Rien_Effacer()
    {
        var executionId = await DemarrerExecutionAsync("Seule étape");
        var etape = (await _executions.GetAsync(executionId))!.Steps.Single();

        await _executions.ConfirmStepAsync(etape.Id, "op1", "Preuve initiale constatée.", null);
        var termine = await _executions.GetAsync(executionId);
        Assert.Equal(SopExecutionStatus.Termine, termine!.Status);

        var erreur = await _executions.GoBackAsync(executionId, "op2", "La preuve initiale était incomplète.");
        Assert.Null(erreur);

        var repris = await _executions.GetAsync(executionId);
        Assert.Equal(SopExecutionStatus.EnCours, repris!.Status);
        Assert.Null(repris.EndedAt);

        var etapeReprise = repris.Steps.Single();
        Assert.Equal(SopExecutionStepState.EnCours, etapeReprise.State);
        Assert.Null(etapeReprise.Evidence);
        Assert.Null(etapeReprise.ConfirmedBy);

        // Rien n'est perdu : l'ancienne confirmation est archivée.
        Assert.NotNull(etapeReprise.History);
        Assert.Contains("Preuve initiale constatée", etapeReprise.History!);
        Assert.Contains("op1", etapeReprise.History!);
        Assert.Contains("op2", etapeReprise.History!);
        Assert.Contains("preuve initiale était incomplète", etapeReprise.History!);
    }

    [Fact(DisplayName = "Un retour en arrière fait aussi reculer l'étape suivante à venir")]
    public async Task GoBackAsync_Remet_Aussi_L_Etape_Suivante_A_Venir()
    {
        var executionId = await DemarrerExecutionAsync("Première", "Seconde");
        var premiere = (await _executions.GetAsync(executionId))!.Steps.OrderBy(s => s.Order).First();

        await _executions.ConfirmStepAsync(premiere.Id, "op1", "Première étape faite.", null);

        var avant = await _executions.GetAsync(executionId);
        Assert.Equal(SopExecutionStepState.EnCours, avant!.Steps.OrderBy(s => s.Order).Last().State);

        await _executions.GoBackAsync(executionId, "op1", "Erreur, à reprendre.");

        var apres = await _executions.GetAsync(executionId);
        var etapes = apres!.Steps.OrderBy(s => s.Order).ToList();
        Assert.Equal(SopExecutionStepState.EnCours, etapes[0].State);
        Assert.Equal(SopExecutionStepState.AVenir, etapes[1].State);
        Assert.Null(etapes[1].StartedAt);
    }

    [Fact(DisplayName = "Un retour en arrière sans motif est refusé")]
    public async Task GoBackAsync_Exige_Un_Motif()
    {
        var executionId = await DemarrerExecutionAsync("Seule étape");
        var etape = (await _executions.GetAsync(executionId))!.Steps.Single();
        await _executions.ConfirmStepAsync(etape.Id, "op1", "Fait.", null);

        var erreur = await _executions.GoBackAsync(executionId, "op2", "");

        Assert.NotNull(erreur);
        Assert.Contains("pas traçable", erreur!);
    }

    // =======================================================================
    // Abandon
    // =======================================================================
    [Fact(DisplayName = "Abandonner une exécution annule les étapes restantes")]
    public async Task AbandonAsync_Annule_Les_Etapes_Restantes()
    {
        var executionId = await DemarrerExecutionAsync("Première", "Seconde");

        var erreur = await _executions.AbandonAsync(executionId, "op1", "Incident réel en cours, priorité ailleurs.");
        Assert.Null(erreur);

        var apres = await _executions.GetAsync(executionId);
        Assert.Equal(SopExecutionStatus.Abandonne, apres!.Status);
        Assert.All(apres.Steps, s => Assert.Equal(SopExecutionStepState.Annulee, s.State));
    }

    // =======================================================================
    // FR-089D — Suggestion de réutilisation
    // =======================================================================
    [Fact(DisplayName = "Un SOP associé à un composant est proposé pour ce composant")]
    public async Task SuggestForReuseAsync_Trouve_Un_Sop_Associe_Au_Composant()
    {
        var id = await ValiderSopAsync("Procédure Bridge", "Étape unique");
        Assert.Null(await _sop.AddAssociationAsync(new SopAssociation
        {
            SopId = id,
            Kind = SopAssociationKind.Composant,
            ComponentId = _composantId
        }));

        var suggestions = await _sop.SuggestForReuseAsync(_envId, componentId: _composantId);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(id, suggestion.Id);
    }

    [Fact(DisplayName = "Sans association ni filtre, aucune suggestion n'est proposée")]
    public async Task SuggestForReuseAsync_Sans_Critere_Ne_Renvoie_Rien()
    {
        await ValiderSopAsync("Procédure Bridge", "Étape unique");

        var suggestions = await _sop.SuggestForReuseAsync(_envId);

        Assert.Empty(suggestions);
    }

    [Fact(DisplayName = "L'historique d'utilisation compte les exécutions par état, sans inventer de taux de réussite")]
    public async Task GetUsageStatsAsync_Compte_Par_Etat()
    {
        var id = await ValiderSopAsync("Procédure Bridge", "Étape unique");

        var e1 = await _executions.StartAsync(id, "op1", "Test 1", null);
        Assert.True(e1.Succeeded, e1.Error);
        var etape1 = (await _executions.GetAsync(e1.ExecutionId))!.Steps.Single().Id;
        Assert.Null(await _executions.ConfirmStepAsync(etape1, "op1", "Preuve 1", null));

        var e2 = await _executions.StartAsync(id, "op2", "Test 2", null);
        Assert.True(e2.Succeeded, e2.Error);
        Assert.Null(await _executions.AbandonAsync(e2.ExecutionId, "op2", "Interrompu"));

        var stats = await _sop.GetUsageStatsAsync(id);

        Assert.Equal(1, stats.Termine);
        Assert.Equal(1, stats.Abandonne);
        Assert.Equal(0, stats.EnCours);
        Assert.Equal(2, stats.Total);

        // Aucune exécution n'a été attestée : le taux reste null, jamais
        // déduit du seul fait que la procédure ait été suivie jusqu'au bout.
        Assert.Equal(0, stats.OutcomesAttestes);
        Assert.Null(stats.SuccessRatePercent);
    }

    [Fact(DisplayName = "Un SOP jamais exécuté a un historique vide")]
    public async Task GetUsageStatsAsync_Sans_Execution_Est_Vide()
    {
        var id = await ValiderSopAsync("Procédure Bridge", "Étape unique");

        var stats = await _sop.GetUsageStatsAsync(id);

        Assert.Equal(0, stats.Total);
    }

    [Fact(DisplayName = "Un résultat attesté alimente réellement le taux de réussite (FR-089D)")]
    public async Task DeclareOutcomeAsync_Alimente_Le_Taux_De_Reussite_Reel()
    {
        var id = await ValiderSopAsync("Procédure Bridge", "Étape unique");

        var e1 = await _executions.StartAsync(id, "op1", "Test 1", null);
        var etape1 = (await _executions.GetAsync(e1.ExecutionId))!.Steps.Single().Id;
        await _executions.ConfirmStepAsync(etape1, "op1", "Preuve 1", null);
        Assert.Null(await _executions.DeclareOutcomeAsync(e1.ExecutionId, "op1", SopOutcome.ProblemeResolu, null));

        var e2 = await _executions.StartAsync(id, "op2", "Test 2", null);
        var etape2 = (await _executions.GetAsync(e2.ExecutionId))!.Steps.Single().Id;
        await _executions.ConfirmStepAsync(etape2, "op2", "Preuve 2", null);
        Assert.Null(await _executions.DeclareOutcomeAsync(e2.ExecutionId, "op2", SopOutcome.ProblemeNonResolu, "Le service a rechuté."));

        var stats = await _sop.GetUsageStatsAsync(id);

        Assert.Equal(2, stats.OutcomesAttestes);
        Assert.Equal(50, stats.SuccessRatePercent);
    }

    [Fact(DisplayName = "Le résultat ne peut être attesté avant la fin de l'exécution")]
    public async Task DeclareOutcomeAsync_Refuse_Avant_La_Fin_De_L_Execution()
    {
        var executionId = await DemarrerExecutionAsync("Première étape", "Seconde étape");

        var erreur = await _executions.DeclareOutcomeAsync(executionId, "op1", SopOutcome.ProblemeResolu, null);

        Assert.NotNull(erreur);
        Assert.Contains("ne peut être attesté", erreur!);
    }

    // =======================================================================
    // FR-087 — Signalement et correction de la documentation
    // =======================================================================
    [Fact(DisplayName = "Accepter une correction remplace le contenu de la section citée")]
    public async Task AcceptFeedbackAsync_Remplace_Le_Contenu_De_La_Section()
    {
        var document = new KnowledgeDocument
        {
            Title = "Procédure interne",
            Reference = "PROC-INT-1",
            Kind = DocumentKind.ProcedureInterne
        };
        var indexation = await _knowledge.IndexAsync(document,
            [new ExtractedSection { PageNumber = 1, Heading = "Redémarrage", Content = "Texte initial, imprécis, mais assez long pour être indexé." }]);
        await _knowledge.ValidateAsync(indexation.DocumentId, "m.konate");

        var doc = await _knowledge.GetDocumentAsync(indexation.DocumentId);
        var sectionId = doc!.Sections.Single().Id;

        await _knowledge.ReportIncorrectAsync(
            indexation.DocumentId, sectionId, "comment redémarrer ?", "Manque une étape.",
            "Texte corrigé, avec l'étape manquante.", "op1");

        var signalement = (await _knowledge.GetOpenFeedbackAsync(indexation.DocumentId)).Single();
        Assert.Equal(FeedbackReviewStatus.EnAttente, signalement.ReviewStatus);

        var erreur = await _knowledge.AcceptFeedbackAsync(signalement.Id, "admin", "Confirmé, merci.");
        Assert.Null(erreur);

        var docApres = await _knowledge.GetDocumentAsync(indexation.DocumentId);
        Assert.Equal("Texte corrigé, avec l'étape manquante.", docApres!.Sections.Single().Content);
        Assert.Empty(await _knowledge.GetOpenFeedbackAsync(indexation.DocumentId));
    }

    [Fact(DisplayName = "Accepter une correction sur un document éditeur n'en modifie jamais le contenu")]
    public async Task AcceptFeedbackAsync_Ne_Modifie_Jamais_Un_Document_Editeur()
    {
        var document = new KnowledgeDocument
        {
            Title = "Guide Navis",
            Reference = "GUIDE-N4-1",
            Kind = DocumentKind.GuideEditeur
        };
        var indexation = await _knowledge.IndexAsync(document,
            [new ExtractedSection { PageNumber = 1, Heading = "Section", Content = "Texte éditeur d'origine, suffisamment long pour être indexé." }]);
        await _knowledge.ValidateAsync(indexation.DocumentId, "m.konate");

        var doc = await _knowledge.GetDocumentAsync(indexation.DocumentId);
        var sectionId = doc!.Sections.Single().Id;

        await _knowledge.ReportIncorrectAsync(
            indexation.DocumentId, sectionId, "question", null, "Correction proposée.", "op1");
        var signalement = (await _knowledge.GetOpenFeedbackAsync(indexation.DocumentId)).Single();

        Assert.Null(await _knowledge.AcceptFeedbackAsync(signalement.Id, "admin", null));

        var docApres = await _knowledge.GetDocumentAsync(indexation.DocumentId);
        Assert.Equal("Texte éditeur d'origine, suffisamment long pour être indexé.", docApres!.Sections.Single().Content);
    }

    [Fact(DisplayName = "Un rejet sans motif est refusé")]
    public async Task RejectFeedbackAsync_Exige_Un_Motif()
    {
        var document = new KnowledgeDocument { Title = "Doc", Reference = "DOC-1", Kind = DocumentKind.ProcedureInterne };
        var indexation = await _knowledge.IndexAsync(document,
            [new ExtractedSection { PageNumber = 1, Content = "Texte suffisamment long pour être indexé correctement." }]);
        await _knowledge.ValidateAsync(indexation.DocumentId, "m.konate");
        var doc = await _knowledge.GetDocumentAsync(indexation.DocumentId);
        var sectionId = doc!.Sections.Single().Id;

        await _knowledge.ReportIncorrectAsync(indexation.DocumentId, sectionId, "question", null, null, "op1");
        var signalement = (await _knowledge.GetOpenFeedbackAsync(indexation.DocumentId)).Single();

        var erreur = await _knowledge.RejectFeedbackAsync(signalement.Id, "admin", "  ");

        Assert.NotNull(erreur);
        Assert.Contains("trou dans la traçabilité", erreur!);
    }

    // =======================================================================
    // FR-088 — Présentation selon le gabarit SOP
    // =======================================================================
    [Fact(DisplayName = "La présentation extrait les sections dont l'en-tête est reconnu, et laisse les autres vides")]
    public async Task PresentAsSopAsync_Extrait_Les_Sections_Reconnues()
    {
        var texte =
            "Objectif: Redémarrer le service Bridge sans perte de messages.\n"
          + "Prerequis: Le Bridge doit être à l'arrêt.\n"
          + "Étapes:\n"
          + "1. Arrêter le service Navis Bridge Daemon.\n"
          + "2. Vérifier que la file ActiveMQ est vide.\n"
          + "3. Redémarrer le service.\n";

        var document = new KnowledgeDocument
        {
            Title = "Procédure Bridge",
            Reference = "PROC-BRIDGE-1",
            Kind = DocumentKind.ModeOperatoire
        };
        var indexation = await _knowledge.IndexAsync(document,
            [new ExtractedSection { PageNumber = 1, Heading = "Redémarrage Bridge", Content = texte }]);
        Assert.True(indexation.Succeeded, indexation.Error);
        await _knowledge.ValidateAsync(indexation.DocumentId, "m.konate");

        var presentation = await _sop.PresentAsSopAsync("redémarrer le service Bridge");

        Assert.True(presentation.Answered);
        Assert.Contains("perte de messages", presentation.Objective!);
        Assert.Contains("à l'arrêt", presentation.Prerequisites!);
        Assert.True(presentation.StepsSourceIdentified);
        Assert.Equal(3, presentation.Steps.Count);
        Assert.Contains("Vérifier que la file ActiveMQ est vide", presentation.Steps[1]);

        // Aucune section "Risques" dans le texte source : elle reste vide,
        // jamais devinée.
        Assert.Null(presentation.Risks);
        Assert.NotNull(presentation.SourceCitation);
    }

    [Fact(DisplayName = "Sans document correspondant, la présentation le dit et ne compose rien")]
    public async Task PresentAsSopAsync_Sans_Reponse_Documentaire()
    {
        var presentation = await _sop.PresentAsSopAsync("procédure de dédouanement des conteneurs frigorifiques");

        Assert.False(presentation.Answered);
        Assert.NotNull(presentation.Explanation);
        Assert.Empty(presentation.Steps);
    }

    // =======================================================================
    // Aides
    // =======================================================================
    private async Task<Guid> CreerSopAsync(string titre, params string[] instructions)
    {
        var id = await _sop.CreateAsync(new Domain.Sop
        {
            EnvironmentId = _envId,
            Code = $"SOP{Guid.NewGuid():N}"[..10],
            Title = titre
        });

        if (instructions.Length > 0)
        {
            var etapes = instructions.Select(UneEtape).ToList();
            var r = await _sop.SaveAsync(id, titre, new SopTemplateFields(), etapes);
            Assert.True(r.Succeeded, r.Error);
        }

        return id;
    }

    private async Task<Guid> ValiderSopAsync(string titre, params string[] instructions)
    {
        var etapes = instructions.Length > 0 ? instructions : ["Étape unique"];
        var id = await CreerSopAsync(titre, etapes);
        Assert.Null(await _sop.ChangeStatusAsync(id, LifecycleStatus.Valide, "test"));
        return id;
    }

    private async Task<Guid> DemarrerExecutionAsync(params string[] instructions)
    {
        var id = await ValiderSopAsync($"Exécution {Guid.NewGuid():N}"[..12], instructions);
        var r = await _executions.StartAsync(id, "op1", "Test", null);
        Assert.True(r.Succeeded, r.Error);
        return r.ExecutionId;
    }

    private static SopStep UneEtape(string instruction) =>
        new() { Title = instruction, Instruction = instruction };

    private async Task<Guid> CreerExecutionWorkflowAsync(ExecutionStatus statut)
    {
        await using var db = _factory.CreateDbContext();

        var workflow = new Workflow
        {
            EnvironmentId = _envId,
            Code = $"WF{Guid.NewGuid():N}"[..8],
            Name = "Démarrage Bridge",
            Kind = WorkflowKind.OperationUnitaire,
            Status = LifecycleStatus.Valide,
            HasBeenExecuted = true
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var execution = new WorkflowExecution
        {
            WorkflowId = workflow.Id,
            WorkflowVersion = 1,
            WorkflowName = workflow.Name,
            Kind = workflow.Kind,
            EnvironmentId = _envId,
            EnvironmentCode = "UAT",
            Status = statut,
            RequestedBy = "op1",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            EndedAt = DateTimeOffset.UtcNow
        };
        execution.Steps.Add(new ExecutionStep
        {
            Order = 1,
            Name = "Démarrer le Bridge",
            Action = StepAction.Demarrer,
            ComponentId = _composantId,
            ComponentName = "Bridge",
            State = ExecutionStepState.Reussi,
            Evidence = "bridge is ACTIVE and ready",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-3)
        });

        db.Executions.Add(execution);
        await db.SaveChangesAsync();
        return execution.Id;
    }

    private async Task<Guid> CreerSessionClotureeAsync(bool cloturer, bool avecAction)
    {
        await using var db = _factory.CreateDbContext();

        var session = new DiagnosticSession
        {
            EnvironmentId = _envId,
            EnvironmentCode = "UAT",
            Title = "Bridge bloqué en StopPending",
            RequestedBy = "op1",
            Phase = cloturer ? DiagnosticPhase.ClotureEtCapitalisation : DiagnosticPhase.DiagnosticEtCorrelation
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        if (avecAction)
        {
            db.ExternalActionDeclarations.Add(new ExternalActionDeclaration
            {
                EnvironmentId = _envId,
                DiagnosticSessionId = session.Id,
                ComponentId = _composantId,
                ComponentName = "Bridge",
                Description = "Redémarrage manuel du service via la console serveur, après échec de la commande à distance.",
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                DeclaredBy = "op1"
            });
            await db.SaveChangesAsync();
        }

        return session.Id;
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
