using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Referential;

namespace N4Sentinel.Tests;

/// <summary>
/// Suppression au référentiel : environnements, serveurs, composants.
///
/// POURQUOI CES TESTS EXISTENT. Trois manques se composaient en impasse, et
/// aucun test ne les voyait :
///
///   — `DeleteComponentAsync` existait sans qu'aucun écran ne l'appelle : un
///     composant ne pouvait pas être supprimé ;
///   — `DeleteServerAsync` refuse un serveur qui porte des composants — règle
///     saine, mais devenue infranchissable puisque les composants étaient
///     indélébiles ;
///   — `DeleteEnvironmentAsync` n'existait pas du tout.
///
/// Résultat sur le terrain : le référentiel se remplissait de saisies erronées
/// que personne ne pouvait retirer. Les tests unitaires du service passaient —
/// ils l'appelaient directement, là où l'interface n'offrait aucune porte.
///
/// La règle que ces tests verrouillent : ce qui n'a jamais servi se supprime,
/// ce qui porte une histoire se désactive.
/// </summary>
public sealed class SuppressionReferentielTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private TestDbContextFactory _factory = null!;

    private ReferentialService Service() =>
        new(_factory, NullLogger<ReferentialService>.Instance);

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

    // -----------------------------------------------------------------------
    private async Task<(Guid Env, Guid Serveur, Guid Composant)> EcosystemeAsync(
        LifecycleStatus statut = LifecycleStatus.Brouillon)
    {
        await using var db = _factory.CreateDbContext();

        var env = new N4Environment { Code = "UAT", Name = "Recette N4", Status = statut };
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var serveur = new N4Server { EnvironmentId = env.Id, HostName = "SRV-N4-01" };
        db.Servers.Add(serveur);
        await db.SaveChangesAsync();

        var composant = new N4Component
        {
            EnvironmentId = env.Id,
            ServerId = serveur.Id,
            LogicalName = "Center Node",
            Role = ComponentRole.CenterNode,
            WindowsServiceName = "Navis N4 Center Node"
        };
        db.Components.Add(composant);
        await db.SaveChangesAsync();

        return (env.Id, serveur.Id, composant.Id);
    }

    // -----------------------------------------------------------------------
    // L'impasse d'origine
    // -----------------------------------------------------------------------
    [Fact(DisplayName = "Un composant se supprime, ce qui débloque la suppression de son serveur")]
    public async Task L_Impasse_Est_Levee()
    {
        var (_, serveurId, composantId) = await EcosystemeAsync();
        var service = Service();

        // Tant que le composant est là, le serveur est protégé — règle saine.
        var refus = await service.DeleteServerAsync(serveurId);
        Assert.NotNull(refus);
        Assert.Contains("Center Node", refus);

        // Le composant part…
        Assert.Null(await service.DeleteComponentAsync(composantId));

        // …et le serveur devient supprimable. C'est la sortie qui manquait.
        Assert.Null(await service.DeleteServerAsync(serveurId));

        await using var db = _factory.CreateDbContext();
        Assert.Empty(await db.Servers.ToListAsync());
        Assert.Empty(await db.Components.ToListAsync());
    }

    [Fact(DisplayName = "Supprimer un composant emporte ses relevés de supervision et ses alertes")]
    public async Task Les_Mesures_Suivent_Leur_Objet()
    {
        var (envId, _, composantId) = await EcosystemeAsync();

        await using (var db = _factory.CreateDbContext())
        {
            db.ComponentSignals.Add(new ComponentSignal
            {
                EnvironmentId = envId,
                ComponentId = composantId,
                ComponentName = "Center Node",
                SignalType = "Service Windows",
                Target = "Navis N4 Center Node",
                Value = "Running",
                Quality = "Mesure"
            });
            db.Alerts.Add(new Alert
            {
                EnvironmentId = envId,
                ComponentId = composantId,
                Signature = "N4-STATUS-DISCONNECTED",
                Title = "Nœud déconnecté"
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(await Service().DeleteComponentAsync(composantId));

        await using var verif = _factory.CreateDbContext();
        Assert.Empty(await verif.ComponentSignals.ToListAsync());
        Assert.Empty(await verif.Alerts.ToListAsync());
    }

    [Fact(DisplayName = "Un composant piloté par un workflow est refusé, pas supprimé en silence")]
    public async Task Un_Composant_Cite_Par_Un_Workflow_Est_Protege()
    {
        var (envId, _, composantId) = await EcosystemeAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var wf = new Workflow { EnvironmentId = envId, Name = "Démarrage complet", Version = 1 };
            db.Workflows.Add(wf);
            await db.SaveChangesAsync();

            db.WorkflowSteps.Add(new WorkflowStep
            {
                WorkflowId = wf.Id,
                ComponentId = composantId,
                Order = 1,
                Name = "Démarrer le Center",
                Action = StepAction.Demarrer
            });
            await db.SaveChangesAsync();
        }

        var erreur = await Service().DeleteComponentAsync(composantId);

        Assert.NotNull(erreur);
        Assert.Contains("workflow", erreur, StringComparison.OrdinalIgnoreCase);
        // On oriente vers la sortie praticable plutôt que de laisser sur un refus.
        Assert.Contains("Désactivé", erreur);

        await using var verif = _factory.CreateDbContext();
        Assert.Single(await verif.Components.ToListAsync());
    }

    // -----------------------------------------------------------------------
    // Environnement : le geste le plus destructeur
    // -----------------------------------------------------------------------
    [Fact(DisplayName = "Un environnement Actif ne se supprime pas — il se désactive")]
    public async Task Un_Environnement_Actif_Est_Protege()
    {
        var (envId, _, _) = await EcosystemeAsync(LifecycleStatus.Actif);

        var erreur = await Service().DeleteEnvironmentAsync(envId, "UAT");

        Assert.NotNull(erreur);
        Assert.Contains("désactive", erreur, StringComparison.OrdinalIgnoreCase);

        await using var db = _factory.CreateDbContext();
        Assert.Single(await db.Environments.ToListAsync());
    }

    [Fact(DisplayName = "Sans le code retapé exactement, la suppression est refusée")]
    public async Task La_Confirmation_Est_Exigee()
    {
        var (envId, _, _) = await EcosystemeAsync();
        var service = Service();

        // La casse compte : « uat » n'est pas « UAT ». Les espaces autour, non —
        // un code recopié depuis l'écran traîne souvent une espace, et refuser
        // pour cela n'apporterait aucune sécurité, seulement de l'agacement.
        foreach (var saisie in new[] { "", "uat", "AUTRE", "UA" })
        {
            var erreur = await service.DeleteEnvironmentAsync(envId, saisie);
            Assert.NotNull(erreur);
        }

        await using var db = _factory.CreateDbContext();
        Assert.Single(await db.Environments.ToListAsync());
    }

    [Fact(DisplayName = "Un environnement jamais exploité se supprime avec sa configuration")]
    public async Task Un_Environnement_Neuf_Se_Supprime()
    {
        var (envId, _, _) = await EcosystemeAsync();

        await using (var db = _factory.CreateDbContext())
        {
            db.Credentials.Add(new TechnicalCredential
            {
                EnvironmentId = envId,
                Reference = "svc-uat",
                Label = "Compte UAT",
                Mode = CredentialMode.IdentiteDuProcessus
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(await Service().DeleteEnvironmentAsync(envId, "UAT"));

        await using var verif = _factory.CreateDbContext();
        Assert.Empty(await verif.Environments.ToListAsync());
        Assert.Empty(await verif.Servers.ToListAsync());
        Assert.Empty(await verif.Components.ToListAsync());
        Assert.Empty(await verif.Credentials.ToListAsync());

        // CE QUI DOIT SURVIVRE : la piste d'audit ne porte aucune clé étrangère
        // vers l'environnement, et garde donc trace de ce qui a été supprimé.
        Assert.NotEmpty(await verif.AuditEntries.ToListAsync());
    }

    [Fact(DisplayName = "Un environnement qui porte un historique d'exécution est refusé")]
    public async Task Un_Environnement_Avec_Historique_Est_Protege()
    {
        var (envId, _, _) = await EcosystemeAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var wf = new Workflow { EnvironmentId = envId, Name = "Démarrage complet", Version = 1 };
            db.Workflows.Add(wf);
            await db.SaveChangesAsync();

            db.Executions.Add(new WorkflowExecution
            {
                EnvironmentId = envId,
                EnvironmentCode = "UAT",
                WorkflowId = wf.Id,
                WorkflowName = "Démarrage complet",
                WorkflowVersion = 1,
                RequestedBy = "mkonate",
                CorrelationId = Guid.NewGuid().ToString("N")
            });
            await db.SaveChangesAsync();
        }

        var erreur = await Service().DeleteEnvironmentAsync(envId, "UAT");

        Assert.NotNull(erreur);
        Assert.Contains("exécution", erreur, StringComparison.OrdinalIgnoreCase);

        // L'historique d'exploitation est ce qui justifie l'existence de cette
        // application : il ne disparaît pas parce qu'on nettoie un référentiel.
        await using var verif = _factory.CreateDbContext();
        Assert.Single(await verif.Environments.ToListAsync());
        Assert.Single(await verif.Executions.ToListAsync());
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
