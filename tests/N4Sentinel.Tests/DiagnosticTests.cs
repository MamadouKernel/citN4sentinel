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
    private DiagnosticSettingsService _parametres = null!;

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

        _analyse = new LogAnalysisService(
            _factory,
            new ConnectorTargetFactory(_factory, store, NullLogger<ConnectorTargetFactory>.Instance),
            new ConnecteurMuet(),
            _catalogue,
            new N4Sentinel.Infrastructure.Observability.MetricsService(),
            NullLogger<LogAnalysisService>.Instance);

        _sessions = new DiagnosticSessionService(_factory, _analyse, new AuditWriter(_factory));
        _parametres = new DiagnosticSettingsService(_factory, new AuditWriter(_factory));

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

    [Fact(DisplayName = "FR-075 : la signature de consommateur lent ActiveMQ reconnait le motif editeur et porte son sens")]
    public async Task Le_Catalogue_Reconnait_Un_Consommateur_Lent_ActiveMQ()
    {
        var sessionId = await CreerSessionAsync();
        var journal = "2026-08-14 09:00:00,000 WARN [main] Slow Consumer detected on queue INBOUND.VESSEL, prefetch exhausted\n";

        var resultat = await _analyse.ImportAsync(sessionId, "app.log", journal);
        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var constat = Assert.Single(session!.Findings);
        Assert.Equal("AMQ-SLOW-CONSUMER", constat.SignatureCode);
        Assert.Equal(DiagnosticDomain.ActiveMqKahaDb, constat.Domain);
        Assert.NotEmpty(constat.Meaning!);
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

    /// <summary>
    /// FR-069 : LIC-EXPIRED pèse 95, seule signature concluante d'un seul
    /// domaine — exactement le cas exceptionnel réservé à CauseConfirmee,
    /// distinct du cas ordinaire CauseCaracterisee (« très probable »).
    /// </summary>
    [Fact]
    public async Task Une_Signature_Concluante_Isolee_De_Poids_Maximal_Donne_Une_Cause_Confirmee()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", JournalAvecLicenceExpiree());

        var session = await _analyse.ConcludeAsync(sessionId);

        Assert.Equal(DiagnosticVerdict.CauseConfirmee, session!.Verdict);
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
    // FR-071 — Identification automatique
    // =======================================================================
    [Fact(DisplayName = "Un import identifie le composant quand le nom du service Windows figure dans le fichier")]
    public async Task ImportAsync_Identifie_Le_Composant_Depuis_Le_Nom_De_Service()
    {
        var sessionId = await CreerSessionAsync();

        var resultat = await _analyse.ImportAsync(
            sessionId, "Navis N4 Center Node - 2026-08-14.log", JournalAvecLicenceExpiree());

        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);
        Assert.Equal(_composantId, source.ComponentId);
        Assert.True(source.ComponentAutoDetected);
    }

    [Fact(DisplayName = "Un import identifie le composant depuis un motif de nom de fichier non ambigu")]
    public async Task ImportAsync_Identifie_Le_Composant_Depuis_Un_Motif_De_Role()
    {
        Guid bridgeId;
        await using (var db = _factory.CreateDbContext())
        {
            var bridge = new N4Component
            {
                EnvironmentId = _envId,
                LogicalName = "Bridge Daemon",
                Role = ComponentRole.BridgeDaemon,
                WindowsServiceName = "Navis Bridge Daemon",
                ControlMode = ControlMode.Pilotable,
                Status = LifecycleStatus.Valide
            };
            db.Components.Add(bridge);
            await db.SaveChangesAsync();
            bridgeId = bridge.Id;
        }

        var sessionId = await CreerSessionAsync();
        var resultat = await _analyse.ImportAsync(sessionId, "bridge-2026-08-14.log", "2026-08-14 09:00:00 INFO Started\n");

        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);
        Assert.Equal(bridgeId, source.ComponentId);
        Assert.True(source.ComponentAutoDetected);
    }

    [Fact(DisplayName = "Entre deux noms declares dont l'un contient l'autre, le plus specifique l'emporte")]
    public async Task ImportAsync_Prefere_Le_Nom_Le_Plus_Specifique_En_Cas_De_Chevauchement()
    {
        Guid ecn4Id, ecn4WebId;
        await using (var db = _factory.CreateDbContext())
        {
            var ecn4 = new N4Component
            {
                EnvironmentId = _envId,
                LogicalName = "ECN4",
                Role = ComponentRole.Ecn4,
                WindowsServiceName = "ECN4Service",
                ControlMode = ControlMode.Pilotable,
                Status = LifecycleStatus.Valide
            };
            var ecn4Web = new N4Component
            {
                EnvironmentId = _envId,
                LogicalName = "ECN4 Web",
                Role = ComponentRole.Ecn4Web,
                WindowsServiceName = "W3SVC",
                ControlMode = ControlMode.Pilotable,
                Status = LifecycleStatus.Valide
            };
            db.Components.AddRange(ecn4, ecn4Web);
            await db.SaveChangesAsync();
            ecn4Id = ecn4.Id;
            ecn4WebId = ecn4Web.Id;
        }

        var sessionId = await CreerSessionAsync();
        // "ECN4" est un sous-texte de "ecn4web" : si le premier composant
        // trouve l'emportait, ce journal serait attribue a tort a ECN4.
        var resultat = await _analyse.ImportAsync(sessionId, "ecn4web-2026-08-14.log", "2026-08-14 09:00:00 INFO Started\n");

        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);
        Assert.Equal(ecn4WebId, source.ComponentId);
        Assert.NotEqual(ecn4Id, source.ComponentId);
        Assert.True(source.ComponentAutoDetected);
    }

    [Fact(DisplayName = "Un nom de fichier ambigu ne devine aucun composant, plutot que de risquer un mauvais choix")]
    public async Task ImportAsync_N_Identifie_Rien_Quand_Ambigu()
    {
        var sessionId = await CreerSessionAsync();

        var resultat = await _analyse.ImportAsync(sessionId, "application.log", "2026-08-14 09:00:00 INFO Started\n");

        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);
        Assert.Null(source.ComponentId);
        Assert.False(source.ComponentAutoDetected);
    }

    [Fact(DisplayName = "Un composant choisi explicitement n'est jamais marque comme detecte automatiquement")]
    public async Task ImportAsync_Avec_Composant_Choisi_Ne_Marque_Pas_Auto_Detecte()
    {
        var sessionId = await CreerSessionAsync();

        var resultat = await _analyse.ImportAsync(
            sessionId, "application.log", JournalAvecLicenceExpiree(), _composantId);

        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);
        Assert.Equal(_composantId, source.ComponentId);
        Assert.False(source.ComponentAutoDetected);
    }

    // =======================================================================
    // FR-073 — Résumé
    // =======================================================================
    [Fact(DisplayName = "Le resume compte les niveaux, la periode couverte et la version detectee")]
    public async Task ImportAsync_Calcule_Le_Resume_Du_Journal()
    {
        var sessionId = await CreerSessionAsync();
        var journal = """
            2026-08-14 09:00:00,000 INFO  [main] Starting Navis N4 Center Node — version 4.0.8
            2026-08-14 09:00:05,000 WARN  [main] Slow initial connection to database
            2026-08-14 09:00:10,000 ERROR [main] LicenseException: no valid license found for product N4
            2026-08-14 09:00:11,000 INFO  [main] Retrying
            2026-08-14 09:05:00,000 INFO  [main] Shutting down
            """;

        var resultat = await _analyse.ImportAsync(sessionId, "app.log", journal);
        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);

        Assert.Equal(3, source.InfoCount);
        Assert.Equal(1, source.WarningCount);
        Assert.Equal(1, source.ErrorCount);
        Assert.Equal("4.0.8", source.DetectedVersion);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero), source.EarliestEntryAt);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 9, 5, 0, TimeSpan.Zero), source.LatestEntryAt);
    }

    [Fact(DisplayName = "Le resume couvre tout le journal, pas seulement les lignes retenues comme anomalies")]
    public async Task ImportAsync_Le_Resume_Couvre_Les_Lignes_Sans_Anomalie()
    {
        var sessionId = await CreerSessionAsync();
        // Aucune signature ni motif d'erreur ici : zero constat attendu,
        // mais le resume doit quand meme voir les 3 lignes INFO.
        var journal = """
            2026-08-14 08:00:00 INFO Démarrage
            2026-08-14 08:00:05 INFO Chargement de la configuration
            2026-08-14 08:00:10 INFO Prêt
            """;

        var resultat = await _analyse.ImportAsync(sessionId, "app.log", journal);
        Assert.Equal(0, resultat.FindingCount);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);
        Assert.Equal(3, source.InfoCount);
    }

    [Fact(DisplayName = "Le type de journal N4 est reconnu a ses marqueurs structurels, jamais devine sur le nom du fichier")]
    public async Task ImportAsync_Detecte_Le_Type_De_Journal_N4()
    {
        var sessionId = await CreerSessionAsync();
        var journal = """
            2026-08-14 09:00:00,000 INFO [main] Starting Navis N4 Center Node
            2026-08-14 09:00:01,000 WARN [main] Slow initial connection
            """;

        // Le nom du fichier ne dit rien de N4 : seul le contenu doit permettre
        // la detection, pas le nom "peu_importe.txt".
        var resultat = await _analyse.ImportAsync(sessionId, "peu_importe.txt", journal);
        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);
        Assert.Equal("Journal applicatif N4 (log4j)", source.DetectedLogType);
    }

    [Fact(DisplayName = "Un journal sans marqueur structurel reconnu ne se voit attribuer aucun type invente")]
    public async Task ImportAsync_Ne_Devine_Pas_Le_Type_De_Journal_Quand_Aucun_Motif_Ne_Correspond()
    {
        var sessionId = await CreerSessionAsync();
        var resultat = await _analyse.ImportAsync(sessionId, "app.log", "quelque chose sans structure reconnue\nune autre ligne\n");
        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);
        Assert.Null(source.DetectedLogType);
    }

    [Fact(DisplayName = "Le fuseau horaire n'est retenu que s'il est explicitement ecrit dans l'horodatage")]
    public async Task ImportAsync_Detecte_Le_Fuseau_Horaire_Explicite()
    {
        var sessionId = await CreerSessionAsync();
        var journal = "2026-08-14T09:00:00Z INFO [main] Starting\n";

        var resultat = await _analyse.ImportAsync(sessionId, "app.log", journal);
        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);
        Assert.Equal("UTC", source.DetectedTimeZone);
    }

    [Fact(DisplayName = "Sans fuseau ecrit dans l'horodatage, aucun fuseau n'est devine")]
    public async Task ImportAsync_Ne_Devine_Pas_Le_Fuseau_Horaire_Absent()
    {
        var sessionId = await CreerSessionAsync();
        var resultat = await _analyse.ImportAsync(sessionId, "app.log", "2026-08-14 09:00:00 INFO Starting\n");
        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);
        Assert.Null(source.DetectedTimeZone);
    }

    // =======================================================================
    // FR-065 — Seuils de corrélation administrables
    // =======================================================================
    [Fact(DisplayName = "Par defaut, les seuils correspondent aux valeurs historiques")]
    public async Task DiagnosticSettingsService_Renvoie_Les_Valeurs_Par_Defaut_Sans_Ligne_Enregistree()
    {
        var parametres = await _parametres.GetAsync();

        Assert.Equal(70, parametres.HypothesisEstablishedThreshold);
        Assert.Equal(90, parametres.ConclusiveSignatureConfidenceWeight);
        Assert.Equal(50, parametres.SeriousLeadConfidenceThreshold);
    }

    [Fact(DisplayName = "Un seuil hors de 1-100 est refuse")]
    public async Task DiagnosticSettingsService_Refuse_Un_Seuil_Hors_Bornes()
    {
        var erreur = await _parametres.SaveAsync(new DiagnosticSettings { SeriousLeadConfidenceThreshold = 0 }, "test");
        Assert.NotNull(erreur);
    }

    [Fact(DisplayName = "Abaisser le seuil de piste serieuse change reellement le verdict rendu, pas seulement la valeur enregistree")]
    public async Task ConcludeAsync_Le_Verdict_Suit_Le_Seuil_De_Piste_Serieuse_Configure()
    {
        // APP-UNCAUGHT porte un poids de confiance de 45 dans le catalogue :
        // sous le seuil par defaut (50), au-dessus d'un seuil abaisse a 40.
        const string journal = "2026-08-14 09:12:01,118 FATAL [main] Uncaught exception in worker thread\n";

        var sessionAvantId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionAvantId, "app.log", journal);
        var sessionAvant = await _analyse.ConcludeAsync(sessionAvantId);
        Assert.Equal(DiagnosticVerdict.AnomaliesSansCause, sessionAvant!.Verdict);

        Assert.Null(await _parametres.SaveAsync(
            new DiagnosticSettings { SeriousLeadConfidenceThreshold = 40 }, "test"));

        var sessionApresId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionApresId, "app.log", journal);
        var sessionApres = await _analyse.ConcludeAsync(sessionApresId);
        Assert.Equal(DiagnosticVerdict.PisteSerieuse, sessionApres!.Verdict);
    }

    [Fact(DisplayName = "EstEtablie suit le seuil transmis, jamais un seuil fige")]
    public void DiagnosticHypothesis_EstEtablie_Suit_Le_Seuil_Transmis()
    {
        var hypothese = new DiagnosticHypothesis { Confidence = 60 };

        Assert.False(hypothese.EstEtablie(70));
        Assert.True(hypothese.EstEtablie(50));
    }

    // =======================================================================
    // §3.18 — Classification de l'échec de collecte
    // =======================================================================
    [Fact(DisplayName = "Une collecte sans chemin de journal configure est classee dans le motif dedie, distinct du message libre")]
    public async Task CollectFromServerAsync_Classe_L_Echec_Quand_Aucun_Chemin_N_Est_Configure()
    {
        var sessionId = await CreerSessionAsync();

        var resultat = await _analyse.CollectFromServerAsync(sessionId, _composantId);

        Assert.False(resultat.Succeeded);

        var session = await _sessions.GetAsync(sessionId);
        var source = Assert.Single(session!.Sources);
        Assert.Equal(LogCollectionFailureReason.ControleNonConfigure, source.FailureReason);
        Assert.NotNull(source.Error);
    }

    // =======================================================================
    // FR-061 — Corrélation temporelle
    // =======================================================================
    [Fact(DisplayName = "La chronologie ajuste un constat de l'ecart d'horloge mesure a la collecte")]
    public void BuildTimeline_Ajuste_Le_Constat_De_L_Ecart_Mesure()
    {
        // Ecart mesure, mais negligeable (sous le seuil d'une seconde) : la
        // correction est appliquee ET l'incertitude qui en resulte est nulle.
        var source = new LogSource { Id = Guid.NewGuid(), ComponentName = "Center Node", ClockSkewSecondsAtCollection = 0.3 };
        var brut = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);
        var finding = new LogFinding { SourceId = source.Id, Title = "Erreur", FirstSeenAt = brut, Severity = SignatureSeverity.Erreur };

        var session = new DiagnosticSession();
        session.Sources.Add(source);
        session.Findings.Add(finding);

        var chronologie = DiagnosticSessionService.BuildTimeline(session);

        var entree = Assert.Single(chronologie);
        Assert.Equal(brut.AddSeconds(-0.3), entree.AdjustedTime);
        Assert.Equal(brut, entree.RawTime);
        Assert.False(entree.IsUncertain);
    }

    [Fact(DisplayName = "Un ecart important reste incertain meme corrige : la correction n'efface pas sa propre marge d'erreur")]
    public void BuildTimeline_Reste_Incertain_Malgre_Correction_D_Un_Grand_Ecart()
    {
        var source = new LogSource { Id = Guid.NewGuid(), ComponentName = "Center Node", ClockSkewSecondsAtCollection = 5.0 };
        var brut = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);
        var finding = new LogFinding { SourceId = source.Id, Title = "Erreur", FirstSeenAt = brut, Severity = SignatureSeverity.Erreur };

        var session = new DiagnosticSession();
        session.Sources.Add(source);
        session.Findings.Add(finding);

        var entree = Assert.Single(DiagnosticSessionService.BuildTimeline(session));

        // La correction est bien appliquee au temps ajuste...
        Assert.Equal(brut.AddSeconds(-5), entree.AdjustedTime);
        // ...mais un ecart de cette ampleur reste signale : sa propre mesure
        // n'est pas garantie stable entre l'instant mesure et celui du log.
        Assert.True(entree.IsUncertain);
    }

    [Fact(DisplayName = "Un constat sans mesure d'ecart d'horloge reste marque incertain")]
    public void BuildTimeline_Marque_Incertain_Sans_Mesure_D_Ecart()
    {
        var source = new LogSource { Id = Guid.NewGuid(), ComponentName = "XPS", ClockSkewSecondsAtCollection = null };
        var finding = new LogFinding { SourceId = source.Id, Title = "Erreur", FirstSeenAt = DateTimeOffset.UtcNow, Severity = SignatureSeverity.Erreur };

        var session = new DiagnosticSession();
        session.Sources.Add(source);
        session.Findings.Add(finding);

        var entree = Assert.Single(DiagnosticSessionService.BuildTimeline(session));
        Assert.True(entree.IsUncertain);
        Assert.Null(entree.ClockSkewSeconds);
    }

    [Fact(DisplayName = "La chronologie multi-sources ordonne par temps ajuste, pas par temps brut")]
    public void BuildTimeline_Ordonne_Par_Temps_Ajuste()
    {
        // Le Bridge (ecart +10s) rapporte un evenement dont l'horodatage BRUT
        // est POSTERIEUR a celui du Center — mais une fois l'ecart corrige,
        // il precede en realite.
        var center = new LogSource { Id = Guid.NewGuid(), ComponentName = "Center Node", ClockSkewSecondsAtCollection = 0 };
        var bridge = new LogSource { Id = Guid.NewGuid(), ComponentName = "Bridge Daemon", ClockSkewSecondsAtCollection = 10 };

        var base_ = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);
        var evenementCenter = new LogFinding { SourceId = center.Id, Title = "Perte de connexion Bridge", FirstSeenAt = base_.AddSeconds(20), Severity = SignatureSeverity.Erreur };
        var evenementBridge = new LogFinding { SourceId = bridge.Id, Title = "Timeout socket", FirstSeenAt = base_.AddSeconds(25), Severity = SignatureSeverity.Erreur };

        var session = new DiagnosticSession();
        session.Sources.Add(center);
        session.Sources.Add(bridge);
        session.Findings.Add(evenementCenter);
        session.Findings.Add(evenementBridge);

        var chronologie = DiagnosticSessionService.BuildTimeline(session);

        // Brut : Center (20s) avant Bridge (25s). Ajuste : Bridge (25-10=15s) avant Center (20-0=20s).
        Assert.Equal("Timeout socket", chronologie[0].Title);
        Assert.Equal("Perte de connexion Bridge", chronologie[1].Title);
    }

    // =======================================================================
    // FR-073 — Évolution temporelle (histogramme horaire)
    // =======================================================================
    [Fact(DisplayName = "L'histogramme regroupe les constats par heure, en tenant compte de l'ecart d'horloge mesure")]
    public void BuildHourlyHistogram_Groupe_Par_Heure_Ajustee()
    {
        var source = new LogSource { Id = Guid.NewGuid(), ComponentName = "Center Node", ClockSkewSecondsAtCollection = 0 };
        var base_ = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

        var session = new DiagnosticSession();
        session.Sources.Add(source);
        // Deux constats a 09h, un a 10h : deux tranches, la premiere avec 2.
        session.Findings.Add(new LogFinding { SourceId = source.Id, Title = "A", FirstSeenAt = base_.AddMinutes(5), Severity = SignatureSeverity.Erreur });
        session.Findings.Add(new LogFinding { SourceId = source.Id, Title = "B", FirstSeenAt = base_.AddMinutes(40), Severity = SignatureSeverity.Erreur });
        session.Findings.Add(new LogFinding { SourceId = source.Id, Title = "C", FirstSeenAt = base_.AddHours(1).AddMinutes(10), Severity = SignatureSeverity.Erreur });

        var histogramme = DiagnosticSessionService.BuildHourlyHistogram(session);

        Assert.Equal(2, histogramme.Count);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero), histogramme[0].Heure);
        Assert.Equal(2, histogramme[0].Nombre);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero), histogramme[1].Heure);
        Assert.Equal(1, histogramme[1].Nombre);
    }

    [Fact(DisplayName = "Un constat sans horodatage n'entre dans aucune tranche de l'histogramme")]
    public void BuildHourlyHistogram_Ignore_Les_Constats_Sans_Horodatage()
    {
        var source = new LogSource { Id = Guid.NewGuid(), ComponentName = "Center Node" };
        var session = new DiagnosticSession();
        session.Sources.Add(source);
        session.Findings.Add(new LogFinding { SourceId = source.Id, Title = "Sans horodatage", FirstSeenAt = null, Severity = SignatureSeverity.Erreur });

        Assert.Empty(DiagnosticSessionService.BuildHourlyHistogram(session));
    }

    // =======================================================================
    // FR-066 — Comparaison avec une référence
    // =======================================================================
    [Fact(DisplayName = "Une session non analysee ne peut pas devenir reference")]
    public async Task MarkAsBaselineAsync_Refuse_Une_Session_Non_Analysee()
    {
        var sessionId = await CreerSessionAsync();

        var erreur = await _sessions.MarkAsBaselineAsync(sessionId, true);

        Assert.NotNull(erreur);
    }

    [Fact(DisplayName = "Une session ne peut pas etre comparee a une reference non marquee saine")]
    public async Task SetReferenceAsync_Refuse_Une_Reference_Non_Marquee()
    {
        var sessionId = await CreerSessionAsync();
        var autreId = await CreerSessionAsync();
        await _analyse.ImportAsync(autreId, "app.log", JournalAvecLicenceExpiree());
        await _analyse.ConcludeAsync(autreId);
        // autreId n'est PAS marquee IsReferenceBaseline.

        var erreur = await _sessions.SetReferenceAsync(sessionId, autreId);

        Assert.NotNull(erreur);
        Assert.Contains("pas marquée", erreur);
    }

    [Fact(DisplayName = "La comparaison signale les constats absents de la reference, et l'age de celle-ci")]
    public async Task CompareToReferenceAsync_Signale_Les_Nouveaux_Constats_Et_L_Age()
    {
        var referenceId = await CreerSessionAsync();
        await _analyse.ImportAsync(referenceId, "app.log",
            "2026-08-14 08:00:00 INFO [main] Startup complete\n");
        await _analyse.ConcludeAsync(referenceId);
        Assert.Null(await _sessions.MarkAsBaselineAsync(referenceId, true));

        // CreatedAt n'est renseigne que par AuditingInterceptor, absent de ce
        // contexte de test : sans ce reglage direct, la reference paraitrait
        // vieille de deux mille ans et fausserait le test de fraicheur.
        await using (var db = _factory.CreateDbContext())
        {
            var reference = await db.Sessions.FirstAsync(s => s.Id == referenceId);
            reference.CreatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var incidentId = await CreerSessionAsync();
        await _analyse.ImportAsync(incidentId, "app.log", JournalAvecLicenceExpiree());
        await _analyse.ConcludeAsync(incidentId);
        Assert.Null(await _sessions.SetReferenceAsync(incidentId, referenceId));

        var comparaison = await _sessions.CompareToReferenceAsync(incidentId);

        Assert.NotNull(comparaison);
        Assert.NotEmpty(comparaison!.NewFindings);
        Assert.Contains(comparaison.NewFindings, f => f.Title.Contains("Licence") || f.SampleLine.Contains("License"));
        Assert.False(comparaison.IsStale); // reference toute fraiche
        Assert.False(comparaison.ReferenceScopeIncomplete);
    }

    // =======================================================================
    // FR-066 — 3 modes de comparaison supplementaires (execution reussie,
    // valeurs habituelles du composant, noeud pair)
    // =======================================================================
    [Fact(DisplayName = "Seule une execution terminee avec succes, dans le meme environnement, peut servir de reference")]
    public async Task SetReferenceExecutionAsync_Refuse_Une_Execution_Non_Reussie_Ou_D_Un_Autre_Environnement()
    {
        var sessionId = await CreerSessionAsync();
        Guid executionEchouee, executionAutreEnv;

        await using (var db = _factory.CreateDbContext())
        {
            executionEchouee = await CreerExecutionAsync(db, _envId, ExecutionStatus.Echec, ["Center Node"]);

            var autreEnv = new N4Environment { Code = "PROD", Name = "Production", Kind = EnvironmentKind.Production };
            db.Environments.Add(autreEnv);
            await db.SaveChangesAsync();
            executionAutreEnv = await CreerExecutionAsync(db, autreEnv.Id, ExecutionStatus.TermineSucces, ["Center Node"]);
        }

        Assert.Contains("succès", await _sessions.SetReferenceExecutionAsync(sessionId, executionEchouee));
        Assert.Contains("environnement", await _sessions.SetReferenceExecutionAsync(sessionId, executionAutreEnv));
    }

    [Fact(DisplayName = "La comparaison a une execution reussie montre les composants de l'incident deja touches par cette execution")]
    public async Task CompareToSuccessfulExecutionAsync_Signale_Les_Composants_Partages()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", JournalAvecLicenceExpiree());
        // La source importee sans collecte ciblee n'a pas de ComponentId : on le force,
        // comme le ferait une collecte ciblee reelle depuis le composant enregistre.
        await using (var db0 = _factory.CreateDbContext())
        {
            var source = await db0.Sources.FirstAsync(s => s.SessionId == sessionId);
            source.ComponentId = _composantId;
            source.ComponentName = "Center Node";
            await db0.SaveChangesAsync();
        }

        Guid executionId;
        await using (var db = _factory.CreateDbContext())
            executionId = await CreerExecutionAsync(db, _envId, ExecutionStatus.TermineSucces, ["Center Node", "Cluster Node 1"]);

        var candidats = await _sessions.GetSuccessfulExecutionCandidatesAsync(_envId);
        Assert.Contains(candidats, e => e.Id == executionId);

        Assert.Null(await _sessions.SetReferenceExecutionAsync(sessionId, executionId));

        var comparaison = await _sessions.CompareToSuccessfulExecutionAsync(sessionId);

        Assert.NotNull(comparaison);
        Assert.Contains("Center Node", comparaison!.SharedIncidentComponentNames);
        Assert.DoesNotContain("Cluster Node 1", comparaison.SharedIncidentComponentNames);
        Assert.False(comparaison.IsStale);
    }

    [Fact(DisplayName = "Les valeurs habituelles du composant signalent un ecart quand le relevé pendant l'incident differe de l'historique")]
    public async Task CompareToUsualValuesAsync_Signale_Un_Ecart_Avec_L_Historique()
    {
        var debutIncident = DateTimeOffset.UtcNow.AddHours(-1);
        var finIncident = DateTimeOffset.UtcNow;
        var sessionId = await CreerSessionAsync(debutIncident, finIncident);

        await using (var db = _factory.CreateDbContext())
        {
            for (var i = 1; i <= 5; i++)
            {
                db.ComponentSignals.Add(new ComponentSignal
                {
                    ComponentId = _composantId,
                    EnvironmentId = _envId,
                    ComponentName = "Center Node",
                    SignalType = "Service Windows",
                    Target = "Navis N4 Center Node",
                    Value = "Running",
                    CapturedAt = debutIncident.AddDays(-i),
                    Quality = "Confirmé"
                });
            }
            db.ComponentSignals.Add(new ComponentSignal
            {
                ComponentId = _composantId,
                EnvironmentId = _envId,
                ComponentName = "Center Node",
                SignalType = "Service Windows",
                Target = "Navis N4 Center Node",
                Value = "Stopped",
                CapturedAt = debutIncident.AddMinutes(10),
                Quality = "Confirmé"
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(await _sessions.SetReferenceComponentAsync(sessionId, _composantId));

        var comparaison = await _sessions.CompareToUsualValuesAsync(sessionId, _composantId);

        Assert.NotNull(comparaison);
        Assert.False(comparaison!.IsIncomplete);
        var signal = Assert.Single(comparaison.Signals);
        Assert.Equal("Running", signal.ReferenceValue);
        Assert.Equal("Stopped", signal.ObservedValue);
        Assert.True(signal.Differs);
    }

    [Fact(DisplayName = "Un historique trop court est signale incomplet plutot que pris pour une habitude etablie")]
    public async Task CompareToUsualValuesAsync_Signale_Un_Historique_Insuffisant()
    {
        var sessionId = await CreerSessionAsync();

        await using (var db = _factory.CreateDbContext())
        {
            // CreatedAt n'est renseigne que par AuditingInterceptor, absent de ce
            // contexte de test (voir le test de fraicheur FR-066 ci-dessus) :
            // sans ce reglage direct, la fenetre par defaut (CreatedAt ± 30 min)
            // deborderait de DateTimeOffset.MinValue.
            var session = await db.Sessions.FirstAsync(s => s.Id == sessionId);
            session.CreatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            db.ComponentSignals.Add(new ComponentSignal
            {
                ComponentId = _composantId,
                EnvironmentId = _envId,
                ComponentName = "Center Node",
                SignalType = "Service Windows",
                Target = "Navis N4 Center Node",
                Value = "Running",
                CapturedAt = DateTimeOffset.UtcNow.AddDays(-10),
                Quality = "Confirmé"
            });
            await db.SaveChangesAsync();
        }

        var comparaison = await _sessions.CompareToUsualValuesAsync(sessionId, _composantId);

        Assert.NotNull(comparaison);
        Assert.True(comparaison!.IsIncomplete);
    }

    [Fact(DisplayName = "Les candidats noeud pair sont du meme role et du meme environnement, jamais le composant lui-meme")]
    public async Task GetPeerCandidatesAsync_Ne_Propose_Que_Le_Meme_Role_Et_Environnement()
    {
        Guid pairId, autreRoleId, autreEnvId;
        await using (var db = _factory.CreateDbContext())
        {
            var pair = new N4Component
            {
                EnvironmentId = _envId, LogicalName = "Standby Center Node", Role = ComponentRole.CenterNode,
                ControlMode = ControlMode.Pilotable, Status = LifecycleStatus.Valide
            };
            var autreRole = new N4Component
            {
                EnvironmentId = _envId, LogicalName = "Bridge", Role = ComponentRole.BridgeDaemon,
                ControlMode = ControlMode.Pilotable, Status = LifecycleStatus.Valide
            };
            db.Components.AddRange(pair, autreRole);
            await db.SaveChangesAsync();
            pairId = pair.Id;
            autreRoleId = autreRole.Id;

            var autreEnv = new N4Environment { Code = "PROD", Name = "Production", Kind = EnvironmentKind.Production };
            db.Environments.Add(autreEnv);
            await db.SaveChangesAsync();
            var composantAutreEnv = new N4Component
            {
                EnvironmentId = autreEnv.Id, LogicalName = "Center Node PROD", Role = ComponentRole.CenterNode,
                ControlMode = ControlMode.Pilotable, Status = LifecycleStatus.Valide
            };
            db.Components.Add(composantAutreEnv);
            await db.SaveChangesAsync();
            autreEnvId = composantAutreEnv.Id;
        }

        var candidats = await _sessions.GetPeerCandidatesAsync(_composantId);

        Assert.Contains(candidats, c => c.Id == pairId);
        Assert.DoesNotContain(candidats, c => c.Id == autreRoleId);
        Assert.DoesNotContain(candidats, c => c.Id == autreEnvId);
        Assert.DoesNotContain(candidats, c => c.Id == _composantId);
    }

    [Fact(DisplayName = "La comparaison a un noeud pair montre ce que le pair affichait sur la meme fenetre que l'incident")]
    public async Task CompareToPeerNodeAsync_Compare_Sur_La_Meme_Fenetre()
    {
        var debutIncident = DateTimeOffset.UtcNow.AddHours(-1);
        var finIncident = DateTimeOffset.UtcNow;
        var sessionId = await CreerSessionAsync(debutIncident, finIncident);

        Guid pairId;
        await using (var db = _factory.CreateDbContext())
        {
            var pair = new N4Component
            {
                EnvironmentId = _envId, LogicalName = "Standby Center Node", Role = ComponentRole.CenterNode,
                ControlMode = ControlMode.Pilotable, Status = LifecycleStatus.Valide
            };
            db.Components.Add(pair);
            await db.SaveChangesAsync();
            pairId = pair.Id;

            for (var i = 0; i < 4; i++)
            {
                db.ComponentSignals.Add(new ComponentSignal
                {
                    ComponentId = pairId, EnvironmentId = _envId, ComponentName = pair.LogicalName,
                    SignalType = "Service Windows", Target = "Navis N4 Standby Center Node", Value = "Running",
                    CapturedAt = debutIncident.AddMinutes(5 * i), Quality = "Confirmé"
                });
            }
            db.ComponentSignals.Add(new ComponentSignal
            {
                ComponentId = _composantId, EnvironmentId = _envId, ComponentName = "Center Node",
                SignalType = "Service Windows", Target = "Navis N4 Center Node", Value = "Stopped",
                CapturedAt = debutIncident.AddMinutes(10), Quality = "Confirmé"
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(await _sessions.SetReferenceComponentAsync(sessionId, pairId));

        var comparaison = await _sessions.CompareToPeerNodeAsync(sessionId, _composantId, pairId);

        Assert.NotNull(comparaison);
        Assert.False(comparaison!.IsIncomplete);
        var signal = Assert.Single(comparaison.Signals);
        Assert.Equal("Running", signal.ReferenceValue);
        Assert.Equal("Stopped", signal.ObservedValue);
        Assert.True(signal.Differs);
    }

    private static async Task<Guid> CreerExecutionAsync(
        N4SentinelDbContext db, Guid environmentId, ExecutionStatus statut, string[] composants)
    {
        var workflow = new Workflow
        {
            EnvironmentId = environmentId,
            Code = $"WF{Guid.NewGuid():N}"[..8],
            Name = "Démarrage complet",
            Kind = WorkflowKind.DemarrageComplet,
            Status = LifecycleStatus.Valide
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var execution = new WorkflowExecution
        {
            EnvironmentId = environmentId,
            EnvironmentCode = "ENV",
            WorkflowId = workflow.Id,
            WorkflowVersion = 1,
            WorkflowName = workflow.Name,
            Status = statut,
            RequestedBy = "op1",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            EndedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var ordre = 0;
        foreach (var nom in composants)
        {
            execution.Steps.Add(new ExecutionStep
            {
                Order = ordre++,
                Name = $"Démarrer {nom}",
                Action = StepAction.Demarrer,
                ComponentName = nom,
                State = ExecutionStepState.Reussi
            });
        }
        db.Executions.Add(execution);
        await db.SaveChangesAsync();
        return execution.Id;
    }

    // =======================================================================
    // FR-068 — Collecte automatique et ciblée depuis une alerte
    // =======================================================================
    [Fact(DisplayName = "Une alerte de composant declenche la collecte directement sur ce composant")]
    public async Task CreateFromAlertAsync_Collecte_Le_Composant_De_L_Alerte()
    {
        Guid alertId;
        await using (var db = _factory.CreateDbContext())
        {
            var alerte = new Alert
            {
                EnvironmentId = _envId,
                ComponentId = _composantId,
                ComponentName = "Center Node",
                Kind = AlertKind.Indisponibilite,
                Severity = AlertSeverity.Critique,
                Title = "Center Node indisponible",
                Detail = "Le service ne répond plus.",
                FirstOccurredAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            };
            db.Alerts.Add(alerte);
            await db.SaveChangesAsync();
            alertId = alerte.Id;
        }

        var resultat = await _sessions.CreateFromAlertAsync(alertId, "m.konate");

        Assert.True(resultat.Succeeded, resultat.Error);
        Assert.Equal(1, resultat.ComponentsCollected);

        var session = await _sessions.GetAsync(resultat.SessionId);
        Assert.NotNull(session);
        Assert.Equal(alertId, session!.SourceAlertId);
        Assert.Single(session.Sources);
        Assert.Equal(_composantId, session.Sources.First().ComponentId);
    }

    [Fact(DisplayName = "Une alerte sans composant designe presente les candidats plutot que de deviner")]
    public async Task CreateFromAlertAsync_Sans_Composant_Ne_Devine_Rien()
    {
        Guid alertId;
        await using (var db = _factory.CreateDbContext())
        {
            var alerte = new Alert
            {
                EnvironmentId = _envId,
                ComponentId = null,
                ComponentName = "Environnement",
                Kind = AlertKind.RessourceCritique, // aucune regle de candidats pour ce type
                Severity = AlertSeverity.Avertissement,
                Title = "Alerte de portée environnement",
                Detail = "Détail.",
                FirstOccurredAt = DateTimeOffset.UtcNow
            };
            db.Alerts.Add(alerte);
            await db.SaveChangesAsync();
            alertId = alerte.Id;
        }

        var resultat = await _sessions.CreateFromAlertAsync(alertId, "m.konate");

        Assert.True(resultat.Succeeded, resultat.Error);
        Assert.Equal(0, resultat.ComponentsCollected);

        // La session existe quand meme : l'operateur peut collecter manuellement.
        var session = await _sessions.GetAsync(resultat.SessionId);
        Assert.NotNull(session);
        Assert.Empty(session!.Sources);
    }

    // =======================================================================
    // FR-059J — Diagnostic rattaché à un fichier EDI précis
    // =======================================================================
    [Fact(DisplayName = "Diagnostiquer depuis un fichier EDI nomme ce fichier dans le motif de la session")]
    public async Task CreateFromEdiFileAsync_Nomme_Le_Fichier_Dans_Le_Motif()
    {
        var fichier = new EdiFile
        {
            ComponentId = _composantId,
            FileName = "COARRI_20260815.edi",
            Partner = "TERM-B",
            Status = EdiFileStatus.Rejete,
            ConsecutiveRejections = 4,
            FirstSeenAt = DateTimeOffset.UtcNow.AddHours(-3)
        };

        var resultat = await _sessions.CreateFromEdiFileAsync(fichier, _composantId, "m.konate");

        Assert.True(resultat.Succeeded, resultat.Error);

        var session = await _sessions.GetAsync(resultat.SessionId);
        Assert.NotNull(session);
        Assert.Contains("COARRI_20260815.edi", session!.Reason);
        Assert.Contains("TERM-B", session.Reason);
        Assert.Contains("4 échec", session.Reason);
        Assert.Single(session.Sources);
        Assert.Equal(_composantId, session.Sources.First().ComponentId);
    }

    [Fact]
    public async Task AdvancePhaseAsync_Trace_Chaque_Transition_Sans_Ecraser_Les_Precedentes()
    {
        var sessionId = await CreerSessionAsync();

        Assert.Null(await _sessions.AdvancePhaseAsync(
            sessionId, DiagnosticPhase.QualificationEtCollecte, "m.konate", null));
        Assert.Null(await _sessions.AdvancePhaseAsync(
            sessionId, DiagnosticPhase.DiagnosticEtCorrelation, "m.konate", "Corrélation lancée sur 3 sources."));

        var session = await _sessions.GetAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(DiagnosticPhase.DiagnosticEtCorrelation, session!.Phase);
        Assert.Equal(2, session.PhaseTransitions.Count);
        Assert.Equal(DiagnosticPhase.QualificationEtCollecte, session.PhaseTransitions.First().Phase);
        Assert.Equal(DiagnosticPhase.DiagnosticEtCorrelation, session.PhaseTransitions.Last().Phase);
    }

    [Fact]
    public async Task AdvancePhaseAsync_Autorise_Un_Retour_En_Arriere()
    {
        var sessionId = await CreerSessionAsync();

        await _sessions.AdvancePhaseAsync(sessionId, DiagnosticPhase.ChoixDuPlanDAction, "m.konate", null);
        var erreur = await _sessions.AdvancePhaseAsync(
            sessionId, DiagnosticPhase.QualificationEtCollecte, "m.konate",
            "Nouvel élément invalide l'hypothèse retenue — retour en collecte.");

        Assert.Null(erreur);

        var session = await _sessions.GetAsync(sessionId);
        Assert.Equal(DiagnosticPhase.QualificationEtCollecte, session!.Phase);
        Assert.Equal(2, session.PhaseTransitions.Count);
    }

    [Fact]
    public async Task AdvancePhaseAsync_Refuse_La_Cloture_Sans_Preciser_Ce_Qui_A_Ete_Verifie()
    {
        var sessionId = await CreerSessionAsync();

        var erreur = await _sessions.AdvancePhaseAsync(
            sessionId, DiagnosticPhase.ClotureEtCapitalisation, "m.konate", null);

        Assert.NotNull(erreur);

        var session = await _sessions.GetAsync(sessionId);
        Assert.Equal(DiagnosticPhase.DetectionEtEnregistrement, session!.Phase);
        Assert.Empty(session.PhaseTransitions);
    }

    [Fact(DisplayName = "§3.10.1 : un diagnostic analysé et inconcluant ne se clôture pas sans escalade déclarée")]
    public async Task AdvancePhaseAsync_Refuse_La_Cloture_D_Un_Diagnostic_Inconcluant_Sans_Escalade()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", "2026-08-14 08:00:00 INFO Démarrage\n");
        var analyse = await _analyse.ConcludeAsync(sessionId);
        Assert.Equal(DiagnosticVerdict.RienDeConcluant, analyse!.Verdict);

        var erreur = await _sessions.AdvancePhaseAsync(
            sessionId, DiagnosticPhase.ClotureEtCapitalisation, "m.konate", "Rien trouvé, on referme.");

        Assert.NotNull(erreur);
        Assert.Contains("escaladé", erreur!);
    }

    [Fact(DisplayName = "§3.10.1 : déclarer l'escalade permet la clôture et reste tracé sur la session")]
    public async Task AdvancePhaseAsync_Accepte_La_Cloture_D_Un_Diagnostic_Inconcluant_Avec_Escalade()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", "2026-08-14 08:00:00 INFO Démarrage\n");
        await _analyse.ConcludeAsync(sessionId);

        var erreur = await _sessions.AdvancePhaseAsync(
            sessionId, DiagnosticPhase.ClotureEtCapitalisation, "m.konate", "Rien trouvé, on referme.",
            "Support Navis");

        Assert.Null(erreur);

        var session = await _sessions.GetAsync(sessionId);
        Assert.Equal(DiagnosticPhase.ClotureEtCapitalisation, session!.Phase);
        Assert.Equal("Support Navis", session.EscalatedTo);
        Assert.Equal("m.konate", session.EscalatedBy);
        Assert.NotNull(session.EscalatedAt);
    }

    [Fact(DisplayName = "§3.10.1 : un verdict concluant se clôture sans exiger d'escalade")]
    public async Task AdvancePhaseAsync_N_Exige_Pas_D_Escalade_Pour_Un_Verdict_Concluant()
    {
        var sessionId = await CreerSessionAsync();
        await _analyse.ImportAsync(sessionId, "app.log", JournalAvecLicenceExpiree());
        var analyse = await _analyse.ConcludeAsync(sessionId);
        Assert.NotEqual(DiagnosticVerdict.RienDeConcluant, analyse!.Verdict);
        Assert.False(analyse.VerdictEstInconcluant);

        var erreur = await _sessions.AdvancePhaseAsync(
            sessionId, DiagnosticPhase.ClotureEtCapitalisation, "m.konate", "Licence renouvelée, service redémarré.");

        Assert.Null(erreur);
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

        public Task<ConnectorResult<LiveMetrics>> GetLiveMetricsAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<LiveMetrics>());

        public Task<ConnectorResult<TimeSyncSnapshot>> GetTimeSyncAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<TimeSyncSnapshot>());

        public Task<ConnectorResult<UpdateSnapshot>> GetPendingUpdatesAsync(ConnectorTarget t, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<UpdateSnapshot>());

        public Task<ConnectorResult<FolderSnapshot>> ListFilesAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<FolderSnapshot>());

        public Task<ConnectorResult<WriteProbeResult>> ProbeWriteAsync(ConnectorTarget t, string p, CancellationToken ct = default) =>
            Task.FromResult(Injoignable<WriteProbeResult>());
    }
}
