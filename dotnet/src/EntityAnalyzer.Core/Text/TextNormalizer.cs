using System.Text;
using System.Text.RegularExpressions;

namespace EntityAnalyzer.Core.Text;

/// <summary>
/// Cleans raw text from corpus files: PDF artifacts, encoding quirks, whitespace noise.
/// Unlike the original Python engine, newline characters are preserved so that
/// line numbers reported for mentions remain accurate.
/// </summary>
public static partial class TextNormalizer
{
    [GeneratedRegex(@"\((?:cid|CID):\d+\)")]
    private static partial Regex CidArtifact();

    [GeneratedRegex(@"[ \t\x0B\f]+")]
    private static partial Regex HorizontalRuns();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLineRuns();

    [GeneratedRegex(@" +")]
    private static partial Regex MultiSpaces();

    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // BOM-символ U+FEFF не должен попадать в цитаты отчётов (QA BUG-007).
        if (text[0] == '\uFEFF') text = text.TrimStart('\uFEFF');

        // Compatibility folding (fullwidth forms, ligatures, superscripts…).
        var sb = new StringBuilder(text.Normalize(NormalizationForm.FormKC));

        // Drop PDF "(cid:123)" artifacts.
        var cleaned = CidArtifact().Replace(sb.ToString(), string.Empty);

        var dst = new StringBuilder(cleaned.Length);
        foreach (var ch in cleaned)
        {
            dst.Append(ch switch
            {
                '\r' => '\n',
                '\u2013' or '\u2014' or '\u2212' => '\u2014', // – — − → em dash
                '\u00AB' or '\u00BB' or '\u201E' or '\u201C' or '\u201D' or '"' => '"',
                _ => ch
            });
        }

        var result = dst.ToString()
            .Replace("\r\n", "\n");

        result = HorizontalRuns().Replace(result, " ");
        result = MultiSpaces().Replace(result, " ");
        result = BlankLineRuns().Replace(result, "\n\n");
        result = result.Replace(" \n", "\n").Replace("\n ", "\n");

        return result.Trim();
    }
}
