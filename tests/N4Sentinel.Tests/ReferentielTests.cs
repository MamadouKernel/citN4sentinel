using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Referential;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests d'integration du referentiel, executes contre une base SQL Server
/// reelle et jetable.
///
/// Pourquoi pas une base en memoire : l'intercepteur d'audit, les colonnes
/// RowVersion et les contraintes d'unicite sont precisement ce qu'on veut
/// verifier. Une base en memoire ne les implemente pas, et le test passerait
/// en donnant une fausse assurance.
/// </summary>
public sealed class ReferentielTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _connectionString = string.Empty;
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _connectionString =
            $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

        var options = TestDbContextOptions.Builder(_connectionString)
            .AddInterceptors(new AuditingInterceptor(new TestUserContext()))
            .Options;

        _factory = new TestDbContextFactory(options);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        // Nettoyage : on ne laisse pas des bases de test s'accumuler sur
        // l'instance de developpement.
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

    [Fact(DisplayName = "Creer un environnement produit une entree d'audit rattachee a son auteur")]
    public async Task Creation_Ecrit_Une_Entree_Audit()
    {
        var service = new ReferentialService(_factory);

        var environnement = new N4Environment
        {
            Code = "UAT",
            Name = "Recette N4",
            Kind = EnvironmentKind.UAT
        };
        await service.SaveEnvironmentAsync(environnement);

        await using var db = _factory.CreateDbContext();
        var audit = await db.AuditEntries.SingleAsync();

        Assert.Equal(AuditAction.Creation, audit.Action);
        Assert.Equal("N4Environment", audit.EntityType);
        Assert.Equal("UAT - Recette N4", audit.EntityLabel);
        Assert.Equal("test\\operateur", audit.Actor);
        Assert.Equal("10.0.0.42", audit.ActorIpAddress);
        Assert.Equal(environnement.Id, audit.EnvironmentId);

        // La date et l'auteur de creation sont horodates sans intervention
        // du code appelant.
        var enregistre = await db.Environments.SingleAsync();
        Assert.Equal("test\\operateur", enregistre.CreatedBy);
        Assert.NotEqual(default, enregistre.CreatedAt);
    }

    [Fact(DisplayName = "Une modification n'enregistre que les proprietes reellement changees")]
    public async Task Modification_Ne_Conserve_Que_Le_Delta()
    {
        var service = new ReferentialService(_factory);
        var env = new N4Environment { Code = "PROD", Name = "Production", Kind = EnvironmentKind.Production };
        await service.SaveEnvironmentAsync(env);

        env.Name = "Production Abidjan";
        await service.SaveEnvironmentAsync(env);

        await using var db = _factory.CreateDbContext();
        var modification = await db.AuditEntries
            .Where(a => a.Action == AuditAction.Modification)
            .SingleAsync();

        Assert.NotNull(modification.BeforeJson);
        Assert.NotNull(modification.AfterJson);
        Assert.Contains("Production", modification.BeforeJson);
        Assert.Contains("Production Abidjan", modification.AfterJson);

        // Le code n'a pas change : il ne doit pas figurer dans le delta.
        Assert.DoesNotContain("\"Code\"", modification.AfterJson);
    }

    [Fact(DisplayName = "Un environnement sans composant ne peut pas etre active")]
    public async Task Activation_Refusee_Sans_Composant()
    {
        var service = new ReferentialService(_factory);
        var env = new N4Environment { Code = "UAT", Name = "Recette", Status = LifecycleStatus.Valide };
        await service.SaveEnvironmentAsync(env);

        var erreur = await service.ChangeEnvironmentStatusAsync(env.Id, LifecycleStatus.Actif);

        Assert.NotNull(erreur);
        Assert.Contains("aucun composant", erreur, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Un composant pilotable sans service Windows bloque l'activation")]
    public async Task Activation_Refusee_Si_Composant_Pilotable_Sans_Service()
    {
        var service = new ReferentialService(_factory);
        var env = new N4Environment { Code = "UAT", Name = "Recette", Status = LifecycleStatus.Valide };
        await service.SaveEnvironmentAsync(env);

        await service.SaveComponentAsync(new N4Component
        {
            EnvironmentId = env.Id,
            LogicalName = "Center Node",
            Role = ComponentRole.CenterNode,
            ControlMode = ControlMode.Pilotable,
            WindowsServiceName = null
        });

        var erreur = await service.ChangeEnvironmentStatusAsync(env.Id, LifecycleStatus.Actif);

        Assert.NotNull(erreur);
        Assert.Contains("Center Node", erreur);
    }

    [Fact(DisplayName = "Le cycle de validation interdit les transitions directes")]
    public void Transitions_Respectent_Le_Cycle()
    {
        // Brouillon ne peut pas devenir Actif directement : il faut passer par
        // "A valider" puis "Valide" (FR-006).
        Assert.DoesNotContain(LifecycleStatus.Actif,
            ReferentialService.AllowedTransitions(LifecycleStatus.Brouillon));

        Assert.Contains(LifecycleStatus.AValider,
            ReferentialService.AllowedTransitions(LifecycleStatus.Brouillon));

        Assert.Contains(LifecycleStatus.Actif,
            ReferentialService.AllowedTransitions(LifecycleStatus.Valide));

        // Un environnement actif ne peut que sortir du service.
        Assert.Equal([LifecycleStatus.Desactive],
            ReferentialService.AllowedTransitions(LifecycleStatus.Actif));
    }

    [Fact(DisplayName = "L'import Navis-Config cree serveurs et composants, en dedupliquant les hotes")]
    public async Task Import_Navis_Config()
    {
        var service = new ReferentialService(_factory);
        var env = new N4Environment { Code = "UAT", Name = "Recette" };
        await service.SaveEnvironmentAsync(env);

        // Bridge et XPS partagent le meme hote, comme chez CIT.
        const string json = """
        {
          "CenterNode": "N4CENTER01",
          "StandbyNode": "N4STANDBY01",
          "ClusterNodes": [ "N4CLUSTER01", "N4CLUSTER02" ],
          "BridgeHost": "N4XPSBRIDGE01",
          "XPSHost": "N4XPSBRIDGE01",
          "ECN4Host": "N4ECN401",
          "ServiceNames": {
            "Center": "Navis N4 Center Node",
            "Cluster": "Navis N4 Cluster Node",
            "Standby": "Navis N4 Center Node",
            "Bridge": "Navis XPS Bridge Daemon",
            "XPS": "Navis XPS Service",
            "ECN4": "Navis ECN4 Daemon",
            "ECN4Web": "Navis ECN4web"
          },
          "DatabaseHost": "N4DB01",
          "DatabasePort": 1433,
          "DatabaseEngine": "SQL Server",
          "SharedFolder": "\\\\N4CLUSTER01\\NavisShared",
          "Readiness": {
            "Defaults": { "LogReadyTimeoutSeconds": 1800, "ErrorPatterns": [ "OutOfMemoryError" ] },
            "Components": {
              "Cluster": {
                "LogPath": "C:\\ProgramData\\Navis\\*\\logs\\navis-apex.log",
                "ReadyPatterns": [ "Web tier servlet .*initialized" ],
                "LogReadyTimeoutSeconds": 2400
              }
            }
          }
        }
        """;

        var importer = new NavisConfigImporter(_factory);
        var rapport = await importer.ImportAsync(env.Id, json);

        Assert.True(rapport.Succeeded);

        // Six hotes distincts pour sept references : Bridge et XPS partagent
        // la meme machine et ne doivent compter qu'une fois.
        Assert.Equal(6, rapport.ServersCreated);

        await using var db = _factory.CreateDbContext();
        var composants = await db.Components.Where(c => c.EnvironmentId == env.Id).ToListAsync();

        // 2 noeuds Cluster + Center + Standby + Bridge + XPS + ECN4 + ECN4Web
        // + base de donnees + dossier partage
        Assert.Equal(10, composants.Count);
        Assert.Contains(composants, c => c.LogicalName == "Center Node"
                                      && c.WindowsServiceName == "Navis N4 Center Node");

        // Le profil de demarrage est repris tel quel depuis le fichier.
        var noeud = composants.First(c => c.Role == ComponentRole.ClusterNode);
        Assert.Equal(2400, noeud.Readiness.LogReadyTimeoutSeconds);
        Assert.Contains("Web tier servlet .*initialized", noeud.Readiness.ReadyPatterns);
        Assert.Contains("OutOfMemoryError", noeud.Readiness.ErrorPatterns);
        Assert.True(noeud.Readiness.IsProvable);

        // Tout arrive en brouillon : l'import alimente, il ne valide pas.
        Assert.All(composants, c => Assert.Equal(LifecycleStatus.Brouillon, c.Status));

        // La base de donnees est supervisee, jamais pilotee.
        var bdd = composants.Single(c => c.Role == ComponentRole.BaseDeDonnees);
        Assert.Equal(ControlMode.SuperviseSeulement, bdd.ControlMode);
        Assert.False(bdd.CanBeControlled);
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
