using System.Text;

namespace EntityAnalyzer.Core.Filtering;

/// <summary>
/// Rejects contexts whose head contains known «listing/TOC» phrases
/// (book titles over name indexes, «Оглавление», «Table of contents», …).
/// Only the head of the context is inspected: a phrase deep inside a long
/// narrative passage is legitimate.
/// </summary>
public sealed class BlacklistPhraseFilter
{
    private readonly List<string> _phrases;
    private readonly int _headWindow;

    public BlacklistPhraseFilter(IReadOnlyList<string> phrases, int headWindow = 160)
    {
        _phrases = phrases
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Normalize(p))
            .ToList();
        _headWindow = headWindow;
    }

    public bool IsBlacklisted(string context)
    {
        if (string.IsNullOrWhiteSpace(context) || _phrases.Count == 0) return false;

        var normalized = Normalize(context);
        var window = Math.Min(normalized.Length, _headWindow);

        foreach (var phrase in _phrases)
        {
            // Phrase must START within the head window to count as a heading.
            var idx = normalized.IndexOf(phrase, StringComparison.Ordinal);
            if (idx >= 0 && idx <= window) return true;
        }
        return false;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var wasSpace = true;
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch)) { if (!wasSpace) { sb.Append(' '); wasSpace = true; } }
            else { sb.Append(ch); wasSpace = false; }
        }
        return sb.ToString().Trim();
    }
}
