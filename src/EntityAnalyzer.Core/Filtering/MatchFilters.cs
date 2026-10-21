namespace EntityAnalyzer.Core.Filtering;

/// <summary>Raw pattern occurrence before context filtering.</summary>
public readonly record struct RawMatch(int Start, int End, string Variant, string Entity, double NoiseScore = 0);

/// <summary>
/// Boundary checks rejecting matches glued into neighbouring words.
/// Hyphen and apostrophe connectors are allowed (names like «Наг-Иа», «Y'ha-Nthlei»).
/// </summary>
public static class MatchFilters
{
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    /// <summary>True when a letter/digit touches the match on either side.</summary>
    public static bool IsFalsePositive(string text, int start, int end)
    {
        if (start > 0 && IsWordChar(text[start - 1])) return true;
        if (end < text.Length && IsWordChar(text[end])) return true;
        return false;
    }

    /// <summary>
    /// True when the match is embedded into a word without a legitimate connector.
    /// Complements <see cref="IsFalsePositive"/> by explicitly allowing '-' and '\''.
    /// </summary>
    public static bool IsBrokenWord(string text, int start, int end)
    {
        if (start > 0)
        {
            var c = text[start - 1];
            if (c != '-' && c != '\'' && IsWordChar(c)) return true;
        }
        if (end < text.Length)
        {
            var c = text[end];
            if (c != '-' && c != '\'' && IsWordChar(c)) return true;
        }
        return false;
    }

    /// <summary>Combined gate used by the pipeline.</summary>
    public static bool ShouldReject(string text, RawMatch m) =>
        IsFalsePositive(text, m.Start, m.End) || IsBrokenWord(text, m.Start, m.End);
}

/// <summary>
/// Resolves overlapping matches: longest wins; ties prefer lower noise score,
/// then keep the earliest. Partial (non-nested) overlaps are all preserved —
/// same behaviour as the original Python resolver.
/// </summary>
public static class MatchResolver
{
    public static List<RawMatch> ResolveOverlaps(IEnumerable<RawMatch> matches)
    {
        var sorted = matches
            .OrderBy(m => m.Start)
            .ThenByDescending(m => m.End)
            .ThenBy(m => m.NoiseScore)
            .ToList();

        var resolved = new List<RawMatch>(sorted.Count);
        foreach (var m in sorted)
        {
            if (resolved.Count == 0) { resolved.Add(m); continue; }

            var last = resolved[^1];

            var nestedInLast = m.Start >= last.Start && m.End <= last.End;
            var lastNestedInM = last.Start >= m.Start && last.End <= m.End;

            if (nestedInLast) continue;                 // strictly smaller or equal span → drop
            if (lastNestedInM) { resolved[^1] = m; continue; }

            resolved.Add(m);
        }
        return resolved;
    }
}
