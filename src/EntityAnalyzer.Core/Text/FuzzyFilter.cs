namespace EntityAnalyzer.Core.Text;

/// <summary>Элемент результата ранжирования.</summary>
public sealed record FuzzyMatch(string Item, int Score);

/// <summary>
/// R1-7 «Command Palette»: нечёткий фильтр по подпоследовательности символов
/// с ранжированием. Оценка (чем больше, тем лучше):
///   +40 точное вхождение подстроки; +25 совпадение в начале слова;
///   +8 за каждый подряд идущий символ после первого; −0.1 × длина цели
///   (короткие формулировки ценнее при прочих равных); +2 за каждый
///   совпавший символ базово. Не нашлось — элемент отфильтрован.
/// </summary>
public static class FuzzyFilter
{
    public static IReadOnlyList<FuzzyMatch> Rank(IEnumerable<string> items, string query)
    {
        var result = new List<FuzzyMatch>();
        if (string.IsNullOrWhiteSpace(query))
            return [.. items.Select(i => new FuzzyMatch(i, 0))];

        var q = query.Trim();
        foreach (var item in items)
        {
            var score = Score(item, q);
            if (score > 0) result.Add(new FuzzyMatch(item, score));
        }
        return [.. result.OrderByDescending(m => m.Score).ThenBy(m => m.Item, StringComparer.Ordinal)];
    }

    /// <summary>Оценка совпадения одной строки; 0 — совпадения нет.</summary>
    public static int Score(string target, string query)
    {
        var t = target;
        var ti = 0; // индекс в target

        // Точное вхождение подстроки.
        var idx = t.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx == 0) return 60 - t.Length / 10;
        if (idx > 0) return 40 - t.Length / 10;

        // Подпоследовательность с бонусами за начало слова и подряд.
        var score = 0;
        var prev = -2;
        var matched = 0;
        for (var qi = 0; qi < query.Length && ti < t.Length; qi++)
        {
            var found = -1;
            for (var i = ti; i < t.Length; i++)
                if (char.ToLowerInvariant(t[i]) == char.ToLowerInvariant(query[qi])) { found = i; break; }
            if (found < 0) return 0;

            score += 2;
            if (found == 0 || IsWordBoundary(t, found)) score += 25;
            if (found == prev + 1) score += 8;
            prev = found;
            ti = found + 1;
            matched++;
        }
        if (matched < query.Length) return 0;
        return score - t.Length / 10;
    }

    private static bool IsWordBoundary(string s, int index) =>
        index == 0 || !char.IsLetterOrDigit(s[index - 1]);
}
