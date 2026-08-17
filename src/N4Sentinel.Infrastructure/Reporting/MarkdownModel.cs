using System.Text.RegularExpressions;

namespace N4Sentinel.Infrastructure.Reporting;

/// <summary>
/// Modèle et analyseur du sous-ensemble Markdown produit par
/// <c>ExecutionReportService</c> et <c>HistoryService</c> : titres (#, ##,
/// ###), tables, citations (&gt;), listes à puces (-), règle horizontale
/// (---), et mise en forme en ligne (**gras**, _italique_, `code`).
///
/// PAS UN ANALYSEUR MARKDOWN GÉNÉRALISTE — volontairement limité à ce que ces
/// deux générateurs produisent réellement, pour rester vérifiable plutôt que
/// de viser une compatibilité CommonMark que rien ici n'exige.
/// </summary>
internal abstract record MdBlock;
internal sealed record MdHeading(int Level, string Text) : MdBlock;
internal sealed record MdParagraph(string Text) : MdBlock;
internal sealed record MdQuote(string Text) : MdBlock;
internal sealed record MdBullet(string Text) : MdBlock;
internal sealed record MdRule : MdBlock;
internal sealed record MdTable(List<string> Headers, List<List<string>> Rows) : MdBlock;

internal readonly record struct MdRun(string Text, bool Bold, bool Italic, bool Code);

internal static class MarkdownDocument
{
    public static List<MdBlock> Parse(string markdown)
    {
        var blocks = new List<MdBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            if (line.Trim() == "---") { blocks.Add(new MdRule()); i++; continue; }

            if (line.StartsWith("### ")) { blocks.Add(new MdHeading(3, line[4..].Trim())); i++; continue; }
            if (line.StartsWith("## ")) { blocks.Add(new MdHeading(2, line[3..].Trim())); i++; continue; }
            if (line.StartsWith("# ")) { blocks.Add(new MdHeading(1, line[2..].Trim())); i++; continue; }

            if (line.StartsWith("> "))
            {
                var texte = line[2..].Trim();
                i++;
                while (i < lines.Length && lines[i].StartsWith("> "))
                {
                    texte += " " + lines[i][2..].Trim();
                    i++;
                }
                blocks.Add(new MdQuote(texte));
                continue;
            }

            if (line.StartsWith("- "))
            {
                blocks.Add(new MdBullet(line[2..].Trim()));
                i++;
                continue;
            }

            if (line.TrimStart().StartsWith('|'))
            {
                var entetes = SplitTableRow(line);
                i++;
                if (i < lines.Length && EstSeparateurDeTable(lines[i])) i++;

                var lignes = new List<List<string>>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    lignes.Add(SplitTableRow(lines[i]));
                    i++;
                }
                blocks.Add(new MdTable(entetes, lignes));
                continue;
            }

            blocks.Add(new MdParagraph(line.Trim()));
            i++;
        }

        return blocks;
    }

    private static bool EstSeparateurDeTable(string line)
    {
        var t = line.Trim();
        return t.StartsWith('|') && t.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length == 0;
    }

    private static List<string> SplitTableRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];

        const string espaceReserve = "";
        t = t.Replace("\\|", espaceReserve);
        return [.. t.Split('|').Select(c => c.Replace(espaceReserve, "|").Trim())];
    }

    private static readonly Regex InlinePattern = new(
        @"\*\*(?<b>[^*]+)\*\*|`(?<c>[^`]+)`|_(?<i>[^_]+)_", RegexOptions.Compiled);

    /// <summary>Découpe une ligne en fragments porteurs de leur mise en forme.</summary>
    public static List<MdRun> ParseInline(string text)
    {
        var runs = new List<MdRun>();
        var pos = 0;

        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > pos) runs.Add(new MdRun(text[pos..m.Index], false, false, false));

            if (m.Groups["b"].Success) runs.Add(new MdRun(m.Groups["b"].Value, true, false, false));
            else if (m.Groups["c"].Success) runs.Add(new MdRun(m.Groups["c"].Value, false, false, true));
            else if (m.Groups["i"].Success) runs.Add(new MdRun(m.Groups["i"].Value, false, true, false));

            pos = m.Index + m.Length;
        }

        if (pos < text.Length) runs.Add(new MdRun(text[pos..], false, false, false));
        if (runs.Count == 0) runs.Add(new MdRun(text, false, false, false));

        return runs;
    }

    /// <summary>Texte brut d'une ligne, mise en forme retirée — pour la pagination PDF.</summary>
    public static string PlainText(string text) => string.Concat(ParseInline(text).Select(r => r.Text));
}
