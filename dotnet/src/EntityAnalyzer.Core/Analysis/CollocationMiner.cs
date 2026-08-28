namespace EntityAnalyzer.Core.Analysis;

/// <summary>Найденная коллокация: биграмма и число вхождений.</summary>
public sealed record Collocation(string Phrase, int Count);

/// <summary>
/// R2-1 «Шахтёр коллокаций»: устойчивые биграммы вокруг упоминаний сущности
/// («воющей собаки» рядом с «Наг»). Наивный проход по нормализованным
/// текстам: биграмма учитывается, если один из двух её членов — совпадение
/// сущности. Слова короче трёх букв игнорируются (предлоги/союзы шумят).
/// Ранжирование — по частоте, затем алфавит (детерминированность).
/// </summary>
public static class CollocationMiner
{
    public static IReadOnlyList<Collocation> Mine(
        IEnumerable<string> normalizedTexts, string entityName, int top = 20)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var target = entityName.ToLowerInvariant();

        foreach (var text in normalizedTexts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < words.Length; i++)
            {
                if (!words[i].Equals(target, StringComparison.OrdinalIgnoreCase)) continue;

                // Соседние биграммы: слева и справа от совпадения.
                if (i > 0) Add(counts, words[i - 1], target);
                if (i + 1 < words.Length) Add(counts, target, words[i + 1]);
            }
        }

        return [.. counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(Math.Max(1, top))
            .Select(kv => new Collocation(kv.Key, kv.Value))];
    }

    private static void Add(Dictionary<string, int> counts, string left, string right)
    {
        // Короткие служебные слова не дают полезных кандидатов.
        if (left.Length < 3 || right.Length < 3) return;
        var key = $"{left} {right}";
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }
}
