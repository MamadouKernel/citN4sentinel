using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace N4Sentinel.Infrastructure.Reporting;

/// <summary>
/// Rend un document Markdown (voir <see cref="MarkdownDocument"/>) en PDF.
///
/// PAGINATION MANUELLE, PAS DE MOTEUR DE MISE EN PAGE : chaque bloc calcule sa
/// hauteur, une nouvelle page démarre dès qu'il ne tient plus sur la
/// courante. Suffisant pour un rapport d'exploitation lu et archivé — pas une
/// tentative de composition éditoriale.
/// </summary>
internal static class PdfRenderer
{
    private const double MargeGauche = 40, MargeDroite = 40, MargeHaut = 45, MargeBas = 45;
    private const double InterligneFacteur = 1.25;

    private sealed class Curseur(PdfDocument document)
    {
        public PdfPage Page = NouvellePage(document);
        public XGraphics Graphics = XGraphics.FromPdfPage(NouvellePage(document));
        public double Y = MargeHaut;

        private static PdfPage NouvellePage(PdfDocument d)
        {
            var page = d.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            return page;
        }

        public double LargeurUtile => Page.Width.Point - MargeGauche - MargeDroite;

        public void AssurerPlace(double hauteur, PdfDocument document)
        {
            if (Y + hauteur <= Page.Height.Point - MargeBas) return;

            Page = document.AddPage();
            Page.Size = PdfSharp.PageSize.A4;
            Graphics = XGraphics.FromPdfPage(Page);
            Y = MargeHaut;
        }
    }

