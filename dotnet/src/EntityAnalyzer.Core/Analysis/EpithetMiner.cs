using System.Text.RegularExpressions;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Эпитет сущности: слово (или словосочетание), частота и уникальность.</summary>
public sealed record EpithetStat(
    string Word,
    int Count,
    bool Unique,
    IReadOnlyList<string> AlsoUsedBy);

/// <summary>Облако эпитетов одной сущности.</summary>
public sealed record EntityEpithets(string Entity, IReadOnlyList<EpithetStat> Epithets);

/// <summary>
/// Группа 3 (item 23): автосбор словаря эпитетов. В контексте каждого
/// упоминания берутся слова в окне ±N от него и фильтруются эвристикой
/// «похоже на прилагательное» (окончания; морфологического анализатора в
/// проекте нет). Собираются одиночные слова и биграммы/триграммы подряд
/// идущих прилагательных; всё, что встретилось реже minCount, отбрасывается.
/// </summary>
public static partial class EpithetMiner
{
    public const int DefaultMinCount = 2;
    public const int DefaultWindow = 3;
    public const int DefaultMaxPerEntity = 40;

    private static readonly string[] AdjectiveSuffixes =
    [
        // русские: «древний», «зловещая», «подземное», «холодные», «великого», «чёрному»…
        "ый", "ий", "ой", "ая", "яя", "ое", "ее", "ые", "ие",
        "ых", "их", "ого", "его", "ому", "ему", "ым", "им",
        "ыми", "ими", "ую", "юю", "ею", "ою",
        // английские: «hideous», «fearful», «ancient», «sleeping»…
        "ous", "ful", "ive", "ical", "ic", "al", "ish", "less", "ing", "ed",
    ];

    /// <summary>Эвристика «прилагательное» по окончанию (без морфологии).</summary>
    public static bool LooksLikeAdjective(string word)
    {
        if (word.Length < 4) return false;
        var w = word.ToLowerInvariant();
        foreach (var suffix in AdjectiveSuffixes)
            if (w.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Собрать эпитеты всех сущностей прогона (сущности без эпитетов не попадают в результат).</summary>
    public static List<EntityEpithets> Mine(
        AnalysisSession session,
        int minCount = DefaultMinCount,
        int window = DefaultWindow,
        int maxPerEntity = DefaultMaxPerEntity)
    {
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var file in session.Files)
            foreach (var m in file.Mentions)
            {
                var (before, after) = AdjacentWords(m.Context ?? "", m.Variant, window);
                foreach (var word in Candidates(before, beforeSide: true))
                    Count(counts, m.Entity, word);
                foreach (var word in Candidates(after, beforeSide: false))
                    Count(counts, m.Entity, word);
            }

        // Уникальность считаем по «значимым» эпитетам (набравшим minCount).
        var entitiesByWord = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (entity, words) in counts)
            foreach (var (word, n) in words)
            {
                if (n < minCount) continue;
                if (!entitiesByWord.TryGetValue(word, out var owners))
                    entitiesByWord[word] = owners = new SortedSet<string>(StringComparer.Ordinal);
                owners.Add(entity);
            }

        var result = new List<EntityEpithets>();
        foreach (var (entity, words) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var stats = words
                .Where(kv => kv.Value >= minCount)
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(Math.Max(1, maxPerEntity))
                .Select(kv =>
                {
                    var others = entitiesByWord.TryGetValue(kv.Key, out var owners)
                        ? owners.Where(o => !string.Equals(o, entity, StringComparison.Ordinal)).ToList()
                        : [];
                    return new EpithetStat(kv.Key, kv.Value, others.Count == 0, others);
                })
                .ToList();
            if (stats.Count > 0)
                result.Add(new EntityEpithets(entity, stats));
        }
        return result;
    }

    /// <summary>Один эпитет (одиночное слово или биграмма/триграмма прилагательных).</summary>
    private static IEnumerable<string> Candidates(IReadOnlyList<string> nearestFirst, bool beforeSide)
    {
        if (nearestFirst.Count == 0 || !LooksLikeAdjective(nearestFirst[0]))
            yield break;
        yield return nearestFirst[0];
        if (nearestFirst.Count > 1 && LooksLikeAdjective(nearestFirst[1]))
        {
            yield return beforeSide
                ? nearestFirst[1] + " " + nearestFirst[0]
                : nearestFirst[0] + " " + nearestFirst[1];
            if (nearestFirst.Count > 2 && LooksLikeAdjective(nearestFirst[2]))
                yield return beforeSide
                    ? nearestFirst[2] + " " + nearestFirst[1] + " " + nearestFirst[0]
                    : nearestFirst[0] + " " + nearestFirst[1] + " " + nearestFirst[2];
        }
    }

    private static void Count(Dictionary<string, Dictionary<string, int>> counts, string entity, string word)
    {
        if (!counts.TryGetValue(entity, out var words))
            counts[entity] = words = new Dictionary<string, int>(StringComparer.Ordinal);
        words[word] = words.GetValueOrDefault(word) + 1;
    }

    /// <summary>Слова до/после упоминания в контексте (ближние — первыми, не более window с каждой стороны).</summary>
    private static (List<string> Before, List<string> After) AdjacentWords(
        string context, string variant, int window)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(variant))
            return ([], []);
        var idx = context.IndexOf(variant, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return ([], []);
        var end = idx + variant.Length;

        var before = new List<string>();
        var after = new List<string>();
        foreach (Match m in WordRegex().Matches(context))
        {
            var wordEnd = m.Index + m.Length;
            if (wordEnd <= idx) before.Add(m.Value.ToLowerInvariant());
            else if (m.Index >= end && after.Count < window) after.Add(m.Value.ToLowerInvariant());
        }
        if (before.Count > window) before.RemoveRange(0, before.Count - window);
        before.Reverse(); // ближнее слово — первым
        return (before, after);
    }

    [GeneratedRegex(@"[\p{L}]+(?:[’'\-][\p{L}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
