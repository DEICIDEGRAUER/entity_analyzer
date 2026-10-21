using System.Text.RegularExpressions;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Кандидат в глоссарий: термин, частота и примеры контекстов.</summary>
public sealed record GlossaryCandidate(string Word, int Count, IReadOnlyList<string> Examples);

/// <summary>
/// Группа 3 (item 25): автоглоссарий терминов. Кандидаты — слова с заглавной
/// буквы внутри предложения (в начале фразы заглавная буква ничего не значит),
/// не совпадающие с известными сущностями и их вариантами, встретившиеся
/// не реже minCount раз. «Мифологический контекст» гарантирован природой
/// данных: каждое упоминание приходит вместе со своим контекстом.
/// </summary>
public static partial class GlossaryBuilder
{
    public const int DefaultMinCount = 2;
    public const int DefaultMax = 200;
    public const int MaxExamples = 2;

    /// <summary>Частые слова-«заглавные», которые не являются терминами.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "А", "Но", "И", "В", "Во", "На", "С", "Со", "По", "К", "Ко", "О", "Об", "За", "Из", "От", "До",
        "Не", "Ни", "Что", "Как", "Когда", "Где", "Если", "То", "Так", "Также", "Кроме", "Это", "Этот",
        "Эта", "Эти", "Его", "Её", "Их", "Он", "Она", "Оно", "Они", "Мы", "Вы", "Ты", "Я", "Мой", "Все",
        "Весь", "Вся", "Был", "Была", "Было", "Будет", "Даже", "Уже", "Только", "Именно", "Там", "Тут",
        "Потом", "Затем", "Однако", "Поэтому", "Значит", "Ведь", "Вот", "Да", "Нет", "Ну",
    };

    public static List<GlossaryCandidate> Build(
        AnalysisSession session,
        int minCount = DefaultMinCount,
        int max = DefaultMax)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in session.Files)
            foreach (var m in file.Mentions)
            {
                if (!string.IsNullOrEmpty(m.Entity)) known.Add(m.Entity);
                if (!string.IsNullOrEmpty(m.Variant)) known.Add(m.Variant);
            }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var examples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in session.Files)
            foreach (var m in file.Mentions)
            {
                var context = m.Context ?? "";
                foreach (Match match in WordRegex().Matches(context))
                {
                    var word = match.Value;
                    if (word.Length < 3) continue;
                    if (known.Contains(word) || StopWords.Contains(word)) continue;
                    var prev = PreviousMeaningfulChar(context, match.Index);
                    if (prev is null || IsSentenceStart(prev.Value)) continue;

                    counts[word] = counts.GetValueOrDefault(word) + 1;
                    if (!examples.TryGetValue(word, out var list))
                        examples[word] = list = [];
                    if (list.Count < MaxExamples && !list.Contains(context))
                        list.Add(context);
                }
            }

        return
        [
            .. counts
                .Where(kv => kv.Value >= minCount)
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(Math.Max(1, max))
                .Select(kv => new GlossaryCandidate(
                    kv.Key, kv.Value, examples.GetValueOrDefault(kv.Key) ?? [])),
        ];
    }

    /// <summary>Первый непробельный символ перед позицией (null — начало контекста).</summary>
    private static char? PreviousMeaningfulChar(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
            if (!char.IsWhiteSpace(text[i]))
                return text[i];
        return null;
    }

    /// <summary>Слово стоит сразу после конца фразы, реплики или открывающей скобки — это не термин.</summary>
    private static bool IsSentenceStart(char prev) =>
        prev is '.' or '!' or '?' or '…' or ':' or ';' or '—' or '–'
            or '"' or '«' or '„' or '(' or '[' or ')' or ']';

    [GeneratedRegex(@"\p{Lu}[\p{L}]*(?:[-'’][\p{L}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