    public static byte[] Render(string title, string markdown)
    {
        var document = new PdfDocument();
        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        var curseur = new Curseur(document) { Page = page, Graphics = XGraphics.FromPdfPage(page) };

        var policeTitre = new XFont("Arial", 18, XFontStyleEx.Bold);
        DessinerTexteMultiligne(document, curseur, title, policeTitre, XBrushes.Black);
        curseur.Y += 14;

        foreach (var bloc in MarkdownDocument.Parse(markdown))
            RenderBloc(document, curseur, bloc);

        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static void RenderBloc(PdfDocument document, Curseur c, MdBlock bloc)
    {
        switch (bloc)
        {
            case MdHeading h:
                c.Y += 10;
                var taille = h.Level switch { 1 => 15, 2 => 13, _ => 11.5 };
                DessinerTexteMultiligne(document, c, h.Text, new XFont("Arial", taille, XFontStyleEx.Bold), XBrushes.Black);
                c.Y += 4;
                break;

            case MdRule:
                c.AssurerPlace(12, document);
                c.Graphics.DrawLine(XPens.LightGray, MargeGauche, c.Y, MargeGauche + c.LargeurUtile, c.Y);
                c.Y += 12;
                break;

            case MdQuote q:
                DessinerTexteMultiligne(document, c, q.Text, new XFont("Arial", 9.5, XFontStyleEx.Italic),
                    XBrushes.DimGray, decalageGauche: 16);
                c.Y += 6;
                break;

            case MdBullet bu:
                DessinerTexteMultiligne(document, c, "•  " + MarkdownDocument.PlainText(bu.Text),
                    ChoisirPolice(MarkdownDocument.ParseInline(bu.Text)), XBrushes.Black, decalageGauche: 10);
                break;

            case MdTable t:
                DessinerTable(document, c, t);
                c.Y += 8;
                break;

            case MdParagraph pa:
                var fragments = MarkdownDocument.ParseInline(pa.Text);
                DessinerTexteMultiligne(document, c, pa.Text, ChoisirPolice(fragments), XBrushes.Black);
                c.Y += 4;
                break;
        }
    }

    /// <summary>
    /// Un rapport d'exploitation se scanne, il ne se lit pas mot à mot : le
    /// PDF privilégie une police dominante par ligne (gras si un fragment
    /// l'est, sinon code, sinon italique) plutôt que des polices mélangées au
    /// caractère près — lisible, pas typographiquement parfait.
    /// </summary>
    private static XFont ChoisirPolice(List<MdRun> fragments)
    {
        if (fragments.Any(f => f.Bold)) return new XFont("Arial", 10, XFontStyleEx.Bold);
        if (fragments.Any(f => f.Code)) return new XFont("Consolas", 9.5, XFontStyleEx.Regular);
        if (fragments.Any(f => f.Italic)) return new XFont("Arial", 10, XFontStyleEx.Italic);
        return new XFont("Arial", 10, XFontStyleEx.Regular);
    }

    private static void DessinerTexteMultiligne(
        PdfDocument document, Curseur c, string texteMarkdown, XFont police, XBrush brosse, double decalageGauche = 0)
    {
        var texte = MarkdownDocument.PlainText(texteMarkdown);
        var largeur = c.LargeurUtile - decalageGauche;
        var lignes = EnvelopperTexte(c.Graphics, texte, police, largeur);
        var hauteurLigne = police.GetHeight() * InterligneFacteur;

        foreach (var ligne in lignes)
        {
            c.AssurerPlace(hauteurLigne, document);
            c.Graphics.DrawString(ligne, police, brosse,
                new XPoint(MargeGauche + decalageGauche, c.Y + police.GetHeight()));
            c.Y += hauteurLigne;
        }
    }

    private static List<string> EnvelopperTexte(XGraphics gfx, string texte, XFont police, double largeurMax)
    {
        var resultat = new List<string>();
        foreach (var paragrapheBrut in texte.Split('\n'))
        {
            var mots = paragrapheBrut.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (mots.Length == 0) { resultat.Add(string.Empty); continue; }

            var courante = mots[0];
            for (var i = 1; i < mots.Length; i++)
            {
                var essai = courante + " " + mots[i];
                if (gfx.MeasureString(essai, police).Width <= largeurMax) courante = essai;
                else { resultat.Add(courante); courante = mots[i]; }
            }
            resultat.Add(courante);
        }
        return resultat;
    }

    private static void DessinerTable(PdfDocument document, Curseur c, MdTable t)
    {
        var colonnes = t.Headers.Count;
        if (colonnes == 0) return;

        var largeurColonne = c.LargeurUtile / colonnes;
        var policeEntete = new XFont("Arial", 9, XFontStyleEx.Bold);
        var policeCellule = new XFont("Arial", 9, XFontStyleEx.Regular);

        var enTeteVisible = t.Headers.Any(h => !string.IsNullOrWhiteSpace(h));
        if (enTeteVisible)
            DessinerLigneDeTable(document, c, t.Headers, colonnes, largeurColonne, policeEntete, XBrushes.Black, "EFEFEF");

        foreach (var ligne in t.Rows)
            DessinerLigneDeTable(document, c, ligne, colonnes, largeurColonne, policeCellule, XBrushes.Black, null);
    }

    private static void DessinerLigneDeTable(
        PdfDocument document, Curseur c, List<string> cellules, int colonnes, double largeurColonne,
        XFont police, XBrush brosse, string? fondHex)
    {
        var cellulesEnveloppees = cellules
            .Select(cell => EnvelopperTexte(c.Graphics, MarkdownDocument.PlainText(cell), police, largeurColonne - 8))
            .ToList();

        var hauteurLigne = cellulesEnveloppees.Max(l => l.Count) * police.GetHeight() * InterligneFacteur + 6;
        c.AssurerPlace(hauteurLigne, document);

        var fond = fondHex is null ? null : new XSolidBrush(CouleurDepuisHex(fondHex));
        if (fond is not null)
            c.Graphics.DrawRectangle(fond, MargeGauche, c.Y, colonnes * largeurColonne, hauteurLigne);

        for (var col = 0; col < colonnes; col++)
        {
            var x = MargeGauche + col * largeurColonne;
            c.Graphics.DrawRectangle(XPens.LightGray, x, c.Y, largeurColonne, hauteurLigne);

            var yTexte = c.Y + police.GetHeight();
            foreach (var ligneTexte in col < cellulesEnveloppees.Count ? cellulesEnveloppees[col] : [])
            {
                c.Graphics.DrawString(ligneTexte, police, brosse, new XPoint(x + 4, yTexte));
                yTexte += police.GetHeight() * InterligneFacteur;
            }
        }

        c.Y += hauteurLigne;
    }

    private static XColor CouleurDepuisHex(string hex) => XColor.FromArgb(
        Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16));
}
