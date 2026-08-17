using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace N4Sentinel.Infrastructure.Reporting;

/// <summary>
/// Rend un document Markdown (voir <see cref="MarkdownDocument"/>) en .docx —
/// mise en forme directe (gras, italique, police), pas de styles nommés :
/// un document produit pour être lu et archivé, pas édité en continu.
/// </summary>
internal static class DocxRenderer
{
    public static byte[] Render(string title, string markdown)
    {
        using var stream = new MemoryStream();

        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = mainPart.Document.AppendChild(new Body());

            body.AppendChild(Paragraphe(title, gras: true, taille: "32", avant: 0, apres: 240));

            foreach (var bloc in MarkdownDocument.Parse(markdown))
                RenderBloc(body, bloc);

            body.AppendChild(new SectionProperties(new PageMargin
            {
                Top = 1000, Bottom = 1000, Left = 1200, Right = 1200
            }));
        }

        return stream.ToArray();
    }

    private static void RenderBloc(Body body, MdBlock bloc)
    {
        switch (bloc)
        {
            case MdHeading h:
                body.AppendChild(Paragraphe(h.Text, gras: true,
                    taille: h.Level switch { 1 => "28", 2 => "24", _ => "22" },
                    avant: 240, apres: 120));
                break;

            case MdRule:
                var p = new Paragraph(new ParagraphProperties(
                    new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Color = "999999" })));
                body.AppendChild(p);
                break;

            case MdQuote q:
                body.AppendChild(ParagrapheAvecFragments(MarkdownDocument.ParseInline(q.Text),
                    italique: true, indentGauche: 360));
                break;

            case MdBullet bu:
                var puce = ParagrapheAvecFragments(MarkdownDocument.ParseInline(bu.Text));
                puce.ParagraphProperties ??= new ParagraphProperties();
                puce.ParagraphProperties.Indentation = new Indentation { Left = "360" };
                puce.PrependChild(new Run(new Text("• ") { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(puce);
                break;

            case MdTable t:
                body.AppendChild(TableauOpenXml(t));
                body.AppendChild(new Paragraph());
                break;

            case MdParagraph pa:
                body.AppendChild(ParagrapheAvecFragments(MarkdownDocument.ParseInline(pa.Text)));
                break;
        }
    }

    private static Paragraph Paragraphe(string texte, bool gras = false, string taille = "22", int avant = 0, int apres = 120)
    {
        var run = new Run(
            new RunProperties(new Bold { Val = gras }, new FontSize { Val = taille }),
            new Text(texte) { Space = SpaceProcessingModeValues.Preserve });

        return new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { Before = avant.ToString(), After = apres.ToString() }),
            run);
    }

    private static Paragraph ParagrapheAvecFragments(List<MdRun> fragments, bool italique = false, int? indentGauche = null)
    {
        var paragraphe = new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "160" }));

        if (indentGauche is not null)
            paragraphe.ParagraphProperties!.Indentation = new Indentation { Left = indentGauche.Value.ToString() };

        foreach (var f in fragments)
        {
            var proprietes = new RunProperties();
            if (f.Bold) proprietes.Append(new Bold());
            if (f.Italic || italique) proprietes.Append(new Italic());
            if (f.Code) proprietes.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });

            paragraphe.AppendChild(new Run(proprietes, new Text(f.Text) { Space = SpaceProcessingModeValues.Preserve }));
        }

        return paragraphe;
    }

    private static Table TableauOpenXml(MdTable t)
    {
        var table = new Table();

        table.AppendChild(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" }),
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }));

        var enTeteVisible = t.Headers.Any(h => !string.IsNullOrWhiteSpace(h));
        if (enTeteVisible)
        {
            var ligne = new TableRow();
            foreach (var h in t.Headers)
                ligne.AppendChild(new TableCell(CelluleShading("EFEFEF"), Paragraphe(h, gras: true, taille: "18", apres: 0)));
            table.AppendChild(ligne);
        }

        foreach (var r in t.Rows)
        {
            var ligne = new TableRow();
            foreach (var cellule in r)
                ligne.AppendChild(new TableCell(ParagrapheCelluleAvecFragments(MarkdownDocument.ParseInline(cellule))));
            table.AppendChild(ligne);
        }

        return table;
    }

    private static TableCellProperties CelluleShading(string couleurHex) =>
        new(new Shading { Val = ShadingPatternValues.Clear, Fill = couleurHex });

    private static TableCell ParagrapheCelluleAvecFragments(List<MdRun> fragments)
    {
        var p = new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "0" }));
        foreach (var f in fragments)
        {
            var proprietes = new RunProperties { FontSize = new FontSize { Val = "18" } };
            if (f.Bold) proprietes.Append(new Bold());
            if (f.Italic) proprietes.Append(new Italic());
            if (f.Code) proprietes.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
            p.AppendChild(new Run(proprietes, new Text(f.Text) { Space = SpaceProcessingModeValues.Preserve }));
        }
        return new TableCell(p);
    }
}
