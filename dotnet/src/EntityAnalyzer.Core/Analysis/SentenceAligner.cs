using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Пара выровненных предложений.</summary>
public sealed record AlignmentPair(int RuIndex, int EnIndex, string RuText, string EnText);

/// <summary>
/// R2-25 «Выравнивание параллельных текстов»: карта соответствий RU↔EN
/// по предложениям. Сигнал сходства: близость длин (нормированная),
/// общие числа-якоря, совпадение имён через существующую транслитерацию
/// (Transliteration.ToLatin). Монотонное выравнивание жадным проходом
/// с окном поиска — детерминированно, офлайн, без зависимостей.
/// </summary>
public static class SentenceAligner
{
    public static IReadOnlyList<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?')) continue;
            var s = text[start..(i + 1)].Trim();
            if (s.Length > 0) sentences.Add(s);
            start = i + 1;
        }
        var tail = text[start..].Trim();
        if (tail.Length > 0) sentences.Add(tail);
        return sentences;
    }

    public static IReadOnlyList<AlignmentPair> Align(
        IReadOnlyList<string> ruSentences, IReadOnlyList<string> enSentences)
    {
        if (ruSentences.Count == 0 || enSentences.Count == 0) return [];

        // Динамическое программирование максимального веса (монотонное
        // взаимно-однозначное выравнивание с пропусками, как в Gale-Church).
        var n = ruSentences.Count;
        var m = enSentences.Count;
        var d = new double[n + 1, m + 1];
        for (var i = 1; i <= n; i++) d[i, 0] = 0;
        for (var j = 1; j <= m; j++) d[0, j] = 0;

        var enNumbers = enSentences.Select(ExtractNumbers).ToList();
        var enLatinNames = enSentences.Select(s => ExtractLatinNames(s).ToList()).ToList();
        var ruNumbersAll = ruSentences.Select(ExtractNumbers).ToList();
        var ruNamesAll = ruSentences.Select(ExtractRuNames).ToList();
        var totalRu = ruSentences.Sum(s => s.Length);
        var totalEn = enSentences.Sum(s => s.Length);
        var expectedRatio = totalRu / (double)Math.Max(1, totalEn);

        double Sim(int ri, int ei)
        {
            // Бонус за саму пару: для переводчика полнота карты важнее
            // точечных сомнений — слабые по длине пары всё равно нужны.
            var score = 1.0 - Math.Abs(ruSentences[ri].Length / (double)Math.Max(1, enSentences[ei].Length) - expectedRatio);
            if (ruNumbersAll[ri].Count > 0 && ruNumbersAll[ri].Overlaps(enNumbers[ei])) score += 10;
            foreach (var name in ruNamesAll[ri])
                if (enLatinNames[ei].Any(latin =>
                        string.Equals(Transliterator.Basic(latin), name, StringComparison.OrdinalIgnoreCase)))
                    score += 5;
            return score;
        }

        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
                d[i, j] = Math.Max(
                    Math.Max(d[i - 1, j - 1] + Sim(i - 1, j - 1), d[i - 1, j]),
                    d[i, j - 1]);

        // Восстановление пути.
        var pairsList = new List<(int Ri, int Ei)>();
        var ci = n;
        var cj = m;
        while (ci > 0 && cj > 0)
        {
            if (Math.Abs(d[ci, cj] - (d[ci - 1, cj - 1] + Sim(ci - 1, cj - 1))) < 1e-9)
            {
                pairsList.Add((ci - 1, cj - 1));
                ci--;
                cj--;
            }
            else if (Math.Abs(d[ci, cj] - d[ci - 1, cj]) < 1e-9) ci--;
            else cj--;
        }
        pairsList.Reverse();

        return [.. pairsList.Select(p => new AlignmentPair(p.Ri, p.Ei, ruSentences[p.Ri], enSentences[p.Ei]))];
    }

    private static HashSet<string> ExtractNumbers(string text)
    {
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        var word = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsDigit(ch)) word.Append(ch);
            else
            {
                if (word.Length >= 4) numbers.Add(word.ToString());
                word.Clear();
            }
        }
        if (word.Length >= 4) numbers.Add(word.ToString());
        return numbers;
    }

    /// <summary>Латинские слова с заглавной — кандидаты в имена EN.</summary>
    private static IEnumerable<string> ExtractLatinNames(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(',', '.', ';', ':', '!', '?'))
            .Where(w => w.Length >= 3 && char.IsUpper(w[0]) && w.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-'));

    /// <summary>Кириллические слова с заглавной — кандидаты в русские имена.</summary>
    private static HashSet<string> ExtractRuNames(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim(',', '.', ';', ':', '!', '?', '«', '»');
            if (w.Length < 4 || !char.IsUpper(w[0])) continue;
            if (!w.Any(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё')) continue;
            names.Add(w);
        }
        return names;
    }
}
