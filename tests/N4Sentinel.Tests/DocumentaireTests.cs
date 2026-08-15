using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Knowledge;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Tests;

/// <summary>
/// Tests de la base documentaire — DOC-01 à DOC-03, recette AC-10.
///
/// Deux règles gouvernent ce module, et ce sont elles que ces tests protègent.
///
///   1. AUCUNE RÉPONSE SANS CITATION. Quand aucun passage ne correspond, le
///      service le dit. Il ne compose jamais une réponse à partir de ce qu'il
///      « sait » : il n'a pas de connaissance propre. Une réponse plausible
///      mais non sourcée est pire qu'une absence de réponse — elle sera crue,
///      et personne ne saura d'où elle vient.
///
///   2. UN DOCUMENT NON VALIDÉ NE RÉPOND PAS. Citer un brouillon avec
///      l'assurance d'une procédure approuvée, c'est ainsi qu'une consigne
///      périmée se retrouve appliquée un dimanche matin.
/// </summary>
public sealed class DocumentaireTests : IAsyncLifetime
{
    private const string MasterConnection =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"n4sentinel_test_{Guid.NewGuid():N}";
    private TestDbContextFactory _factory = null!;
    private KnowledgeService _base = null!;

    public async Task InitializeAsync()
    {
        var cs = $"Server=localhost;Database={_databaseName};Trusted_Connection=True;"
               + "TrustServerCertificate=True;MultipleActiveResultSets=True";

        _factory = new TestDbContextFactory(TestDbContextOptions.Builder(cs).Options);

        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        _base = new KnowledgeService(_factory, NullLogger<KnowledgeService>.Instance, new AuditWriter(_factory));
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
    // DOC-01 — Indexation
    // =======================================================================
    [Fact]
    public async Task Un_Document_Indexe_Reste_En_Brouillon()
    {
        var r = await IndexerGuideAsync();

        Assert.True(r.Succeeded, r.Error);

        var document = await _base.GetDocumentAsync(r.DocumentId);
        Assert.Equal(LifecycleStatus.Brouillon, document!.Status);
        Assert.False(document.IsCitable);
    }

    [Fact]
    public async Task Une_Reference_Est_Obligatoire()
    {
        // Sans reference, une citation ne designe rien.
        var r = await _base.IndexAsync(
            new KnowledgeDocument { Title = "Guide sans référence" },
            [new ExtractedSection { PageNumber = 1, Content = new string('a', 100) }]);

        Assert.False(r.Succeeded);
        Assert.Contains("citation ne désigne rien", r.Error!);
    }

    [Fact]
    public async Task Une_Reference_En_Double_Est_Refusee()
    {
        await IndexerGuideAsync();
        var second = await IndexerGuideAsync();

        Assert.False(second.Succeeded);
        Assert.Contains("déjà utilisée", second.Error!);
    }

    [Fact]
    public async Task Un_Document_Sans_Contenu_Exploitable_Est_Refuse()
    {
        var r = await _base.IndexAsync(
            new KnowledgeDocument { Title = "Scan", Reference = "SCAN-1" },
            [new ExtractedSection { PageNumber = 1, Content = "  " }]);

        Assert.False(r.Succeeded);
        Assert.Contains("reconnaissance de caractères", r.Error!);
    }

    // =======================================================================
    // DOC-02 — Recherche et citation (AC-10)
    // =======================================================================
    [Fact]
    public async Task Une_Reponse_Cite_Document_Section_Et_Page()
    {
        var r = await IndexerGuideAsync();
        await _base.ValidateAsync(r.DocumentId, "m.konate");

        var reponse = await _base.AskAsync("marqueur de démarrage du Center Node");

        Assert.True(reponse.Answered);
        var passage = reponse.Passages[0];

        // Les trois elements exiges par AC-10.
        Assert.Equal("GUIDE-3.8.25", passage.DocumentReference);
        Assert.True(passage.PageNumber > 0);
        Assert.False(string.IsNullOrWhiteSpace(passage.Heading));
        Assert.Contains("page", passage.Citation);
        Assert.Contains("GUIDE-3.8.25", passage.Citation);
    }

    [Fact]
    public async Task Un_Document_Non_Valide_Ne_Repond_Pas()
    {
        // Indexe mais pas valide : il ne doit alimenter aucune reponse.
        await IndexerGuideAsync();

        var reponse = await _base.AskAsync("marqueur de démarrage du Center Node");

        Assert.False(reponse.Answered);
        Assert.Empty(reponse.Passages);
        Assert.Contains("Aucun document validé", reponse.Explanation!);
    }

    [Fact]
    public async Task Un_Document_Revoque_Cesse_De_Repondre()
    {
        var r = await IndexerGuideAsync();
        await _base.ValidateAsync(r.DocumentId, "m.konate");
        Assert.True((await _base.AskAsync("marqueur de démarrage")).Answered);

        await _base.RevokeAsync(r.DocumentId, "test");

        Assert.False((await _base.AskAsync("marqueur de démarrage")).Answered);
    }

    [Fact(DisplayName = "Valider puis revoquer un document laisse une trace d'audit (FR-091)")]
    public async Task Valider_Et_Revoquer_Sont_Audites()
    {
        var r = await IndexerGuideAsync();
        await _base.ValidateAsync(r.DocumentId, "m.konate");
        await _base.RevokeAsync(r.DocumentId, "m.konate");

        await using var db = _factory.CreateDbContext();
        var traces = await db.AuditEntries
            .Where(a => a.Action == AuditAction.ChangementDeStatut)
            .Where(a => a.EntityId == r.DocumentId.ToString())
            .OrderBy(a => a.OccurredAt)
            .ToListAsync();

        Assert.Equal(2, traces.Count);
        Assert.All(traces, t => Assert.Equal("m.konate", t.Actor));
        Assert.Contains("Validé", traces[0].Reason);
        Assert.Contains("Désactivé", traces[1].Reason);
    }

    [Fact]
    public async Task Sans_Passage_Correspondant_L_Application_Le_Dit()
    {
        // LE TEST CENTRAL. L'application ne doit rien inventer : elle n'a pas
        // de connaissance propre, seulement des documents.
        var r = await IndexerGuideAsync();
        await _base.ValidateAsync(r.DocumentId, "m.konate");

        var reponse = await _base.AskAsync("procédure de dédouanement des conteneurs frigorifiques");

        Assert.False(reponse.Answered);
        Assert.Empty(reponse.Passages);
        Assert.Contains("n'a pas de connaissance propre", reponse.Explanation!);
    }

    [Fact]
    public async Task La_Recherche_Ignore_Les_Accents()
    {
        var r = await IndexerGuideAsync();
        await _base.ValidateAsync(r.DocumentId, "m.konate");

        var avec = await _base.AskAsync("démarrage séquence");
        var sans = await _base.AskAsync("demarrage sequence");

        Assert.True(avec.Answered);
        Assert.True(sans.Answered);
        Assert.Equal(avec.Passages[0].PageNumber, sans.Passages[0].PageNumber);
    }

    [Fact]
    public async Task Le_Passage_Couvrant_Le_Plus_De_Termes_Remonte_En_Premier()
    {
        // Une page qui repete vingt fois un mot courant ne doit pas passer
        // devant celle qui repond vraiment.
        await _base.IndexAsync(
            new KnowledgeDocument { Title = "Test de pertinence", Reference = "PERT-1" },
            [
                new ExtractedSection
                {
                    PageNumber = 1, Heading = "Bruit",
                    Content = string.Join(' ', Enumerable.Repeat("service", 40))
                            + " et beaucoup de remplissage pour atteindre la longueur minimale requise."
                },
                new ExtractedSection
                {
                    PageNumber = 2, Heading = "Réponse",
                    Content = "Pour redémarrer le service Bridge, arrêter le service puis attendre la "
                            + "confirmation dans le journal avant de relancer le composant XPS."
                }
            ]);

        var document = (await _base.GetDocumentsAsync()).First(d => d.Reference == "PERT-1");
        await _base.ValidateAsync(document.Id, "m.konate");

        var reponse = await _base.AskAsync("redemarrer service Bridge XPS journal");

        Assert.True(reponse.Answered);
        Assert.Equal(2, reponse.Passages[0].PageNumber);
    }

    [Fact]
    public async Task Une_Question_Vide_Ou_Trop_Courte_Est_Refusee()
    {
        var courte = await _base.AskAsync("a");
        Assert.False(courte.Answered);
        Assert.Contains("trop courte", courte.Explanation!);

        var vide = await _base.AskAsync("   ");
        Assert.False(vide.Answered);
    }

    [Fact]
    public async Task Une_Question_Faite_Uniquement_De_Mots_Vides_Est_Refusee()
    {
        var reponse = await _base.AskAsync("comment est-ce que le pour les");

        Assert.False(reponse.Answered);
        Assert.Contains("aucun terme exploitable", reponse.Explanation!);
    }

    [Fact]
    public async Task L_Extrait_Restitue_Le_Texte_D_Origine_Accents_Compris()
    {
        // La normalisation preserve la longueur caractere pour caractere :
        // sans cela, la position trouvee dans le texte normalise designerait
        // le mauvais endroit du texte d'origine, et l'extrait serait decale.
        await _base.IndexAsync(
            new KnowledgeDocument { Title = "Accents", Reference = "ACC-1" },
            [new ExtractedSection
            {
                PageNumber = 1,
                Content = new string('é', 500)
                        + " ATTENTION la séquence d'arrêt du Standby Center Node diffère du démarrage "
                        + new string('à', 500)
            }]);

        var document = (await _base.GetDocumentsAsync()).First(d => d.Reference == "ACC-1");
        await _base.ValidateAsync(document.Id, "m.konate");

        var reponse = await _base.AskAsync("Standby Center Node arret");

        Assert.True(reponse.Answered);
        Assert.Contains("Standby Center Node", reponse.Passages[0].Excerpt);
    }

    // =======================================================================
    // DOC-03 — Séparation conseil / action
    // =======================================================================
    [Fact]
    public void La_Reponse_Porte_Toujours_L_Avertissement_De_Non_Action()
    {
        Assert.Contains("ils ne la prennent pas", KnowledgeAnswer.AvertissementConseil);
        Assert.Contains("aucune action n'est déclenchée", KnowledgeAnswer.AvertissementConseil);
        Assert.Contains("workflow validé", KnowledgeAnswer.AvertissementConseil);
    }

    [Fact]
    public void Le_Service_Documentaire_Ne_Depend_D_Aucun_Service_D_Orchestration()
    {
        // Garde-fou structurel : si quelqu'un ajoute un jour une dependance
        // vers l'orchestrateur, ce test tombe. La documentation conseille,
        // elle n'execute pas.
        var constructeur = typeof(KnowledgeService).GetConstructors().Single();

        var dependances = constructeur.GetParameters()
            .Select(p => p.ParameterType.FullName ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(dependances, d => d.Contains("Orchestration", StringComparison.Ordinal));
    }

    // =======================================================================
    // Extraction
    // =======================================================================
    [Fact]
    public void Un_Markdown_Est_Decoupe_A_Chaque_Titre()
    {
        const string contenu = """
            # Guide d'exploitation

            Introduction suffisamment longue pour être retenue par l'indexation du document.

            ## Démarrage du cluster

            Les nœuds Cluster se démarrent un par un, en attendant la confirmation de chacun.

            ## Arrêt de l'écosystème

            La séquence d'arrêt n'est pas l'inverse de la séquence de démarrage.
            """;

        var fragments = DocumentExtractor.DepuisMarkdown(contenu);

        Assert.Equal(3, fragments.Count);
        Assert.Equal("Guide d'exploitation", fragments[0].Heading);
        Assert.Equal("Démarrage du cluster", fragments[1].Heading);
        Assert.Contains("un par un", fragments[1].Content);
    }

    [Fact]
    public void Un_Texte_Brut_Est_Regroupe_En_Blocs_Utilisables()
    {
        // Indexer chaque paragraphe isolement produirait des fragments trop
        // courts pour porter un sens.
        var contenu = string.Join("\n\n", Enumerable.Range(1, 12)
            .Select(i => $"Paragraphe {i} : contenu technique de longueur raisonnable pour un test."));

        var fragments = DocumentExtractor.DepuisTexte(contenu);

        Assert.NotEmpty(fragments);
        Assert.All(fragments, f => Assert.True(f.Content.Length > 40));
        Assert.True(fragments.Count < 12);
    }

    [Fact]
    public void Les_Extensions_Supportees_Sont_Reconnues()
    {
        Assert.True(DocumentExtractor.EstSupporte("guide.pdf"));
        Assert.True(DocumentExtractor.EstSupporte("SOP.MD"));
        Assert.True(DocumentExtractor.EstSupporte("notes.txt"));
        Assert.False(DocumentExtractor.EstSupporte("classeur.xlsx"));
    }

    [Fact]
    public void L_Extraction_Choisit_Le_Decoupage_D_Apres_L_Extension()
    {
        var octets = Encoding.UTF8.GetBytes(
            "# Titre principal\n\nUn contenu de section assez long pour être retenu par l'indexation.");

        using var flux = new MemoryStream(octets);
        var fragments = DocumentExtractor.Extraire("guide.md", flux);

        Assert.Single(fragments);
        Assert.Equal("Titre principal", fragments[0].Heading);
    }

    // =======================================================================
    // Normalisation
    // =======================================================================
    [Fact]
    public void La_Normalisation_Preserve_La_Longueur()
    {
        // Propriete indispensable a l'extraction d'un passage : chaque
        // caractere est remplace, jamais supprime.
        foreach (var texte in new[]
                 {
                     "Démarrage séquentiel du cluster",
                     "Arrêt — Standby Center Node (à confirmer)",
                     "ÉÀÇÙÔÎ 12345 !?;:"
                 })
        {
            Assert.Equal(texte.Length, KnowledgeService.Normaliser(texte).Length);
        }
    }

    [Fact]
    public void Les_Mots_Vides_Sont_Ecartes_Des_Termes()
    {
        var termes = KnowledgeService.Termes("Comment faire pour démarrer le Center Node ?");

        Assert.Contains("demarrer", termes);
        Assert.Contains("center", termes);
        Assert.DoesNotContain("pour", termes);
        Assert.DoesNotContain("comment", termes);
        Assert.DoesNotContain("le", termes);
    }

    // =======================================================================
    // Aides
    // =======================================================================
    private async Task<IndexResult> IndexerGuideAsync() =>
        await _base.IndexAsync(
            new KnowledgeDocument
            {
                Title = "Guide d'exploitation Navis N4",
                Reference = "GUIDE-3.8.25",
                Kind = DocumentKind.GuideEditeur,
                AppliesToVersion = "3.8.25"
            },
            [
                new ExtractedSection
                {
                    PageNumber = 12,
                    Heading = "Séquence de démarrage",
                    Content = "Le marqueur de démarrage du Center Node est la ligne "
                            + "« Web tier servlet 'action' initialized » écrite dans navis-apex.log. "
                            + "La séquence de démarrage impose de lancer les nœuds Cluster un par un."
                },
                new ExtractedSection
                {
                    PageNumber = 34,
                    Heading = "Séquence d'arrêt",
                    Content = "L'arrêt de l'écosystème suit sa propre séquence : le Standby Center Node "
                            + "s'arrête avant le Center Node, alors qu'il démarre après lui."
                }
            ]);

    private sealed class TestDbContextFactory(DbContextOptions<N4SentinelDbContext> options)
        : IDbContextFactory<N4SentinelDbContext>
    {
        public N4SentinelDbContext CreateDbContext() => new(options);
    }
}
