using EntityAnalyzer.Core.Text;

namespace EntityAnalyzer.Core.Fuzzy;

/// <summary>A near-miss discovered without exact matching.</summary>
public sealed record FuzzyHit(string Entity, string Window, string MatchedVariant, int Score, string Sentence);

/// <summary>
/// Catches slightly misspelled mentions of entities the exact automaton never saw,
/// using bounded Levenshtein distance over word windows (port of the rapidfuzz pass).
/// Variants are indexed by length so a window only compares same-scale candidates.
/// </summary>
public sealed class FuzzyFinder
{
    private const string EdgePunctuation = ",.!?:;)(\u00AB\u00BB\"\u201D\u2019\u2026";

    private readonly int _threshold;
    private const int MaxVariantsPerEntity = 120;
    private const int MaxHits = 200;
    /// <summary>Fuzzy scanning is bounded: enough for rescue duty, never a second full-text pass.</summary>
    private const int MaxSentencesScanned = 1500;

    public FuzzyFinder(int threshold = 85) => _threshold = Math.Clamp(threshold, 50, 100);

    public List<FuzzyHit> FindMissed(
        EntityAnalyzer.Core.Variants.EntityCatalog catalog,
        string text,
        IReadOnlySet<string> foundEntities)
    {
        var hits = new List<FuzzyHit>();
        if (string.IsNullOrWhiteSpace(text)) return hits;

        var seenWindows = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entity in catalog.AllNames)
        {
            if (foundEntities.Contains(entity)) continue;
            if (!catalog.VariantsByEntity.TryGetValue(entity, out var variants)) continue;

            // Length-indexed candidate table: window → few same-scale buckets.
            var buckets = new SortedDictionary<int, List<(string Lower, string Original)>>();
            foreach (var v in variants.Distinct(StringComparer.Ordinal).Take(MaxVariantsPerEntity))
            {
                if (v.Length < 3) continue;
                var low = v.ToLowerInvariant();
                var list = GetOrCreate(buckets, low.Length);
                if (!list.Any(x => x.Lower == low)) list.Add((low, v));
            }
            if (buckets.Count == 0) continue;

            var scanned = 0;
            foreach (var sentence in SentenceSplitter.Split(text))
            {
                if (++scanned > MaxSentencesScanned) break;

                var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (var size = 1; size <= Math.Min(4, words.Length); size++)
                {
                    for (var i = 0; i + size <= words.Length; i++)
                    {
                        var windowText = string.Join(' ', words.Skip(i).Take(size));
                        var trimmed = windowText.Trim(EdgePunctuation.ToCharArray());
                        if (trimmed.Length < 3) continue;

                        var key = entity + "\u0001" + trimmed.ToLowerInvariant();
                        if (!seenWindows.Add(key)) continue;

                        var best = TryMatchWindow(trimmed.ToLowerInvariant(), buckets);
                        if (best is null) continue;

                        hits.Add(new FuzzyHit(entity, trimmed, best.Value.Original, best.Value.Score, sentence));
                        if (hits.Count >= MaxHits) return hits;
                    }
                }
            }
        }
        return hits;
    }

    private static List<(string Lower, string Original)> GetOrCreate(
        SortedDictionary<int, List<(string, string)>> dict, int len)
    {
        if (!dict.TryGetValue(len, out var list))
        {
            list = [];
            dict[len] = list;
        }
        return list;
    }

    private (string Original, int Score)? TryMatchWindow(
        string windowLower, SortedDictionary<int, List<(string Lower, string Original)>> buckets)
    {
        var wlen = windowLower.Length;
        var allowedDistance = Math.Max(0, (int)Math.Floor(wlen * (1 - _threshold / 100.0)));
        var minLen = (int)Math.Ceiling(wlen / 1.3);
        var maxLen = (int)(wlen / 0.7);

        (string, int)? best = null;

        foreach (var pair in buckets)
        {
            var len = pair.Key;
            if (len < minLen) continue;
            if (len > maxLen) break;

            var maxAllowed = Math.Max(allowedDistance, Math.Max(0, (int)Math.Floor(Math.Max(wlen, len) * (1 - _threshold / 100.0))));
            foreach (var (lower, original) in pair.Value)
            {
                var distance = Levenshtein.Bounded(windowLower, lower, maxAllowed + 1);
                if (distance > maxAllowed) continue;

                var score = (int)Math.Round((1 - distance / (double)Math.Max(wlen, len)) * 100);
                if (score >= _threshold && (best is null || score > best.Value.Item2))
                    best = (original, score);
            }
        }
        return best is null ? null : (best.Value.Item1, best.Value.Item2);
    }
}

/// <summary>Levenshtein distance with an upper-bound cutoff for speed.</summary>
internal static class Levenshtein
{
    public static int Bounded(string a, string b, int abandonAbove)
    {
        if (Math.Abs(a.Length - b.Length) > abandonAbove) return abandonAbove + 1;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
                if (curr[j] < rowMin) rowMin = curr[j];
            }
            if (rowMin > abandonAbove) return abandonAbove + 1;
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
