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

    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // BOM-символ U+FEFF не должен попадать в цитаты отчётов (QA BUG-007).
        if (text[0] == '\uFEFF') text = text.TrimStart('\uFEFF');

        // Compatibility folding (fullwidth forms, ligatures, superscripts…).
        // Большинство текстов уже нормализованы: IsNormalized избавляет от
        // лишней копии, а Normalize вызывается только при реальной разнице.
        var normalized = text.IsNormalized(NormalizationForm.FormKC)
            ? text
            : text.Normalize(NormalizationForm.FormKC);

        // Drop PDF "(cid:123)" artifacts only when they are actually present:
        // regex по каждому файлу был заметной долей времени нормализации.
        if (normalized.Contains("(cid:", StringComparison.Ordinal) ||
            normalized.Contains("(CID:", StringComparison.Ordinal))
            normalized = CidArtifact().Replace(normalized, string.Empty);

        // Один проход вместо четырёх regex: CR→LF, тире/кавычки, схлопывание
        // горизонтальных пробелов, не более двух переводов строк и удаление
        // пробелов рядом с границами строк.
        var sb = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        var newlineRun = 0;

        foreach (var raw in normalized)
        {
            var ch = raw switch
            {
                '\r' => '\n',
                '\u2013' or '\u2014' or '\u2212' => '\u2014', // – — − → em dash
                '\u00AB' or '\u00BB' or '\u201E' or '\u201C' or '\u201D' or '"' => '"',
                _ => raw
            };

            if (ch == '\n')
            {
                pendingSpace = false;
                if (newlineRun < 2) sb.Append('\n');
                newlineRun++;
                continue;
            }

            if (ch is ' ' or '\t' or '\u000B' or '\f')
            {
                if (newlineRun == 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0 && sb[^1] != '\n')
                sb.Append(' ');
            pendingSpace = false;
            newlineRun = 0;
            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }
}
