using DocumentFormat.OpenXml.Packaging;
using N4Sentinel.Infrastructure.Reporting;
using PdfSharp.Pdf.IO;

namespace N4Sentinel.Tests;

/// <summary>
/// FR-093 : l'export PDF/Word doit reproduire le contenu déjà structuré en
/// Markdown par ExecutionReportService/HistoryService — pas un contenu
/// recomposé. On vérifie ici que le fichier produit est un document valide
/// et RÉOUVRABLE (pas seulement un tableau d'octets non vide), et que le
/// texte significatif du Markdown source s'y retrouve.
/// </summary>
public sealed class ReportDocumentServiceTests
{
    private readonly ReportDocumentService _service = new();

    private const string MarkdownExemple = """
        # Rapport d'exécution — Démarrage complet

        **Opération terminée — chaque étape a produit la preuve de son aboutissement**

        ## Identification

        | | |
        |---|---|
        | Environnement | UAT |
        | Corrélation | `a1b2c3d4e5f6` |

        > **Impact annoncé au lancement.** Interruption totale des mouvements.

        ## Déroulement

        | # | Étape | Composant | Résultat | Durée |
        |---|---|---|---|---|
        | 1 | Démarrer Cluster Node 1 | Cluster Node 1 | Prouvé | 45 s |
        | 2 | Démarrer Center Node | Center Node | **Échec** | 12 s |

        - Contournée par m.konate. Redémarrage manuel confirmé.

        ---

        _N4 Sentinel — rapport produit le 16/08/2026 à 09:00._
        """;

    [Fact]
    public void RenderDocx_Produit_Un_Document_Word_Valide_Et_Reouvrable()
    {
        var octets = _service.RenderDocx("Rapport d'exécution — Démarrage complet", MarkdownExemple);

        Assert.True(octets.Length > 500, "Le document DOCX est anormalement petit.");

        using var stream = new MemoryStream(octets);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);

        var texte = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.Contains("Rapport d'exécution", texte);
        Assert.Contains("UAT", texte);
        Assert.Contains("a1b2c3d4e5f6", texte);
        Assert.Contains("Cluster Node 1", texte);
        Assert.Contains("Interruption totale des mouvements", texte);
    }

    [Fact]
    public void RenderPdf_Produit_Un_Document_Pdf_Valide_Et_Reouvrable()
    {
        var octets = _service.RenderPdf("Rapport d'exécution — Démarrage complet", MarkdownExemple);

        Assert.True(octets.Length > 500, "Le document PDF est anormalement petit.");

        using var stream = new MemoryStream(octets);
        using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

        Assert.True(pdf.PageCount >= 1);
    }

    [Fact]
    public void RenderPdf_Pagine_Quand_Le_Contenu_Depasse_Une_Page()
    {
        var longMarkdown = "# Rapport très long\n\n"
            + string.Concat(Enumerable.Range(1, 200).Select(i => $"- Ligne de constat numéro {i}, avec un peu de texte pour occuper de la place.\n"));

        var octets = _service.RenderPdf("Rapport très long", longMarkdown);

        using var stream = new MemoryStream(octets);
        using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

        Assert.True(pdf.PageCount > 1, "200 constats doivent déborder sur plusieurs pages.");
    }

    [Fact]
    public void RenderDocx_Sur_Table_Sans_Entete_Visible_Conserve_Les_Lignes()
    {
        const string md = """
            # Test

            | | |
            |---|---|
            | Clé | Valeur |
            """;

        var octets = _service.RenderDocx("Test", md);

        using var stream = new MemoryStream(octets);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var texte = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.Contains("Clé", texte);
        Assert.Contains("Valeur", texte);
    }
}
