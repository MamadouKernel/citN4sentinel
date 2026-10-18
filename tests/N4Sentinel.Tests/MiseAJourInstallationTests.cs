using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Orchestration.UseCases;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;
using N4Sentinel.Infrastructure.Supervision;
using Xunit;

namespace N4Sentinel.Tests;

/// <summary>
/// Installation des mises à jour Windows (FR nouveau) — logique propre à la
/// fonctionnalité : l'étape d'installation ne redémarre jamais, signale un
/// redémarrage requis, distingue succès/échec/partiel, et hérite de la
/// gouvernance des opérations ponctuelles (approbation selon l'environnement).
///
/// Le garde-fou central — aucun redémarrage automatique d'un nœud actif — est
/// prouvé ici de façon structurelle : même quand le connecteur signale
/// RebootRequired, l'étape ne redémarre rien, elle renvoie un AVERTISSEMENT qui
/// renvoie l'opérateur vers le workflow d'arrêt/redémarrage N4 (ordre Kaleris).
/// </summary>
public sealed class MiseAJourInstallationTests
{
    // -----------------------------------------------------------------------
    // Étape d'installation : issues
    // -----------------------------------------------------------------------
    [Fact]
    public async Task InstallerAsync_installe_sans_redemarrage_rend_un_succes()
    {
        // Arrange
        var h = new Harnais();
        var ids = await h.SeedPilotableAsync();
        h.Connector
            .Setup(c => c.InstallUpdatesAsync(It.IsAny<ConnectorTarget>(), It.IsAny<UpdateInstallScope>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConnectorResult<UpdateInstallResult>.Ok(new UpdateInstallResult
            {
                HostName = "HOTE01", InstalledCount = 3, FailedCount = 0, RebootRequired = false
            }, TimeSpan.FromMinutes(2)));

        var step = EtapeInstallation(ids.ComponentId, PorteeMajInstructions.Securite);

        // Act
        var issue = await h.Executeur.ExecuteAsync(step, isSimulation: false, Progress(), CancellationToken.None);

        // Assert
        Assert.Equal(ExecutionStepState.Reussi, issue.State);
        Assert.Contains("3 mise(s) à jour installée(s)", issue.Message);
    }

    [Fact]
    public async Task InstallerAsync_quand_redemarrage_requis_avertit_sans_redemarrer()
    {
        // Arrange
        var h = new Harnais();
        var ids = await h.SeedPilotableAsync();
        h.Connector
            .Setup(c => c.InstallUpdatesAsync(It.IsAny<ConnectorTarget>(), It.IsAny<UpdateInstallScope>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConnectorResult<UpdateInstallResult>.Ok(new UpdateInstallResult
            {
                HostName = "HOTE01", InstalledCount = 2, FailedCount = 0, RebootRequired = true
            }, TimeSpan.FromMinutes(3)));

        var step = EtapeInstallation(ids.ComponentId, PorteeMajInstructions.Securite);

        // Act
        var issue = await h.Executeur.ExecuteAsync(step, isSimulation: false, Progress(), CancellationToken.None);

        // Assert : un redémarrage requis n'est JAMAIS un succès silencieux ni un
        // reboot — c'est un avertissement qui renvoie vers l'ordre N4.
        Assert.Equal(ExecutionStepState.Avertissement, issue.State);
        Assert.Contains("REDÉMARRAGE EST REQUIS", issue.Message);
        Assert.Contains("Kaleris", issue.Message);
    }

    [Fact]
    public async Task InstallerAsync_echec_connecteur_rend_un_echec()
    {
        // Arrange
        var h = new Harnais();
        var ids = await h.SeedPilotableAsync();
        h.Connector
            .Setup(c => c.InstallUpdatesAsync(It.IsAny<ConnectorTarget>(), It.IsAny<UpdateInstallScope>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConnectorResult<UpdateInstallResult>.Fail(
                ConnectorFailure.AccesRefuse, "Accès refusé (double saut).", TimeSpan.FromSeconds(1)));

        var step = EtapeInstallation(ids.ComponentId, PorteeMajInstructions.Securite);

        // Act
        var issue = await h.Executeur.ExecuteAsync(step, isSimulation: false, Progress(), CancellationToken.None);

        // Assert
        Assert.Equal(ExecutionStepState.Echec, issue.State);
        Assert.Equal(StepErrorType.CommandeRefusee, issue.ErrorType);
    }

    [Fact]
    public async Task InstallerAsync_en_simulation_n_emet_aucune_commande()
    {
        // Arrange
        var h = new Harnais();
        var ids = await h.SeedPilotableAsync();
        var step = EtapeInstallation(ids.ComponentId, PorteeMajInstructions.Securite);

        // Act
        var issue = await h.Executeur.ExecuteAsync(step, isSimulation: true, Progress(), CancellationToken.None);

        // Assert
        Assert.Equal(ExecutionStepState.Reussi, issue.State);
        Assert.Contains("[Simulation]", issue.Message);
        h.Connector.Verify(c => c.InstallUpdatesAsync(It.IsAny<ConnectorTarget>(), It.IsAny<UpdateInstallScope>(),
            It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, UpdateInstallScope.SecuriteEtCritiques)]
    [InlineData(true, UpdateInstallScope.Toutes)]
    public async Task InstallerAsync_transmet_la_portee_lue_dans_l_instruction(
        bool toutes, UpdateInstallScope attendue)
    {
        // Arrange
        var h = new Harnais();
        var ids = await h.SeedPilotableAsync();
        UpdateInstallScope? recue = null;
        h.Connector
            .Setup(c => c.InstallUpdatesAsync(It.IsAny<ConnectorTarget>(), It.IsAny<UpdateInstallScope>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<ConnectorTarget, UpdateInstallScope, IReadOnlyCollection<string>?, CancellationToken>(
                (_, s, _, _) => recue = s)
            .ReturnsAsync(ConnectorResult<UpdateInstallResult>.Ok(new UpdateInstallResult
            {
                HostName = "HOTE01", InstalledCount = 1, FailedCount = 0, RebootRequired = false
            }, TimeSpan.Zero));

        var instruction = toutes ? PorteeMajInstructions.Toutes : PorteeMajInstructions.Securite;
        var step = EtapeInstallation(ids.ComponentId, instruction);

        // Act
        await h.Executeur.ExecuteAsync(step, isSimulation: false, Progress(), CancellationToken.None);

        // Assert
        Assert.Equal(attendue, recue);
    }

    [Fact]
    public async Task InstallerAsync_refuse_un_composant_non_pilotable()
    {
        // Arrange
        var h = new Harnais();
        var ids = await h.SeedNonPilotableAsync();
        var step = EtapeInstallation(ids.ComponentId, PorteeMajInstructions.Securite);

        // Act
        var issue = await h.Executeur.ExecuteAsync(step, isSimulation: false, Progress(), CancellationToken.None);

        // Assert
        Assert.Equal(ExecutionStepState.Echec, issue.State);
        Assert.Contains("pas pilotable", issue.Message);
        h.Connector.Verify(c => c.InstallUpdatesAsync(It.IsAny<ConnectorTarget>(), It.IsAny<UpdateInstallScope>(),
            It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -----------------------------------------------------------------------
    // Construction + gouvernance (approbation selon l'environnement)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task BuildUpdateInstall_cree_une_etape_d_installation_avec_confirmation()
    {
        // Arrange
        var h = new Harnais();
        var ids = await h.SeedPilotableAsync();
        var adhoc = new AdHocOperationService(h.Factory, NullLogger<AdHocOperationService>.Instance);

        // Act
        var r = await adhoc.BuildUpdateInstallAsync(
            ids.EnvironmentId, ids.ComponentId, UpdateInstallScope.SecuriteEtCritiques, "admin");

        // Assert
        Assert.True(r.Succeeded);
        await using var db = h.Factory.CreateDbContext();
        var etapes = await db.WorkflowSteps.Where(s => s.WorkflowId == r.WorkflowId).ToListAsync();
        var etape = Assert.Single(etapes);
        Assert.Equal(StepAction.InstallerMiseAJour, etape.Action);
        Assert.True(etape.RequiresConfirmation);
        Assert.Equal(PorteeMajInstructions.Securite, etape.Instruction);
    }

    [Fact]
    public async Task Prepare_hors_production_ne_force_pas_d_approbation()
    {
        // Un admin seul doit pouvoir lancer en UAT sans second approbateur.
        var h = new Harnais();
        var ids = await h.SeedPilotableAsync(EnvironmentKind.UAT);
        var prep = await PreparerInstallationAsync(h, ids);

        Assert.True(prep.Succeeded);
        await using var db = h.Factory.CreateDbContext();
        var exec = await db.Executions.FirstAsync(e => e.Id == prep.ExecutionId);
        Assert.Equal(ExecutionStatus.EnPreparation, exec.Status);
        Assert.False(exec.RequiresDoubleApproval);
    }

    [Fact]
    public async Task Prepare_en_production_impose_l_approbation()
    {
        // La Production reste soumise à sa règle d'environnement : approbation imposée.
        var h = new Harnais();
        var ids = await h.SeedPilotableAsync(EnvironmentKind.Production);
        var prep = await PreparerInstallationAsync(h, ids);

        Assert.True(prep.Succeeded);
        await using var db = h.Factory.CreateDbContext();
        var exec = await db.Executions.FirstAsync(e => e.Id == prep.ExecutionId);
        Assert.Equal(ExecutionStatus.EnAttenteApprobation, exec.Status);
    }

    private static async Task<PrepareResult> PreparerInstallationAsync(Harnais h, SeededIds ids)
    {
        var adhoc = new AdHocOperationService(h.Factory, NullLogger<AdHocOperationService>.Instance);
        var build = await adhoc.BuildUpdateInstallAsync(
            ids.EnvironmentId, ids.ComponentId, UpdateInstallScope.SecuriteEtCritiques, "admin");

        var prepare = new PrepareExecutionUseCase(
            h.Factory, NullLogger<PrepareExecutionUseCase>.Instance,
            new ApprovalMatrixService(h.Factory));

        return await prepare.ExecuteAsync(build.WorkflowId, "admin", "Correctifs de sécurité mensuels");
    }

    // -----------------------------------------------------------------------
    // Outils
    // -----------------------------------------------------------------------
    private static IProgress<string> Progress() => new Progress<string>();

    private static ExecutionStep EtapeInstallation(Guid componentId, string instruction) => new()
    {
        ExecutionId = Guid.NewGuid(),
        Order = 1,
        Name = "Installer les mises à jour",
        Action = StepAction.InstallerMiseAJour,
        ComponentId = componentId,
        Instruction = instruction
    };

    private sealed record SeededIds(Guid EnvironmentId, Guid ServerId, Guid ComponentId);

    private sealed class Harnais
    {
        public Mock<IN4Connector> Connector { get; } = new();
        public IDbContextFactory<N4SentinelDbContext> Factory { get; }
        public StepExecutor Executeur { get; }

        public Harnais()
        {
            var options = new DbContextOptionsBuilder<N4SentinelDbContext>()
                .UseInMemoryDatabase($"maj-{Guid.NewGuid():N}")
                .Options;
            Factory = new FabriqueDeTest(options);

            var dossierCles = Path.Combine(Path.GetTempPath(), $"maj-cles-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dossierCles);
            var store = new CredentialStore(Factory,
                DataProtectionProvider.Create(new DirectoryInfo(dossierCles)),
                NullLogger<CredentialStore>.Instance);
            var cibles = new ConnectorTargetFactory(Factory, store, NullLogger<ConnectorTargetFactory>.Instance);
            var supervision = new SupervisionService(Factory, cibles, Connector.Object,
                NullLogger<SupervisionService>.Instance);

            Executeur = new StepExecutor(Factory, cibles, Connector.Object, supervision, store,
                NullLogger<StepExecutor>.Instance);
        }

        public Task<SeededIds> SeedPilotableAsync(EnvironmentKind kind = EnvironmentKind.UAT) =>
            SeedAsync(ControlMode.Pilotable, LifecycleStatus.Valide, kind);

        public Task<SeededIds> SeedNonPilotableAsync() =>
            SeedAsync(ControlMode.SuperviseSeulement, LifecycleStatus.Valide, EnvironmentKind.UAT);

        private async Task<SeededIds> SeedAsync(ControlMode mode, LifecycleStatus statut, EnvironmentKind kind)
        {
            await using var db = Factory.CreateDbContext();

            var env = new N4Environment { Id = Guid.NewGuid(), Code = "UAT", Name = "Recette", Kind = kind };
            var serveur = new N4Server { Id = Guid.NewGuid(), EnvironmentId = env.Id, HostName = "HOTE01" };
            var composant = new N4Component
            {
                Id = Guid.NewGuid(),
                EnvironmentId = env.Id,
                ServerId = serveur.Id,
                LogicalName = "Cluster-1",
                ControlMode = mode,
                Status = statut
            };

            db.Environments.Add(env);
            db.Servers.Add(serveur);
            db.Components.Add(composant);
            await db.SaveChangesAsync();

            return new SeededIds(env.Id, serveur.Id, composant.Id);
        }
    }

    private sealed class FabriqueDeTest(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
