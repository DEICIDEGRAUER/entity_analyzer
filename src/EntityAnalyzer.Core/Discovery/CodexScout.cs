using System.Text;

namespace EntityAnalyzer.Core.Discovery;

/// <summary>Кандидат в карточки кодекса, найденный скаутом по корпусу.</summary>
public sealed record ScoutCandidate(
    string Phrase,
    int Frequency,
    int FileCount,
    IReadOnlyList<string> Examples);

/// <summary>
/// «Кодекс-скаут» (R1-1): discovery непокрытых имён. Находит частотные
/// капитализированные последовательности (1–3 слова), не пересекающиеся с
/// известными вариантами кодекса, и ранжирует их как кандидатов в карточки.
///
/// Шумодавы: слово, встречающееся ТОЛЬКО в начале предложения («Жрецы
/// пришли…»), кандидатом не считается; служебные слова отфильтрованы;
/// известные сущности исключаются по слову и по подстроке.
/// Офлайн, ноль зависимостей, наивный проход по уже прочитанным текстам.
/// </summary>
public static class CodexScout
{
    /// <summary>Служебные слова, чья капитализация не делает их именем.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "и", "а", "но", "да", "не", "ни", "ну", "вот", "так", "как", "это", "этот", "эта",
        "тот", "та", "все", "всё", "мы", "вы", "они", "он", "она", "оно", "я", "ты", "кто",
        "что", "где", "когда", "затем", "позже", "однако", "если", "чтобы", "при", "для",
        "между", "после", "до", "из", "от", "по", "за", "на", "в", "с", "у", "об", "о",
        "the", "a", "an", "and", "but", "if", "of", "in", "on", "at", "to",
    };

    public static IReadOnlyList<ScoutCandidate> Discover(
        IEnumerable<KeyValuePair<string, string>> documents,
        ISet<string> knownNames,
        int top = 50,
        int minFrequency = 2)
    {
        // Агрегаты по фразе (регистр сохраняем для показа, сравниваем без него).
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var midSentence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fileName, text) in documents)
        {
            foreach (var sentence in SplitSentences(text))
            {
                var caps = CapitalizedWords(sentence);
                for (var start = 0; start < caps.Count; start++)
                {
                    var maxLen = Math.Min(3, caps.Count - start);
                    for (var len = 1; len <= maxLen; len++)
                    {
                        var words = caps.GetRange(start, len);
                        var phrase = string.Join(' ', words);

                        // Служебная последовательность целиком из стоп-слов — мимо.
                        if (words.All(w => StopWords.Contains(w))) continue;

                        // Известные сущности исключаем: по любому слову и по подстроке.
                        if (MatchesKnown(phrase, words, knownNames)) continue;

                        var key = phrase;
                        freq[key] = freq.GetValueOrDefault(key) + 1;
                        // Середина предложения: перед фразой есть другое кап-слово,
                        // либо само предложение начинается со строчной.
                        if (start > 0 || sentenceStartsWithLowercase(sentence))
                            midSentence[key] = midSentence.GetValueOrDefault(key) + 1;

                        if (!files.TryGetValue(key, out var set))
                            files[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(fileName);
                    }
                }
            }
        }

        return freq
            .Where(kv => kv.Value >= minFrequency)
            .Where(kv =>
            {
                // Однословный кандидат обязан хотя бы раз встатьить себя в середину
                // предложения — иначе это просто «слово с заглавной после точки».
                var wordCount = kv.Key.Split(' ').Length;
                return wordCount > 1 || midSentence.GetValueOrDefault(kv.Key) > 0;
            })
            .Select(kv => new ScoutCandidate(
                kv.Key,
                kv.Value,
                files[kv.Key].Count,
                files[kv.Key].Take(3).ToList()))
            .OrderByDescending(c => c.Frequency)
            .ThenByDescending(c => c.FileCount)
            .ThenBy(c => c.Phrase, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .ToList();
    }

    /// <summary>Отчёт scout.txt: ранжированный список кандидатов с примерами файлов.</summary>
    public static string WriteReport(IReadOnlyList<ScoutCandidate> candidates, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "scout.txt");
        var sb = new StringBuilder();
        sb.AppendLine("СКАУТ КОДЕКСА — непокрытые имена, найденные в корпусе");
        sb.AppendLine($"Дата: {DateTime.Now:yyyy-MM-dd HH:mm} · кандидатов: {candidates.Count}");
        sb.AppendLine("Формат: частота · файлов · фраза · примеры файлов");
        sb.AppendLine();

        foreach (var c in candidates)
            sb.AppendLine($"{c.Frequency,5} · {c.FileCount,3} · {c.Phrase} · {string.Join(", ", c.Examples)}");

        if (candidates.Count == 0)
            sb.AppendLine("Кандидатов не найдено — либо корпус пуст, либо кодекс покрывает его полностью.");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    // ─── internals ───

    private static bool MatchesKnown(string phrase, List<string> words, ISet<string> known)
    {
        foreach (var w in words)
            if (known.Contains(w)) return true;
        foreach (var k in known)
            if (k.Length >= 3 && phrase.Contains(k, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static IEnumerable<string> SplitSentences(string text) =>
        text.Split(['.', '!', '?', '…', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Слова предложения, начинающиеся с заглавной буквы (длина ≥ 2).</summary>
    private static List<string> CapitalizedWords(string sentence)
    {
        var result = new List<string>();
        foreach (var raw in sentence.Split([' ', '\t', ',', ':', '(', ')', '"', '«', '»', '—', '–'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim('-');
            if (word.Length < 2) continue;
            if (char.IsUpper(word[0]) && char.IsLetter(word[0]))
                result.Add(word);
        }
        return result;
    }

    /// <summary>Предложение начинается со строчной (значит, первый кап-слово — точно не его начало).</summary>
    private static bool sentenceStartsWithLowercase(string sentence)
    {
        foreach (var ch in sentence.TrimStart())
        {
            if (char.IsWhiteSpace(ch) || ch is '"' or '«' or '(' or '—' or '–') continue;
            return char.IsLower(ch);
        }
        return false;
    }
}
