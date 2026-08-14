using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace N4Sentinel.Infrastructure.Knowledge;

/// <summary>
/// Découpe un fichier en fragments citables.
///
/// LE DÉCOUPAGE DÉTERMINE LA QUALITÉ DES CITATIONS. Un document indexé d'un
/// seul bloc ne peut pas être cité par page ni par section — ce que la recette
/// AC-10 exige. Le grain retenu est donc la page pour un PDF, la section pour
/// un Markdown, et le paragraphe pour un texte brut.
/// </summary>
public static class DocumentExtractor
{
    /// <summary>Extensions acceptées à l'import.</summary>
    public static readonly string[] ExtensionsSupportees = [".pdf", ".md", ".markdown", ".txt"];

    public static bool EstSupporte(string fileName) =>
        ExtensionsSupportees.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>
    /// Extrait les fragments d'un PDF, une entrée par page.
    ///
    /// Une page dont le texte n'est pas extractible — page numérisée, image —
    /// est retournée VIDE plutôt qu'omise : le décompte des pages reste juste,
    /// et l'appelant peut dire à l'opérateur combien de pages n'ont rien donné.
    /// </summary>
    public static List<ExtractedSection> DepuisPdf(Stream flux)
    {
        var fragments = new List<ExtractedSection>();

        using var document = PdfDocument.Open(flux);
        var extracteur = NearestNeighbourWordExtractor.Instance;

        var numero = 0;
        foreach (var page in document.GetPages())
        {
            numero++;

            string texte;
            try
            {
                // Les mots reconstitues par proximite donnent un texte bien plus
                // lisible que la concatenation brute des caracteres, qui colle
                // les mots entre eux sur beaucoup de PDF techniques.
                var mots = extracteur.GetWords(page.Letters).Select(m => m.Text);
                texte = string.Join(' ', mots);

                if (string.IsNullOrWhiteSpace(texte)) texte = page.Text;
            }
            catch (Exception)
            {
                // Une page mal formee ne doit pas faire echouer l'import entier.
                texte = string.Empty;
            }

            fragments.Add(new ExtractedSection
            {
                PageNumber = numero,
                Heading = PremiereLigneUtile(texte),
                Content = Nettoyer(texte)
            });
        }

        return fragments;
    }

    private static readonly Regex MotifTitreMarkdown =
        new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Découpe un Markdown à chaque titre. C'est le découpage naturel du
    /// format, et il produit des citations que le lecteur retrouve.
    /// </summary>
    public static List<ExtractedSection> DepuisMarkdown(string contenu)
    {
        var fragments = new List<ExtractedSection>();
        var correspondances = MotifTitreMarkdown.Matches(contenu);

        if (correspondances.Count == 0)
            return DepuisTexte(contenu);

        // Ce qui precede le premier titre n'est pas perdu.
        var preambule = contenu[..correspondances[0].Index].Trim();
        if (preambule.Length >= 40)
            fragments.Add(new ExtractedSection { PageNumber = 0, Content = Nettoyer(preambule) });

        for (var i = 0; i < correspondances.Count; i++)
        {
            var debut = correspondances[i].Index;
            var fin = i + 1 < correspondances.Count ? correspondances[i + 1].Index : contenu.Length;

            var titre = correspondances[i].Groups[2].Value.Trim();
            var corps = contenu[debut..fin].Trim();

            fragments.Add(new ExtractedSection
            {
                PageNumber = 0,
                Heading = titre,
                Content = Nettoyer(corps)
            });
        }

        return fragments;
    }

    /// <summary>
    /// Découpe un texte brut par blocs de paragraphes.
    ///
    /// Les blocs sont regroupés jusqu'à un millier de caractères : indexer
    /// chaque paragraphe isolément produirait des fragments trop courts pour
    /// porter un sens, et donc des citations inutilisables.
    /// </summary>
    public static List<ExtractedSection> DepuisTexte(string contenu)
    {
        var fragments = new List<ExtractedSection>();
        var blocs = Regex.Split(contenu, @"\r?\n\s*\r?\n");

        var accumule = new StringBuilder();

        foreach (var bloc in blocs)
        {
            var propre = bloc.Trim();
            if (propre.Length == 0) continue;

            if (accumule.Length > 0 && accumule.Length + propre.Length > 1000)
            {
                fragments.Add(new ExtractedSection
                {
                    PageNumber = 0,
                    Heading = PremiereLigneUtile(accumule.ToString()),
                    Content = Nettoyer(accumule.ToString())
                });
                accumule.Clear();
            }

            if (accumule.Length > 0) accumule.Append("\n\n");
            accumule.Append(propre);
        }

        if (accumule.Length > 0)
            fragments.Add(new ExtractedSection
            {
                PageNumber = 0,
                Heading = PremiereLigneUtile(accumule.ToString()),
                Content = Nettoyer(accumule.ToString())
            });

        return fragments;
    }

    /// <summary>Choisit le découpage d'après l'extension du fichier.</summary>
    public static List<ExtractedSection> Extraire(string fileName, Stream flux)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension == ".pdf") return DepuisPdf(flux);

        using var lecteur = new StreamReader(flux, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var contenu = lecteur.ReadToEnd();

        return extension is ".md" or ".markdown"
            ? DepuisMarkdown(contenu)
            : DepuisTexte(contenu);
    }

    // -----------------------------------------------------------------------
    private static readonly Regex MotifEspaces =
        new(@"[ \t]{2,}", RegexOptions.Compiled);

    private static readonly Regex MotifSautsMultiples =
        new(@"(\r?\n){3,}", RegexOptions.Compiled);

    /// <summary>
    /// Normalise l'espacement sans altérer le texte.
    ///
    /// Les PDF techniques produisent des rafales d'espaces là où la mise en
    /// page utilisait des colonnes. Les conserver rendrait les extraits
    /// illisibles à l'écran comme dans un rapport.
    /// </summary>
    private static string Nettoyer(string texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return string.Empty;

        var propre = texte.Replace(' ', ' ').Replace('\f', '\n');
        propre = MotifEspaces.Replace(propre, " ");
        propre = MotifSautsMultiples.Replace(propre, "\n\n");

        return propre.Trim();
    }

    /// <summary>Première ligne assez longue pour faire un titre de section.</summary>
    private static string? PremiereLigneUtile(string texte)
    {
        foreach (var ligne in texte.Split('\n'))
        {
            var propre = ligne.Trim().TrimStart('#').Trim();
            if (propre.Length is >= 8 and <= 200) return propre;
        }

        return null;
    }
}
