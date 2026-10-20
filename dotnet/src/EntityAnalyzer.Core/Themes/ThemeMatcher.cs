using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Core.Themes;

/// <summary>Найденное в тексте вхождение слова темы: позиция токена + словарное слово.</summary>
public sealed record ThemeHit(int Start, int Length, string Word);

/// <summary>
/// R3-1 (item 52): морфологический матчер слов темы по тексту.
/// Токен (последовательность букв, регистр не важен) считается вхождением,
/// если совпал с падежной формой слова (<see cref="RussianMorphology.Decline"/>),
/// со сгенерированной формой среднего рода («море» → «морями») либо — для
/// длинных слов — начинается с усечённой основы («география» →
/// «географический»). Основы короче 5 букв по префиксу не матчатся, чтобы
/// «море» не ловило «мораль».
/// </summary>
public static class ThemeMatcher
{
    private sealed record WordRule(string Word, HashSet<string> Forms, string? Prefix);

    /// <summary>Позиции и длины всех вхождений слов темы в тексте (по порядку).</summary>
    public static IReadOnlyList<ThemeHit> FindHits(string text, IReadOnlyList<string> words)
    {
        var hits = new List<ThemeHit>();
        if (string.IsNullOrEmpty(text) || words.Count == 0) return hits;

        var rules = words
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(BuildRule)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
        if (rules.Count == 0) return hits;

        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && char.IsLetter(text[i])) i++;
            var token = text[start..i].ToLowerInvariant();

            foreach (var rule in rules)
            {
                if (rule.Forms.Contains(token)
                    || (rule.Prefix is not null && token.StartsWith(rule.Prefix, StringComparison.Ordinal)))
                {
                    hits.Add(new ThemeHit(start, i - start, rule.Word));
                    break; // одно вхождение — одно словарное слово
                }
            }
        }
        return hits;
    }

    /// <summary>Уникальные слова темы, встретившиеся в тексте (в порядке появления).</summary>
    public static IReadOnlyList<string> MatchWords(string text, IReadOnlyList<string> words)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var hit in FindHits(text, words))
            if (seen.Add(hit.Word)) result.Add(hit.Word);
        return result;
    }

    private static WordRule? BuildRule(string word)
    {
        var w = word.Trim().ToLowerInvariant();
        if (w.Length < 2) return null;

        var forms = new HashSet<string>(StringComparer.Ordinal) { w };
        foreach (var form in RussianMorphology.Decline(w))
            forms.Add(form.ToLowerInvariant());

        // Средний род на -о/-е/-ё генератор имён не склоняет — добавляем сами
        // («море» → «морями», «копьё» → «копьями», «озеро» → «озёрами»).
        var last = w[^1];
        var stem = w[..^1];
        if (stem.Length >= 3)
        {
            if (last == 'о') foreach (var suf in (string[])["а", "у", "ом", "е", "ах", "ами"]) forms.Add(stem + suf);
            else if (last is 'е' or 'ё') foreach (var suf in (string[])["я", "ю", "ем", "е", "ях", "ями"]) forms.Add(stem + suf);
        }

        // Словообразовательный префикс: длинные слова с гласным окончанием
        // («география» → «географический»).
        string? prefix = null;
        if (w.Length >= 6 && "аеёиоуыэюя".Contains(last) && stem.Length >= 5)
            prefix = stem;

        return new WordRule(w, forms, prefix);
    }
}
