using System.Text.RegularExpressions;

namespace EntityAnalyzer.Core.Text;

/// <summary>
/// Builds display contexts around matches: adaptive size depending on how busy the file is,
/// sentence-aligned windows with a word budget, whitespace-insensitive deduplication.
/// </summary>
public static partial class ContextBuilder
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\s\u00A0]+")]
    private static partial Regex AllWhitespace();

    /// <summary>
    /// Piecewise-linear context size: full <paramref name="maxWords"/> for sparse files,
    /// shrinking towards <paramref name="minWords"/> as mentions pile up.
    /// </summary>
    public static int CalculateContextSize(int totalFound, int maxWords = 75, int minWords = 25, int threshold = 50)
    {
        if (totalFound <= 5) return maxWords;
        var mid = (maxWords + minWords) / 2.0;

        if (totalFound <= 15)
        {
            var slope = (maxWords - mid) / 10.0;
            var v = (int)(maxWords - (totalFound - 5) * slope);
            return Math.Max(minWords, v);
        }

        if (totalFound <= threshold && threshold > 15)
        {
            var slope = (mid - minWords) / (threshold - 15);
            var v = (int)(mid - (totalFound - 15) * slope);
            return Math.Max(minWords, v);
        }

        return minWords;
    }

    /// <summary>
    /// Indices of sentences forming the context window around the match:
    /// the sentence containing it plus neighbours fitted into a word budget
    /// (45% of <paramref name="maxWords"/> per side, whole sentences at a time).
    /// </summary>
    public static IReadOnlyList<int> SelectWindowIndices(
        IReadOnlyList<SentenceSpan> spans, int matchStart, int matchEnd, int maxWords)
    {
        if (spans.Count == 0) return Array.Empty<int>();

        var target = FindSentenceIndex(spans, matchStart);

        var budget = maxWords * 0.45;
        var before = new List<int>();
        var acc = 0;
        for (var i = target - 1; i >= 0; i--)
        {
            var wc = spans[i].WordCount;
            if (acc + wc > budget) break;
            before.Insert(0, i);
            acc += wc;
        }

        var after = new List<int>();
        acc = 0;
        for (var i = target + 1; i < spans.Count; i++)
        {
            var wc = spans[i].WordCount;
            if (acc + wc > budget) break;
            after.Add(i);
            acc += wc;
        }

        var result = new List<int>(before.Count + after.Count + 1) { Capacity = before.Count + after.Count + 1 };
        result.AddRange(before);
        result.Add(target);
        result.AddRange(after);
        return result;
    }

    public static int FindSentenceIndex(IReadOnlyList<SentenceSpan> spans, int position)
    {
        var target = -1;
        var bestDist = double.MaxValue;
        for (var i = 0; i < spans.Count; i++)
        {
            var s = spans[i];
            if (s.Start <= position && position < s.End) return i;
            var d = Math.Min(Math.Abs(position - s.Start), Math.Abs(position - s.End));
            if (d < bestDist) { bestDist = d; target = i; }
        }
        return target;
    }

    /// <summary>Joins the selected sentences into one normalized context block.</summary>
    public static string Build(string text, IReadOnlyList<SentenceSpan> spans, int matchStart, int matchEnd, int maxWords)
    {
        var indices = SelectWindowIndices(spans, matchStart, matchEnd, maxWords);
        if (indices.Count == 0) return string.Empty;

        var parts = new string[indices.Count];
        for (var k = 0; k < indices.Count; k++)
        {
            var sp = spans[indices[k]];
            parts[k] = text.Substring(sp.Start, sp.End - sp.Start).Trim();
        }

        var ctx = string.Join(' ', parts);
        var mention = text.Substring(matchStart, matchEnd - matchStart);
        if (!string.IsNullOrEmpty(mention) && !ctx.Contains(mention))
            ctx = mention + " " + ctx;

        return Whitespace().Replace(ctx, " ").Trim();
    }

    /// <summary>Collapses all whitespace runs into single spaces (shared helper).</summary>
    public static string CollapseWhitespace(string s) => Whitespace().Replace(s, " ").Trim();

    /// <summary>
    /// Drops items whose context repeats an earlier one (whitespace-insensitive).
    /// When <paramref name="entitySelector"/> is supplied, deduplication is scoped
    /// to one entity: two different entities that share a sentence or context
    /// window must both survive. The same entity in the same context is still
    /// collapsed, which removes duplicate aliases of one mention.
    /// </summary>
    public static List<T> Deduplicate<T>(
        IEnumerable<T> items,
        Func<T, string> contextSelector,
        Func<T, string>? entitySelector = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<T>();
        foreach (var item in items)
        {
            var entity = AllWhitespace().Replace(entitySelector?.Invoke(item) ?? string.Empty, string.Empty);
            var context = AllWhitespace().Replace(contextSelector(item) ?? string.Empty, string.Empty);
            var key = entity + "\u001F" + context;
            if (seen.Add(key)) result.Add(item);
        }
        return result;
    }
}
