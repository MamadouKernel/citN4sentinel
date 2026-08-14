using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Diagnostic;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests du diagnostic — DIA-01 à DIA-06, recette AC-08, AC-09, AC-11.
///
/// Deux comportements pèsent plus que les autres :
///
///   — le REGROUPEMENT. Quarante fois la même exception doit produire UN
///     constat vu quarante fois, pas quarante constats. Sans cela l'anomalie
///     rare, souvent la plus intéressante, se noie dans la répétition.
///
///   — le VERDICT « rien de concluant ». Il ne doit jamais se lire comme
///     « tout va bien ». Confondre les deux fait perdre une heure à quelqu'un
///     qui cherche une panne réelle.
/// </summary>
public sealed class DiagnosticTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private string _keyPath = string.Empty;
    private TestDbContextFactory _factory = null!;
    private SignatureCatalogue _catalogue = null!;
    private LogAnalysisService _analyse = null!;
    private DiagnosticSessionService _sessions = null!;

    private Guid _envId;
    private Guid _composantId;

    public async Task InitializeAsync()
    {
        var cs = $"Server=localhost;Database={_databaseName};Trusted_Connection=True;"
               + "TrustServerCertificate=True;MultipleActiveResultSets=True";

        _factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<N4SentinelDbContext>().UseSqlServer(cs).Options);

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
                LogicalName = "Center Node",
                Role = ComponentRole.CenterNode,
                WindowsServiceName = "Navis N4 Center Node",
                ControlMode = ControlMode.Pilotable,
                Status = LifecycleStatus.Valide
            };
            db.Components.Add(composant);
            await db.SaveChangesAsync();
            _composantId = composant.Id;
        }

        _keyPath = Path.Combine(Path.GetTempPath(), $"n4-cles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keyPath);

        var store = new CredentialStore(_factory,
            DataProtectionProvider.Create(new DirectoryInfo(_keyPath)),
            NullLogger<CredentialStore>.Instance);

        _catalogue = new SignatureCatalogue(_factory, NullLogger<SignatureCatalogue>.Instance);
        _sessions = new DiagnosticSessionService(_factory);

        _analyse = new LogAnalysisService(
            _factory,
            new ConnectorTargetFactory(_factory, store, NullLogger<ConnectorTargetFactory>.Instance),
            new ConnecteurMuet(),
            _catalogue,
            NullLogger<LogAnalysisService>.Instance);

        await _catalogue.SeedAsync();
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
    // DIA-03 — Catalogue de signatures
    // =======================================================================
    [Fact]
    public async Task Le_Catalogue_Est_Amorce_De_La_Documentation_Editeur()
    {
        var signatures = await _catalogue.GetAllAsync();

        Assert.NotEmpty(signatures);
        Assert.All(signatures, s => Assert.Equal(SignatureOrigin.Editeur, s.Origin));
        Assert.Contains(signatures, s => s.Code == "DB-CONN-REFUSED");
        Assert.Contains(signatures, s => s.Code == "LIC-EXPIRED");
    }

    [Fact]
    public async Task Un_Second_Amorcage_N_Ecrase_Pas_Les_Corrections_Du_Site()
    {
        // Un exploitant a corrige une expression qui produisait des faux
        // positifs chez lui. Le redemarrage ne doit pas l'annuler.
        var signature = (await _catalogue.GetAllAsync()).First(s => s.Code == "NET-TIMEOUT");
        signature.Pattern = @"MotifCorrigeParLeSite";
        Assert.Null(await _catalogue.SaveAsync(signature));

        var ajoutees = await _catalogue.SeedAsync();
        Assert.Equal(0, ajoutees);

        var relue = (await _catalogue.GetAllAsync()).First(s => s.Code == "NET-TIMEOUT");
        Assert.Equal("MotifCorrigeParLeSite", relue.Pattern);
    }

    [Fact]
    public async Task Une_Expression_Reguliere_Invalide_Est_Refusee()
    {
        var erreur = await _catalogue.SaveAsync(new DiagnosticSignature
        {
            Code = "TEST-INVALIDE",
            Name = "Motif cassé",
            Pattern = "[non-fermé",
            ConfidenceWeight = 50
        });

        Assert.NotNull(erreur);
        Assert.Contains("invalide", erreur!);
    }

    [Fact]
    public async Task Une_Signature_Concluante_Doit_Expliquer_Ce_Qu_Elle_Signifie()
    {
        // Sans explication, l'operateur lit un code et reste devant sa pile
        // d'exception : la signature ne rend aucun service.
        var erreur = await _catalogue.SaveAsync(new DiagnosticSignature
        {
            Code = "TEST-MUET",
            Name = "Signature sans explication",
            Pattern = "quelque chose",
            ConfidenceWeight = 90,
            Severity = SignatureSeverity.Erreur
        });

        Assert.NotNull(erreur);
        Assert.Contains("doit expliquer", erreur!);
    }

    [Fact]
    public async Task Une_Signature_Deja_Citee_Est_Desactivee_Et_Non_Supprimee()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", JournalAvecLicenceExpiree());

        var signature = (await _catalogue.GetAllAsync()).First(s => s.Code == "LIC-EXPIRED");
        var message = await _catalogue.DeleteAsync(signature.Id);

        Assert.NotNull(message);
        Assert.Contains("désactivée plutôt que", message!);

        var relue = await _catalogue.GetAsync(signature.Id);
        Assert.NotNull(relue);
        Assert.False(relue!.IsEnabled);
    }

    // =======================================================================
    // DIA-01 / DIA-02 — Import et analyse
    // =======================================================================
    [Fact]
    public async Task Une_Signature_Connue_Est_Reconnue_Avec_Son_Sens()
    {
        var sessionId = await CreerSessionAsync();
        var resultat = await _analyse.ImportAsync(sessionId, "app.log", JournalAvecLicenceExpiree());

        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var constat = session!.Findings.First(f => f.SignatureCode == "LIC-EXPIRED");

        Assert.Equal(DiagnosticDomain.Licence, constat.Domain);
        Assert.Equal(SignatureSeverity.Critique, constat.Severity);
        Assert.Contains("licence valide", constat.Meaning!);
        Assert.NotNull(constat.Remediation);
    }

    [Fact]
    public async Task Quarante_Occurrences_Identiques_Font_Un_Seul_Constat()
    {
        var lignes = Enumerable.Range(0, 40)
            .Select(i => $"2026-08-14 09:{i / 60:00}:{i % 60:00},100 ERROR [pool-{i}] c.n.Db - "
                       + $"java.net.SocketTimeoutException: Read timed out after {i * 7} ms");

        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", string.Join('\n', lignes));

        var session = await _sessions.GetAsync(sessionId);
        var constat = Assert.Single(session!.Findings, f => f.SignatureCode == "NET-TIMEOUT");

        Assert.Equal(40, constat.OccurrenceCount);
    }

    [Fact]
    public async Task Le_Constat_Porte_Les_Lignes_Qui_L_Encadrent()
    {
        const string journal = """
            2026-08-14 09:12:00,001 INFO  [main] Chargement de la configuration
            2026-08-14 09:12:00,050 INFO  [main] Ouverture du pool applicatif
            2026-08-14 09:12:01,118 ERROR [main] Login failed for user 'n4app'
            2026-08-14 09:12:01,200 INFO  [main] Nouvelle tentative dans 5 s
            """;

        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", journal);

        var session = await _sessions.GetAsync(sessionId);
        var constat = session!.Findings.First(f => f.SignatureCode == "DB-CONN-REFUSED");

        Assert.NotNull(constat.Context);
        Assert.Contains("Ouverture du pool applicatif", constat.Context!);
        Assert.Contains("Nouvelle tentative", constat.Context);
        Assert.Contains(">>", constat.Context);
        Assert.Equal(3, constat.FirstLineNumber);
    }

    [Fact]
    public async Task Une_Erreur_Non_Cataloguee_Est_Signalee_Quand_Meme()
    {
        // Ne rapporter que le connu ferait passer a cote de tout ce qui est
        // nouveau, c'est-a-dire de l'essentiel un jour de panne inedite.
        const string journal =
            "2026-08-14 09:12:01,118 ERROR [main] c.cit.Truc - Une avarie totalement inédite du module Zorglub";

        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", journal);

        var session = await _sessions.GetAsync(sessionId);
        var constat = Assert.Single(session!.Findings);

        Assert.Null(constat.SignatureCode);
        Assert.Equal(DiagnosticDomain.Indetermine, constat.Domain);
        Assert.Contains("inédite", constat.Title);
        Assert.Contains("non répertoriée", constat.Meaning!);
    }

    [Fact]
    public async Task Les_Lignes_Normales_Ne_Produisent_Aucun_Constat()
    {
        const string journal = """
            2026-08-14 09:12:01,003 INFO  [main] Loading configuration
            2026-08-14 09:12:02,441 INFO  [main] Web tier servlet 'action' initialized
            2026-08-14 09:12:03,010 DEBUG [pool-1] Cache warm-up completed
            """;

        var sessionId = await CreerSessionAsync();
        var resultat = await _analyse.ImportAsync(sessionId, "app.log", journal);

        Assert.True(resultat.Succeeded);
        Assert.Equal(0, resultat.FindingCount);
    }

    [Fact]
    public async Task Une_Ligne_Hors_Fenetre_N_Est_Pas_Analysee()
    {
        const string journal = """
            2026-08-10 08:00:00,000 ERROR [main] Login failed for user 'ancien'
            2026-08-14 09:12:01,118 ERROR [main] No space left on device
            """;

        var sessionId = await CreerSessionAsync(
            debut: new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));

        await _analyse.ImportAsync(sessionId, "app.log", journal);

        var session = await _sessions.GetAsync(sessionId);

        Assert.DoesNotContain(session!.Findings, f => f.SignatureCode == "DB-CONN-REFUSED");
        Assert.Contains(session.Findings, f => f.SignatureCode == "DISK-FULL");
    }

    // =======================================================================
    // DIA-04 — Masquage à l'ingestion (AC-11)
    // =======================================================================
    [Fact]
    public async Task Aucun_Secret_N_Entre_En_Base_Lors_D_Une_Ingestion()
    {
        const string journal = """
            2026-08-14 09:12:01,000 INFO  [main] jdbc:sqlserver://SRV-DB:1433;user=n4;password=Prod!2026
            2026-08-14 09:12:01,118 ERROR [main] Login failed for user 'n4app' password=Prod!2026
            2026-08-14 09:12:01,200 INFO  [main] Retry scheduled
            """;

        var sessionId = await CreerSessionAsync();
        var resultat = await _analyse.ImportAsync(sessionId, "app.log", journal);

        Assert.True(resultat.MaskedSecretCount >= 2);

        // Lecture DIRECTE en base : c'est le seul controle qui prouve que le
        // secret n'y est pas, plutot que simplement caché a l'affichage.
        await using var db = _factory.CreateDbContext();
        var constats = await db.Findings.AsNoTracking()
            .Where(f => f.SessionId == sessionId).ToListAsync();

        Assert.NotEmpty(constats);

        foreach (var f in constats)
        {
            Assert.DoesNotContain("Prod!2026", f.SampleLine);
            Assert.DoesNotContain("Prod!2026", f.Context ?? string.Empty);
            Assert.False(SecretMasker.ContientUnSecretApparent(f.SampleLine));
            Assert.False(SecretMasker.ContientUnSecretApparent(f.Context));
        }
    }

    // =======================================================================
    // DIA-05 — Verdict à quatre valeurs (AC-09)
    // =======================================================================
    [Fact]
    public async Task Un_Journal_Sain_Produit_Rien_De_Concluant_Et_Precise_Ses_Limites()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log",
            "2026-08-14 09:12:02,441 INFO  [main] Web tier servlet 'action' initialized");

        var session = await _analyse.ConcludeAsync(sessionId);

        Assert.Equal(DiagnosticVerdict.RienDeConcluant, session!.Verdict);

        // Le point qui compte : ne jamais se lire comme "tout va bien".
        Assert.Contains("N'EST PAS UN CERTIFICAT DE BONNE SANTÉ", session.VerdictExplanation!);
        Assert.Contains("Portée de ce constat", session.VerdictExplanation!);
    }

    [Fact]
    public async Task Une_Signature_Concluante_Donne_Une_Cause_Caracterisee()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", JournalAvecLicenceExpiree());

        var session = await _analyse.ConcludeAsync(sessionId);

        Assert.Equal(DiagnosticVerdict.CauseCaracterisee, session!.Verdict);
        Assert.Contains("licence", session.VerdictExplanation!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Des_Anomalies_Sans_Signature_Ne_Donnent_Pas_De_Cause()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log",
            "2026-08-14 09:12:01,118 ERROR [main] c.cit.Truc - Avarie inédite du module Zorglub");

        var session = await _analyse.ConcludeAsync(sessionId);

        Assert.Equal(DiagnosticVerdict.AnomaliesSansCause, session!.Verdict);
        Assert.Contains("serait une invention", session.VerdictExplanation!);
    }

    [Fact]
    public async Task Le_Verdict_Signale_Les_Sources_Non_Collectees()
    {
        var sessionId = await CreerSessionAsync();

        // Collecte vouee a l'echec : le composant n'a ni chemin de journal ni
        // serveur. La source est enregistree en echec, et le verdict doit le dire.
        await _analyse.CollectFromServerAsync(sessionId, _composantId);
        await _analyse.ImportAsync(sessionId, "app.log", JournalAvecLicenceExpiree());

        var session = await _analyse.ConcludeAsync(sessionId);

        Assert.Contains("n'ont pas pu être collectées", session!.VerdictExplanation!);
    }

    // =======================================================================
    // DIA-06 — Hypothèses
    // =======================================================================
    [Fact]
    public async Task Les_Hypotheses_Portent_Leurs_Preuves_Et_Sont_Classees()
    {
        const string journal = """
            2026-08-14 09:12:01,118 ERROR [main] Login failed for user 'n4app'
            2026-08-14 09:12:02,118 ERROR [main] Cannot open database "navis"
            2026-08-14 09:12:03,118 WARN  [main] Read timed out
            """;

        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", journal);

        var session = await _analyse.ConcludeAsync(sessionId);
        var hypotheses = session!.Hypotheses.OrderBy(h => h.Rank).ToList();

        Assert.NotEmpty(hypotheses);
        Assert.Equal(1, hypotheses[0].Rank);
        Assert.Equal(DiagnosticDomain.BaseDeDonnees, hypotheses[0].Domain);
        Assert.NotEmpty(hypotheses[0].Evidence);
        Assert.Contains("ligne", hypotheses[0].Evidence);

        // Classement decroissant : la plus probable en premier.
        for (var i = 1; i < hypotheses.Count; i++)
            Assert.True(hypotheses[i - 1].Confidence >= hypotheses[i].Confidence);
    }

    [Fact]
    public async Task La_Confiance_Ne_Depasse_Jamais_95_Pour_Cent()
    {
        // L'application n'a pas vu le systeme : elle a lu un fichier. Afficher
        // 100 % serait une affirmation qu'elle n'est pas en mesure de tenir.
        var journal = string.Join('\n', Enumerable.Repeat(
            "2026-08-14 09:12:01,118 ERROR [main] License expired for product N4", 5)
            .Concat(Enumerable.Repeat(
                "2026-08-14 09:12:02,118 ERROR [main] LicenseException: no valid license", 5)));

        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", journal);

        var session = await _analyse.ConcludeAsync(sessionId);

        Assert.All(session!.Hypotheses, h => Assert.InRange(h.Confidence, 1, 95));
    }

    [Fact]
    public async Task Une_Nouvelle_Conclusion_Remplace_Les_Hypotheses_Precedentes()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", JournalAvecLicenceExpiree());

        await _analyse.ConcludeAsync(sessionId);
        var apresPremiere = (await _sessions.GetAsync(sessionId))!.Hypotheses.Count;

        await _analyse.ConcludeAsync(sessionId);
        var apresSeconde = (await _sessions.GetAsync(sessionId))!.Hypotheses.Count;

        Assert.Equal(apresPremiere, apresSeconde);
    }

    // =======================================================================
    // Rapport
    // =======================================================================
    [Fact]
    public async Task Le_Rapport_Enonce_Ses_Limites_Et_Ne_Contient_Aucun_Secret()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log",
            "2026-08-14 09:12:01,118 ERROR [main] Login failed for user 'n4' password=Prod!2026");

        await _analyse.ConcludeAsync(sessionId);
        var rapport = await _sessions.BuildMarkdownAsync(sessionId);

        Assert.NotNull(rapport);
        Assert.DoesNotContain("Prod!2026", rapport!);
        Assert.False(SecretMasker.ContientUnSecretApparent(rapport));

        Assert.Contains("Journaux examinés", rapport);
        Assert.Contains("Portée de ce constat", rapport);
        Assert.Contains("Les secrets ont été masqués avant enregistrement", rapport);
    }

    [Fact]
    public async Task Le_Rapport_Presente_Les_Hypotheses_Comme_Contestables()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", JournalAvecLicenceExpiree());
        await _analyse.ConcludeAsync(sessionId);

        var rapport = await _sessions.BuildMarkdownAsync(sessionId);

        Assert.Contains("Elles peuvent être contestées", rapport!);
        Assert.Contains("Sur quoi elle repose", rapport);
    }

    // =======================================================================
    // Aides
    // =======================================================================
    private async Task<Guid> CreerSessionAsync(
        DateTimeOffset? debut = null, DateTimeOffset? fin = null) =>
        await _sessions.CreateAsync(_envId, "Incident du 14/08",
            "Le Center Node ne répond plus depuis 09 h.", "INC-2026-0814", "m.konate", debut, fin);

    private static string JournalAvecLicenceExpiree() => """
        2026-08-14 09:12:00,001 INFO  [main] Starting Navis N4 Center Node
        2026-08-14 09:12:01,118 ERROR [main] LicenseException: no valid license found for product N4
        2026-08-14 09:12:01,200 INFO  [main] Shutting down
        """;

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }

    /// <summary>Connecteur qui ne joint rien : les imports n'en ont pas besoin.</summary>
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
}
