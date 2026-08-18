using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Referential;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests du parcours de mise en service - REF-09.
///
/// L'avancement est deduit du referentiel, jamais stocke. Ces tests verifient
/// que la deduction est juste : ce qui manque doit apparaitre, et ce qui est
/// simplement un choix - l'identite du processus plutot qu'un compte - ne doit
/// pas etre pris pour un oubli.
/// </summary>
public sealed class MiseEnServiceTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private TestDbContextFactory _factory = null!;
    private CommissioningStatus _status = null!;
    private Guid _envId;

    public async Task InitializeAsync()
    {
        TestConnectionHelper.SkipIfUnavailable();
        var cs = TestConnectionHelper.BuildDatabaseConnectionString(_databaseName);
        _factory = new TestDbContextFactory(TestDbContextOptions.Builder(cs).Options);
        _status = new CommissioningStatus(_factory);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        var env = new N4Environment { Code = "UAT", Name = "Recette N4", Kind = EnvironmentKind.UAT };
        db.Environments.Add(env);
        await db.SaveChangesAsync();
        _envId = env.Id;
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

    [Fact(DisplayName = "Un environnement vide signale qu'il manque serveurs et composants")]
    public async Task Environnement_Vide()
    {
        var r = await _status.EvaluateAsync(_envId);

        Assert.Equal(8, r.Total);
        Assert.Equal(StepState.Fait, r.Steps[0].State);      // l'environnement existe
        Assert.Equal(StepState.AFaire, r.Steps[1].State);    // aucun serveur
        Assert.Equal(StepState.AFaire, r.Steps[3].State);    // aucun composant

        Assert.NotNull(r.NextStep);
        Assert.Equal("Déclarer les serveurs", r.NextStep!.Title);
    }

    [Fact(DisplayName = "L'absence de compte technique n'est pas un manque : c'est le mode recommande")]
    public async Task Sans_Compte_L_Etape_Est_Consideree_Faite()
    {
        var r = await _status.EvaluateAsync(_envId);

        var etape = r.Steps.Single(s => s.Number == 3);
        Assert.Equal(StepState.Fait, etape.State);
        Assert.Contains("identité du processus", etape.Detail);
    }

    [Fact(DisplayName = "Un compte explicite sans mot de passe est bloquant")]
    public async Task Compte_Incomplet_Est_Bloquant()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Credentials.Add(new TechnicalCredential
            {
                EnvironmentId = _envId, Reference = "svc", Label = "Compte incomplet",
                Mode = CredentialMode.CompteExplicite, UserName = "CIT\\svc"
                // ProtectedPassword volontairement absent
            });
            await db.SaveChangesAsync();
        }

        var r = await _status.EvaluateAsync(_envId);

        Assert.Equal(StepState.Bloquant, r.Steps.Single(s => s.Number == 3).State);

        // Un point bloquant passe devant les etapes simplement non commencees.
        Assert.Equal("Définir le compte d'accès", r.NextStep!.Title);
    }

    [Fact(DisplayName = "Un composant pilotable sans nom de service est bloquant")]
    public async Task Composant_Sans_Service_Est_Bloquant()
    {
        await AjouterComposantAsync("Center Node", service: null, marqueur: false);

        var r = await _status.EvaluateAsync(_envId);

        var etape = r.Steps.Single(s => s.Number == 4);
        Assert.Equal(StepState.Bloquant, etape.State);
        Assert.Contains("jamais être pilotés", etape.Detail);
    }

    [Fact(DisplayName = "Un composant sans marqueur est signale, avec le lien vers l'assistant")]
    public async Task Composant_Sans_Marqueur_Renvoie_Vers_L_Assistant()
    {
        await AjouterComposantAsync("Center Node", service: "Navis N4 Center Node", marqueur: false);

        var r = await _status.EvaluateAsync(_envId);

        var etape = r.Steps.Single(s => s.Number == 5);
        Assert.Equal(StepState.AFaire, etape.State);
        Assert.Contains("marqueurs", etape.Link!);
        Assert.Contains("à confirmer", etape.Hint!);
    }

    [Fact(DisplayName = "Un environnement complet et actif atteint la fin du parcours")]
    public async Task Environnement_Complet()
    {
        await AjouterServeurAsync();
        await AjouterComposantAsync("Center Node", "Navis N4 Center Node", marqueur: true, ordre: 1);

        await using (var db = _factory.CreateDbContext())
        {
            var env = await db.Environments.SingleAsync();
            env.Status = LifecycleStatus.Actif;
            await db.SaveChangesAsync();
        }

        var r = await _status.EvaluateAsync(_envId);

        Assert.Equal(0, r.Blocking);
        Assert.Equal(StepState.Fait, r.Steps.Single(s => s.Number == 5).State);
        Assert.Equal(StepState.Fait, r.Steps.Single(s => s.Number == 8).State);

        // Le test de configuration reste toujours a faire : son resultat n'est
        // pas conserve, et un test passe hier ne dit rien de la configuration
        // modifiee ce matin.
        Assert.Equal(StepState.AFaire, r.Steps.Single(s => s.Number == 7).State);
        Assert.Equal(7, r.Done);
    }

    private async Task<Guid> AjouterServeurAsync()
    {
        await using var db = _factory.CreateDbContext();
        var s = new N4Server { EnvironmentId = _envId, HostName = "N4CENTER01" };
        db.Servers.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private async Task AjouterComposantAsync(string nom, string? service, bool marqueur, int ordre = 0)
    {
        await using var db = _factory.CreateDbContext();

        var serveur = await db.Servers.FirstOrDefaultAsync();
        var c = new N4Component
        {
            EnvironmentId = _envId,
            ServerId = serveur?.Id,
            LogicalName = nom,
            Role = ComponentRole.CenterNode,
            WindowsServiceName = service,
            ControlMode = ControlMode.Pilotable,
            StartOrder = ordre,
            Readiness = marqueur
                ? new ReadinessProfile
                {
                    LogPath = @"C:\ProgramData\Navis\center\logs\navis-apex.log",
                    ReadyPatterns = [@"Web tier servlet .*initialized"]
                }
                : new ReadinessProfile()
        };
        db.Components.Add(c);
        await db.SaveChangesAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
