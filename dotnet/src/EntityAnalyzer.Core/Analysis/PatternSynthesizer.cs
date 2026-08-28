using System.Text.Json;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Черновик паттерна: варианты имени + характерное окружение.</summary>
public sealed record PatternDraft(
    string Target,
    IReadOnlyList<string> Variants,
    IReadOnlyList<string> ContextWords);

/// <summary>
/// R1-23 «Генератор паттернов из примеров»: 3–5 предложений с формами
/// целевого имени → черновик JSON-паттерна для правки в редакторе.
/// Эвристика офлайн: варианты — слова с общим префиксом цели (первые
/// max(3, длина/2) букв); окружение — слова ≥4 букв в ±2 токенах от
/// варианта, встретившиеся минимум в двух примерах. LLM не требуется;
/// при желании черновик можно доработать внешним сервисом.
/// </summary>
public static class PatternSynthesizer
{
    public static PatternDraft FromExamples(IEnumerable<string> examples, string target)
    {
        var tokenized = examples
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim(',', '.', ';', ':', '!', '?', '«', '»')).ToArray())
            .ToList();
        if (tokenized.Count == 0 || target.Length == 0)
            return new PatternDraft(target, [], []);

        // Варианты: общий префикс (не короче 3 и не длиннее половины цели+1).
        var prefixLen = Math.Max(3, Math.Min(target.Length, target.Length / 2 + 1));
        var prefix = target[..prefixLen];
        var variants = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var sentence in tokenized)
        {
            var lowerPrefix = prefix.ToLowerInvariant();
            foreach (var w in sentence)
                if (w.Length >= prefixLen && w.ToLowerInvariant().StartsWith(lowerPrefix, StringComparison.Ordinal))
                    variants.Add(w);
        }

        // Окружение: слово в ±2 токенах от варианта, частота ≥2 примеров.
        var contextByWord = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokenized.Count; i++)
        {
            var sentence = tokenized[i];
            for (var j = 0; j < sentence.Length; j++)
            {
                if (!variants.Contains(sentence[j])) continue;
                for (var k = Math.Max(0, j - 2); k <= Math.Min(sentence.Length - 1, j + 2); k++)
                {
                    var w = sentence[k].ToLowerInvariant();
                    if (w.Length < 4 || variants.Contains(sentence[k])) continue;
                    if (!contextByWord.TryGetValue(w, out var set))
                        contextByWord[w] = set = new HashSet<int>();
                    set.Add(i);
                }
            }
        }

        var context = contextByWord.Where(kv => kv.Value.Count >= 2)
            .Select(kv => kv.Key)
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();

        return new PatternDraft(target, [.. variants], context);
    }

    /// <summary>Скелет JSON-паттерна для вставки в редактор semantic_patterns.json.</summary>
    public static string ToJson(PatternDraft draft)
    {
        var skeleton = new
        {
            name = $"synth-{draft.Target.ToLowerInvariant()}",
            description = $"Черновик по примерам: варианты {draft.Variants.Count}, контекст {draft.ContextWords.Count}.",
            enabled = false,
            match = new
            {
                any_of = draft.Variants,
                required = draft.ContextWords,
            },
        };
        return JsonSerializer.Serialize(skeleton, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
