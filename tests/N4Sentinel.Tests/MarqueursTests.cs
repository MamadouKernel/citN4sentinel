using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectivity;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests de l'assistant de relevé des marqueurs - REF-08.
///
/// Ils s'exécutent sur un journal écrit pour l'occasion, dans le format réel
/// d'un journal Navis : horodatage, niveau, thread, classe abrégée, message.
/// </summary>
public sealed class MarqueursTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _connectionString = string.Empty;
    private string _keyPath = string.Empty;
    private string _logPath = string.Empty;
    private TestDbContextFactory _factory = null!;
    private ReadinessDiscovery _discovery = null!;
    private Guid _componentId;

    /// <summary>Ligne de fin d'initialisation, telle que la documentation éditeur la décrit.</summary>
    private const string LigneMarqueur =
        "2026-08-13 06:04:10,900 INFO  [main] c.n.apex.WebTier - Web tier servlet 'action' initialized in 251090 ms";

    public async Task InitializeAsync()
    {
        _connectionString =
            $"Server=localhost;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<N4SentinelDbContext>()
            .UseSqlServer(_connectionString).Options);

        // Journal realiste : demarrage, marqueur unique, puis des battements
        // de coeur repetes - ce sont eux qu'il ne faut PAS retenir.
        _logPath = Path.Combine(Path.GetTempPath(), $"n4-marqueurs-{Guid.NewGuid():N}.log");
        await File.WriteAllLinesAsync(_logPath,
        [
            "2026-08-13 06:00:01,123 INFO  [main] com.navis.apex.Boot - Starting Apex node",
            "2026-08-13 06:00:04,880 INFO  [main] o.h.Version - HHH000412: Hibernate Core",
            "2026-08-13 06:01:12,004 WARN  [pool-2] c.n.cache.Region - Deprecated cache setting",
            "2026-08-13 06:02:41,551 ERROR [pool-3] c.n.edi.Poller - Connection refused: no further information",
            "2026-08-13 06:03:58,220 INFO  [main] c.n.apex.Cluster - Node joined cluster, members=3",
            LigneMarqueur,
            "2026-08-13 06:10:00,000 INFO  [sched] c.n.job.Runner - waiting for next scheduled run in 60 s",
            "2026-08-13 06:11:00,000 INFO  [sched] c.n.job.Runner - waiting for next scheduled run in 60 s",
            "2026-08-13 06:12:00,000 INFO  [sched] c.n.job.Runner - waiting for next scheduled run in 60 s"
        ]);

        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();

            var env = new N4Environment { Code = "TST", Name = "Test" };
            db.Environments.Add(env);
            await db.SaveChangesAsync();

            var serveur = new N4Server { EnvironmentId = env.Id, HostName = Environment.MachineName };
            db.Servers.Add(serveur);
            await db.SaveChangesAsync();

            var composant = new N4Component
            {
                EnvironmentId = env.Id,
                ServerId = serveur.Id,
                LogicalName = "Center Node",
                Role = ComponentRole.CenterNode,
                WindowsServiceName = "Winmgmt",
                ControlMode = ControlMode.Pilotable,
                Readiness = new ReadinessProfile { LogPath = _logPath }
            };
            db.Components.Add(composant);
            await db.SaveChangesAsync();
            _componentId = composant.Id;
        }

        _keyPath = Path.Combine(Path.GetTempPath(), $"n4-cles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keyPath);

        var store = new CredentialStore(_factory,
            DataProtectionProvider.Create(new DirectoryInfo(_keyPath)),
            NullLogger<CredentialStore>.Instance);

        _discovery = new ReadinessDiscovery(
            _factory,
            new ConnectorTargetFactory(_factory, store, NullLogger<ConnectorTargetFactory>.Instance),
            new PowerShellConnector(NullLogger<PowerShellConnector>.Instance));
    }

    public async Task DisposeAsync()
    {
        if (File.Exists(_logPath)) File.Delete(_logPath);
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

    [Fact(DisplayName = "Le motif propose correspond reellement a la ligne dont il est issu")]
    public async Task Le_Motif_Propose_Correspond_A_Sa_Ligne()
    {
        var r = await _discovery.AnalyzeAsync(_componentId);

        Assert.True(r.Succeeded, r.Error);
        Assert.NotEmpty(r.Candidates);

        var candidat = r.Candidates.FirstOrDefault(c => c.Message.Contains("servlet", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(candidat);

        // Le point central. Un motif construit a partir d'une ligne DOIT
        // retrouver cette ligne : sinon l'assistant propose un marqueur qui
        // ne servira jamais, et le composant restera "a confirmer" pour
        // toujours sans que personne ne comprenne pourquoi.
        Assert.Matches(candidat!.SuggestedPattern, LigneMarqueur);
    }

    [Fact(DisplayName = "Le motif survit au changement de la duree, qui varie a chaque demarrage")]
    public async Task Le_Motif_Est_Insensible_Aux_Nombres()
    {
        var r = await _discovery.AnalyzeAsync(_componentId);
        var candidat = r.Candidates.First(c => c.Message.Contains("servlet", StringComparison.OrdinalIgnoreCase));

        // Meme ligne, autre duree : un demarrage ulterieur doit etre reconnu.
        var autreDemarrage =
            "2026-09-01 08:15:33,010 INFO  [main] c.n.apex.WebTier - Web tier servlet 'action' initialized in 48221 ms";

        Assert.Matches(candidat.SuggestedPattern, autreDemarrage);
    }

    [Fact(DisplayName = "L'horodatage et le thread sont retires du motif")]
    public async Task L_Horodatage_N_Entre_Pas_Dans_Le_Motif()
    {
        var r = await _discovery.AnalyzeAsync(_componentId);
        var candidat = r.Candidates.First(c => c.Message.Contains("servlet", StringComparison.OrdinalIgnoreCase));

        // Un motif contenant la date du jour ne correspondrait a rien demain.
        Assert.DoesNotContain("2026", candidat.SuggestedPattern);
        Assert.DoesNotContain("main", candidat.SuggestedPattern);
        Assert.DoesNotContain("INFO", candidat.SuggestedPattern);
        Assert.StartsWith("Web", candidat.Message);
    }

    [Fact(DisplayName = "Une ligne repetee est signalee comme mauvais candidat")]
    public async Task Une_Ligne_Periodique_Est_Ecartee()
    {
        var r = await _discovery.AnalyzeAsync(_componentId);

        var battement = r.Candidates.FirstOrDefault(c => c.Message.Contains("waiting for next scheduled"));
        Assert.NotNull(battement);

        // Trois occurrences : c'est un battement de coeur. Le retenir ferait
        // declarer operationnel un composant a moitie charge.
        Assert.Equal(3, battement!.Occurrences);
        Assert.False(battement.IsGoodCandidate);

        var marqueur = r.Candidates.First(c => c.Message.Contains("servlet", StringComparison.OrdinalIgnoreCase));
        Assert.True(marqueur.IsGoodCandidate);

        // Les bons candidats remontent en tete.
        Assert.True(r.Candidates.IndexOf(marqueur) < r.Candidates.IndexOf(battement));
    }

    [Fact(DisplayName = "Un motif configure qui ne correspond a rien est signale")]
    public async Task Un_Motif_Inoperant_Est_Signale()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var c = await db.Components.SingleAsync(x => x.Id == _componentId);
            c.Readiness.ReadyPatterns = ["Web tier servlet initialized", @"Web tier servlet .*initialized in \d+ ms"];
            await db.SaveChangesAsync();
        }

        var r = await _discovery.AnalyzeAsync(_componentId);

        Assert.Equal(2, r.ConfiguredPatterns.Count);

        // Le premier omet la partie 'action' : il ne correspond a rien. C'est
        // exactement l'erreur que l'ecran doit rendre visible.
        var inoperant = r.ConfiguredPatterns.Single(p => p.Pattern == "Web tier servlet initialized");
        Assert.Equal(0, inoperant.MatchCount);

        var operant = r.ConfiguredPatterns.Single(p => p.Pattern.Contains(".*"));
        Assert.Equal(1, operant.MatchCount);
        Assert.True(r.HasWorkingPattern);
    }

    [Fact(DisplayName = "Une expression reguliere invalide n'interrompt pas l'analyse")]
    public async Task Un_Motif_Invalide_Est_Rapporte_Sans_Casser()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var c = await db.Components.SingleAsync(x => x.Id == _componentId);
            c.Readiness.ReadyPatterns = ["[non-ferme"];
            await db.SaveChangesAsync();
        }

        var r = await _discovery.AnalyzeAsync(_componentId);

        Assert.True(r.Succeeded, r.Error);
        Assert.True(r.ConfiguredPatterns.Single().IsInvalid);
        Assert.NotEmpty(r.Candidates);
    }

    [Fact(DisplayName = "Les lignes d'erreur du journal sont relevees")]
    public async Task Les_Erreurs_Sont_Relevees()
    {
        var r = await _discovery.AnalyzeAsync(_componentId);

        Assert.Contains(r.ErrorLines, l => l.Contains("Connection refused"));
    }

    [Fact(DisplayName = "Un composant sans chemin de journal donne une consigne, pas une erreur technique")]
    public async Task Sans_Chemin_De_Journal_On_Explique()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var c = await db.Components.SingleAsync(x => x.Id == _componentId);
            c.Readiness.LogPath = null;
            await db.SaveChangesAsync();
        }

        var r = await _discovery.AnalyzeAsync(_componentId);

        Assert.False(r.Succeeded);
        Assert.Contains("chemin de journal", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
