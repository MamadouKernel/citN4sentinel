using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Orchestration;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// Suite de tests d'intégration pour les fonctionnalités des Lots 3 et 4 :
/// - Palier 2 (Orchestration Automatique Bout en Bout) & Fallback d'urgence 1-Click
/// - Authentification SSO Azure AD (SEC-001 V2)
///
/// La supervision JMX temps réel a été retirée (donnée de démonstration fixe
/// sans appel JMX/RMI réel, cf. décision #11 du plan de remédiation de l'audit
/// CIT-CIV-DSI-RFP-0010) : son test a été supprimé avec le code qu'il couvrait.
/// </summary>
public sealed class Palier2AndLot4Tests
{
    private const string ConnectionString =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    [Fact]
    public void AutomationLevel_Enum_Est_Bien_Defini()
    {
        Assert.Equal(1, (int)AutomationLevel.SemiAutomatique);
        Assert.Equal(2, (int)AutomationLevel.AutomatiqueBoutEnBout);
    }

    /// <summary>
    /// SEC-001 : aucun connecteur OIDC réel n'existe en V1. Simuler un succès
    /// fabriquerait une identité DSI à partir de n'importe quelle chaîne — la
    /// méthode doit donc toujours refuser, paramètres activés ou non.
    /// </summary>
    [Fact]
    public async Task AzureAdAuthProvider_Refuse_Toujours_Faute_De_Connecteur_Reel()
    {
        var dbName = $"n4test_azuread_{Guid.NewGuid():N}";
        var cs = $"Server=localhost;Database={dbName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
        var options = TestDbContextOptions.Builder(cs).Options;

        var factory = new LocalTestDbContextFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();

            var settingsService = new AzureAdSettingsService(factory, new AuditWriter(factory));
            await settingsService.SaveAsync(new AzureAdSettings { Enabled = true, TenantId = "cotedivoireterminal.onmicrosoft.com" }, "m.konate");

            var provider = new AzureAdAuthProvider(settingsService, NullLogger<AzureAdAuthProvider>.Instance);
            var userInfo = await provider.AuthenticateSsoTokenAsync("n'importe-quelle-chaine");

            Assert.Null(userInfo);

            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task OrchestrationEngine_Bascule_Urgence_Palier1_Fonctionne()
    {
        var dbName = $"n4test_palier2_{Guid.NewGuid():N}";
        var cs = $"Server=localhost;Database={dbName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
        var options = TestDbContextOptions.Builder(cs).Options;

        var factory = new LocalTestDbContextFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();

            var env = new N4Environment { Id = Guid.NewGuid(), Code = "PROD", Name = "Production N4", AutomationLevel = AutomationLevel.AutomatiqueBoutEnBout };
            var wf = new Workflow { Id = Guid.NewGuid(), EnvironmentId = env.Id, Code = "WF-START", Name = "Démarrage", AutomationLevel = AutomationLevel.AutomatiqueBoutEnBout };
            var exec = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                EnvironmentId = env.Id,
                WorkflowId = wf.Id,
                WorkflowName = wf.Name,
                AutomationLevel = AutomationLevel.AutomatiqueBoutEnBout,
                Status = ExecutionStatus.EnCours
            };

            db.Environments.Add(env);
            db.Workflows.Add(wf);
            db.Executions.Add(exec);
            await db.SaveChangesAsync();

            var engine = new OrchestrationEngine(
                new TestScopeFactory(factory),
                new N4Sentinel.Infrastructure.Observability.MetricsService(),
                NullLogger<OrchestrationEngine>.Instance);

            var result = await engine.ToggleFallbackToSemiAutoAsync(exec.Id, "M. KONATE (DSI)");

            Assert.True(result);

            var updatedExec = await db.Executions.AsNoTracking().FirstAsync(x => x.Id == exec.Id);
            Assert.Equal(AutomationLevel.SemiAutomatique, updatedExec.AutomationLevel);
            Assert.True(updatedExec.IsFallbackSemiAutoForced);

            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class LocalTestDbContextFactory(DbContextOptions<N4SentinelDbContext> options) : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }

    private sealed class TestScopeFactory(LocalTestDbContextFactory factory) : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => new TestScope(factory);
    }

    private sealed class TestScope(LocalTestDbContextFactory factory) : Microsoft.Extensions.DependencyInjection.IServiceScope
    {
        public IServiceProvider ServiceProvider => new TestServiceProvider(factory);
        public void Dispose() { }
    }

    private sealed class TestServiceProvider(LocalTestDbContextFactory factory) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IDbContextFactory<N4SentinelDbContext>)) return factory;
            return null;
        }
    }
}
