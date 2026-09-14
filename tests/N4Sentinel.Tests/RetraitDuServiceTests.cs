using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Referential;

namespace N4Sentinel.Tests;

/// <summary>
/// Retrait du service : la suppression douce du référentiel.
///
/// CE QUE CES TESTS PROTÈGENT. Deux exigences s'opposaient — pouvoir nettoyer
/// un référentiel qui accumule les saisies erronées, et ne jamais rendre
/// illisible l'historique d'exploitation. Le retrait les satisfait ensemble :
/// la ligne demeure, les exécutions continuent de la désigner, et l'objet
/// disparaît de tout ce que l'application montre et pilote.
///
/// Le point le plus fragile de ce dispositif est l'invisibilité elle-même.
/// Elle repose sur un filtre global du contexte, et non sur la vigilance de
/// chaque requête : ces tests vérifient qu'il mord réellement, y compris là où
/// on l'oublierait le plus volontiers — la supervision de fond, qui pilote.
/// </summary>
public sealed class RetraitDuServiceTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private TestDbContextFactory _factory = null!;

    private ReferentialService Service() =>
        new(_factory, new MeriseBusinessPolicyService(_factory), NullLogger<ReferentialService>.Instance);

    public async Task InitializeAsync()
    {
        TestConnectionHelper.SkipIfUnavailable();
        var cs = TestConnectionHelper.BuildDatabaseConnectionString(_databaseName);

        var options = TestDbContextOptions.Builder(cs)
            .AddInterceptors(new AuditingInterceptor(new TestUserContext()))
            .Options;

        _factory = new TestDbContextFactory(options);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
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

    private async Task<(Guid Env, Guid Serveur, Guid Composant)> EcosystemeAsync(string code = "UAT")
    {
        await using var db = _factory.CreateDbContext();

        var env = new N4Environment { Code = code, Name = "Recette N4", Status = LifecycleStatus.Actif };
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var serveur = new N4Server { EnvironmentId = env.Id, HostName = $"SRV-{code}-01" };
        db.Servers.Add(serveur);
        await db.SaveChangesAsync();

        var composant = new N4Component
        {
            EnvironmentId = env.Id,
            ServerId = serveur.Id,
            LogicalName = "Center Node",
            Role = ComponentRole.CenterNode,
            WindowsServiceName = "Navis N4 Center Node",
            Status = LifecycleStatus.Actif
        };
        db.Components.Add(composant);
        await db.SaveChangesAsync();

        return (env.Id, serveur.Id, composant.Id);
    }

    // -----------------------------------------------------------------------
    [Fact(DisplayName = "Un environnement retiré disparaît partout, avec ses serveurs et composants")]
    public async Task Le_Retrait_Rend_Invisible_En_Cascade()
    {
        var (envId, _, _) = await EcosystemeAsync();

        Assert.Null(await Service().SoftDeleteEnvironmentAsync(envId, "UAT", "mkonate", "doublon de saisie"));

        await using var db = _factory.CreateDbContext();

        // Le filtre global vaut pour TOUTE requête ordinaire, sans que celle-ci
        // ait à s'en soucier : c'est ce qui empêche un oubli quelque part de
        // faire réapparaître — ou pire, piloter — un objet retiré.
        Assert.Empty(await db.Environments.ToListAsync());
        Assert.Empty(await db.Servers.ToListAsync());
        Assert.Empty(await db.Components.ToListAsync());

        // Mais rien n'a été effacé.
        Assert.Single(await db.Environments.IgnoreQueryFilters().ToListAsync());
        Assert.Single(await db.Servers.IgnoreQueryFilters().ToListAsync());
        Assert.Single(await db.Components.IgnoreQueryFilters().ToListAsync());
    }

    [Fact(DisplayName = "La supervision de fond cesse de voir un composant retiré")]
    public async Task La_Supervision_Ne_Voit_Plus_Un_Composant_Retire()
    {
        var (_, _, composantId) = await EcosystemeAsync();

        // La requête de la supervision, reproduite telle qu'elle est écrite :
        // elle ne mentionne nulle part le retrait, et c'est le point.
        async Task<int> ComptesSupervisables()
        {
            await using var db = _factory.CreateDbContext();
            return await db.Components
                .Include(c => c.Environment)
                .Where(c => c.Environment != null
                            && (c.Environment.Status == LifecycleStatus.Valide
                                || c.Environment.Status == LifecycleStatus.Actif))
                .CountAsync();
        }

        Assert.Equal(1, await ComptesSupervisables());

        Assert.Null(await Service().SoftDeleteComponentAsync(composantId, "mkonate"));

        // Un composant retiré qui resterait supervisé serait signalé en échec
        // à chaque passe, pour une panne que personne n'a provoquée.
        Assert.Equal(0, await ComptesSupervisables());
    }

    [Fact(DisplayName = "Le code d'un environnement retiré se réutilise immédiatement")]
    public async Task Le_Code_Se_Libere()
    {
        var (envId, _, _) = await EcosystemeAsync();
        Assert.Null(await Service().SoftDeleteEnvironmentAsync(envId, "UAT", "mkonate"));

        // Sans index unique partiel, la ligne retirée garderait « UAT » et
        // interdirait cette saisie — refus incompréhensible, puisque rien
        // n'est visible.
        await using var db = _factory.CreateDbContext();
        db.Environments.Add(new N4Environment { Code = "UAT", Name = "Recette N4 refaite" });

        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        Assert.Null(exception);
    }

    [Fact(DisplayName = "Rétablir un retrait rend exactement ce qu'il avait pris")]
    public async Task Le_Retablissement_Est_Exact()
    {
        var (envId, _, composantId) = await EcosystemeAsync();
        var service = Service();

        // Un premier retrait, délibéré et isolé.
        Assert.Null(await service.SoftDeleteComponentAsync(composantId, "mkonate", "composant en double"));

        // Puis le retrait de l'environnement entier.
        Assert.Null(await service.SoftDeleteEnvironmentAsync(envId, "UAT", "mkonate"));

        var lots = await service.GetDeletionBatchesAsync();
        Assert.Equal(2, lots.Count);

        var lotEnvironnement = lots.Single(l => l.Environments.Count > 0);
        Assert.Null(await service.RestoreAsync(lotEnvironnement.BatchId));

        await using var db = _factory.CreateDbContext();

        Assert.Single(await db.Environments.ToListAsync());
        Assert.Single(await db.Servers.ToListAsync());

        // LE POINT DU LOT : le composant retiré séparément, trois lignes plus
        // haut, ne doit PAS ressusciter. Sans lot, le rétablissement rendrait
        // ce que personne n'avait demandé de rendre.
        Assert.Empty(await db.Components.ToListAsync());
    }

    [Fact(DisplayName = "On ne retire pas un environnement sous une opération en cours")]
    public async Task Une_Operation_En_Cours_Bloque_Le_Retrait()
    {
        var (envId, _, _) = await EcosystemeAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var wf = new Workflow { EnvironmentId = envId, Name = "Démarrage", Version = 1 };
            db.Workflows.Add(wf);
            await db.SaveChangesAsync();

            db.Executions.Add(new WorkflowExecution
            {
                EnvironmentId = envId,
                EnvironmentCode = "UAT",
                WorkflowId = wf.Id,
                WorkflowName = "Démarrage",
                WorkflowVersion = 1,
                RequestedBy = "mkonate",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Status = ExecutionStatus.EnCours
            });
            await db.SaveChangesAsync();
        }

        var erreur = await Service().SoftDeleteEnvironmentAsync(envId, "UAT", "mkonate");

        Assert.NotNull(erreur);
        Assert.Contains("en cours", erreur, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Une opération préparée bloque aussi le retrait, pour ne pas éteindre ses contrôles")]
    public async Task Une_Operation_Preparee_Bloque_Le_Retrait()
    {
        var (envId, _, _) = await EcosystemeAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var wf = new Workflow { EnvironmentId = envId, Name = "Démarrage", Version = 1 };
            db.Workflows.Add(wf);
            await db.SaveChangesAsync();

            db.Executions.Add(new WorkflowExecution
            {
                EnvironmentId = envId,
                EnvironmentCode = "UAT",
                WorkflowId = wf.Id,
                WorkflowName = "Démarrage",
                WorkflowVersion = 1,
                RequestedBy = "mkonate",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Status = ExecutionStatus.EnPreparation
            });
            await db.SaveChangesAsync();
        }

        // Le pré-check lit execution.Environment pour savoir s'il s'agit de la
        // Production. Un environnement retiré rend cette navigation nulle, et
        // le contrôle du ticket ITSM se déclarerait « non applicable » au lieu
        // d'exiger le ticket. Un garde-fou qui s'éteint sans rien dire est pire
        // que pas de garde-fou.
        var erreur = await Service().SoftDeleteEnvironmentAsync(envId, "UAT", "mkonate");

        Assert.NotNull(erreur);
        Assert.Contains("préparée", erreur, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "L'historique d'exécution reste lisible après le retrait de l'environnement")]
    public async Task L_Historique_Survit()
    {
        var (envId, _, _) = await EcosystemeAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var wf = new Workflow { EnvironmentId = envId, Name = "Démarrage", Version = 1 };
            db.Workflows.Add(wf);
            await db.SaveChangesAsync();

            db.Executions.Add(new WorkflowExecution
            {
                EnvironmentId = envId,
                EnvironmentCode = "UAT",
                WorkflowId = wf.Id,
                WorkflowName = "Démarrage",
                WorkflowVersion = 1,
                RequestedBy = "mkonate",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Status = ExecutionStatus.TermineSucces
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(await Service().SoftDeleteEnvironmentAsync(envId, "UAT", "mkonate"));

        await using var verif = _factory.CreateDbContext();
        var execution = await verif.Executions.SingleAsync();

        // C'est toute la raison d'être du retrait plutôt que de l'effacement :
        // le rapport de cette opération se relit des mois plus tard, et le code
        // d'environnement y est dénormalisé pour survivre à sa disparition.
        Assert.Equal("UAT", execution.EnvironmentCode);
        Assert.Equal(ExecutionStatus.TermineSucces, execution.Status);
    }

    [Fact(DisplayName = "Purger n'est possible que depuis la corbeille")]
    public async Task La_Purge_Passe_Par_La_Corbeille()
    {
        var (envId, _, _) = await EcosystemeAsync();

        var erreur = await Service().DeleteEnvironmentAsync(envId, "UAT");

        Assert.NotNull(erreur);
        Assert.Contains("encore en service", erreur, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }

    private sealed class TestUserContext : ICurrentUserContext
    {
        public string Actor => "test\\operateur";
        public string? IpAddress => "10.0.0.42";
        public string? CorrelationId => "test-correlation";
    }
}
