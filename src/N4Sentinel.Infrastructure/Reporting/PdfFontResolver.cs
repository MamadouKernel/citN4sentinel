using System.Runtime.CompilerServices;
using PdfSharp.Fonts;

namespace N4Sentinel.Infrastructure.Reporting;

/// <summary>
/// PdfSharp 6 (noyau sans GDI+) ne résout plus les polices système tout
/// seul — il faut le lui dire explicitement. L'application étant déployée
/// sur Windows uniquement (comme le coffre à secrets DPAPI), on lit
/// directement les fichiers TrueType du dossier Fonts plutôt que d'ajouter
/// une dépendance à des polices embarquées.
/// </summary>
internal sealed class WindowsFontResolver : IFontResolver
{
    private static readonly string DossierFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    public byte[] GetFont(string faceName)
    {
        var fichier = faceName switch
        {
            "Arial#b" => "arialbd.ttf",
            "Arial#i" => "ariali.ttf",
            "Arial#bi" => "arialbi.ttf",
            "Consolas#b" => "consolab.ttf",
            "Consolas#i" => "consolai.ttf",
            "Consolas" => "consola.ttf",
            _ => "arial.ttf"
        };

        var chemin = Path.Combine(DossierFonts, fichier);
        if (!File.Exists(chemin)) chemin = Path.Combine(DossierFonts, "arial.ttf");

        return File.ReadAllBytes(chemin);
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var famille = familyName.Equals("Consolas", StringComparison.OrdinalIgnoreCase) ? "Consolas" : "Arial";
        var suffixe = (isBold, isItalic) switch
        {
            (true, true) => "#bi",
            (true, false) => "#b",
            (false, true) => "#i",
            _ => ""
        };
        return new FontResolverInfo(famille + suffixe);
    }
}

internal static class PdfFontSetup
{
    // CA2255 : l'attribut vise d'abord le code applicatif, mais c'est le seul
    // point garanti de s'exécuter avant tout rendu PDF quel que soit l'appelant
    // (service DI ou test unitaire instanciant ReportDocumentService directement).
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void EnsureFontResolver()
    {
        GlobalFontSettings.FontResolver ??= new WindowsFontResolver();
    }
}
