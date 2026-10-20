using System.Text;
using System.Text.RegularExpressions;
using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.Core.Geography;

/// <summary>
/// Item 19 группы 4: автосборка географии Мифов. Сканирует нормализованные
/// тексты корпуса семантическими паттернами классов мест («город Иннсмут»,
/// «Иннсмут — город…»), отдельно ищет известные места из внутренней базы
/// координат и агрегирует упоминания в записи с цитатами. Для найденных мест
/// подставляются координаты: реальные — из базы, вымышленные — из фан-базы.
/// </summary>
public static partial class PlaceExtractor
{
    /// <summary>Потолок упоминаний на файл — защита от городов-«заглушек» в черновиках.</summary>
    public const int MaxMentionsPerFile = 80;

    /// <summary>Потолок цитат на место в агрегированной записи.</summary>
    public const int MaxQuotesPerPlace = 8;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "потом", "затем", "однако", "кроме", "когда", "после", "вскоре", "наконец", "также",
        "именно", "возможно", "кажется", "однажды", "ныне", "теперь", "здесь", "там", "тогда",
        "через", "между", "хотя", "если", "чтобы", "перед", "вместо", "помимо", "благодаря",
        "город", "деревня", "остров", "гора", "река", "море", "озеро", "лес", "долина",
        "храм", "башня", "страна", "планета", "север", "юг", "запад", "восток", "недалеко",
        "около", "возле", "рядом", "под", "над", "этот", "это", "тот", "весь", "самый",
    };

    [GeneratedRegex(@"\b([А-ЯЁA-Z][\wа-яё'’-]*)\b")]
    private static partial Regex Token();

    /// <summary>
    /// Найти упоминания мест: паттерны «класс + имя» и «имя — класс», плюс
    /// словарные совпадения с внутренней базой координат.
    /// </summary>
    public static IReadOnlyList<PlaceMention> Extract(
        IReadOnlyList<CorpusText> texts,
        PlaceHeads heads,
        PlaceGazetteer? gazetteer = null)
    {
        var mentions = new List<PlaceMention>();
        var seen = new HashSet<(string Name, string File, int Line)>(new SeenComparer());

        foreach (var text in texts)
        {
            var perFile = 0;
            var number = 0;
            foreach (var line in text.Text.Split('\n'))
            {
                number++;
                if (line.Trim().Length == 0) continue;
                if (perFile >= MaxMentionsPerFile) break;

                // 1) Класс перед именем: «город Иннсмут», «озеро Иб».
                foreach (var cls in heads.PresentClasses)
                {
                    var words = heads.Words(cls);
                    if (words.Count == 0) continue;
                    var regex = HeadFirstRegex(cls, words);
                    foreach (Match m in regex.Matches(line))
                    {
                        var name = Cleanup(m.Groups["name"].Value);
                        if (IsUsable(name) &&
                            seen.Add((PlaceGazetteer.Normalize(name), text.File, number)))
                        {
                            mentions.Add(new PlaceMention(name, cls, "Класс места",
                                text.File, text.Path, number, Snippet(line)));
                            perFile++;
                        }
                    }
                }

                // 2) Имя перед классом через тире: «Иннсмут — город в Массачусетсе».
                foreach (var cls in heads.PresentClasses)
                {
                    var words = heads.Words(cls);
                    if (words.Count == 0) continue;
                    var regex = NameFirstRegex(cls, words);
                    foreach (Match m in regex.Matches(line))
                    {
                        var name = Cleanup(m.Groups["name"].Value);
                        if (IsUsable(name) &&
                            seen.Add((PlaceGazetteer.Normalize(name), text.File, number)))
                        {
                            mentions.Add(new PlaceMention(name, cls, "Имя — класс",
                                text.File, text.Path, number, Snippet(line)));
                            perFile++;
                        }
                    }
                }

                // 3) Известные места из базы координат — даже без слова-класса.
                if (gazetteer is not null)
                {
                    foreach (Match token in Token().Matches(line))
                    {
                        var word = token.Groups[1].Value;
                        if (word.Length < 2) continue;
                        if (!gazetteer.TryMatch(word, out var known)) continue;
                        var key = (PlaceGazetteer.Normalize(known.Name), text.File, number);
                        if (!seen.Add(key)) continue;
                        mentions.Add(new PlaceMention(word, known.Class, "База мест",
                            text.File, text.Path, number, Snippet(line)));
                        perFile++;
                    }
                }

                if (perFile >= MaxMentionsPerFile) break;
            }
        }

        return mentions;
    }

    /// <summary>
    /// Агрегация: канонические записи мест с координатами, счётчиками и цитатами.
    /// Известные места получают имя/класс/координаты из базы, остальные — по
    /// самому частому написанию и классу-победителю.
    /// </summary>
    public static IReadOnlyList<PlaceEntry> Aggregate(
        IReadOnlyList<PlaceMention> mentions,
        PlaceGazetteer? gazetteer = null)
    {
        var groups = new Dictionary<string, List<PlaceMention>>(StringComparer.Ordinal);
        var canonical = new Dictionary<string, (GazetteerPlace Place, string Name)>(StringComparer.Ordinal);

        foreach (var mention in mentions)
        {
            string key;
            if (gazetteer is not null && gazetteer.TryMatch(mention.Name, out var known))
            {
                key = "g:" + PlaceGazetteer.Normalize(known.Name);
                canonical[key] = (known, known.Name);
            }
            else
            {
                key = "t:" + PlaceGazetteer.Normalize(mention.Name);
            }
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = [];
            list.Add(mention);
        }

        var entries = new List<PlaceEntry>();
        foreach (var (key, rawList) in groups)
        {
            // Одна строка — одно упоминание: паттерн и база могут найти одно и то же имя.
            var list = rawList
                .GroupBy(m => (File: m.File.ToLowerInvariant(), m.Line))
                .Select(g => g.First())
                .ToList();

            var variants = list
                .Select(m => m.Name)
                .GroupBy(n => PlaceGazetteer.Normalize(n), StringComparer.Ordinal)
                .Select(g => g.Key.Length == 0 ? g.First() : g.OrderBy(n => n.Length).First())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();

            var known = canonical.TryGetValue(key, out var c) ? c.Place : null;
            var name = known?.Name ?? MostFrequentName(list);
            var cls = known?.Class ?? list
                .GroupBy(m => m.Class)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => (int)g.Key)
                .First().Key;

            var quotes = list
                .GroupBy(m => m.File, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(m => m.Line).First())
                .OrderBy(q => q.File, StringComparer.OrdinalIgnoreCase)
                .Take(MaxQuotesPerPlace)
                .Select(m => new PlaceQuote(m.File, m.Path, m.Line, m.Snippet))
                .ToList();

            entries.Add(new PlaceEntry(
                name,
                cls,
                known?.Fictional ?? false,
                known?.Point,
                known?.Note ?? "",
                list.Count,
                list.Select(m => m.File).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                variants,
                quotes));
        }

        return [.. entries
            .OrderByDescending(e => e.Mentions)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string MostFrequentName(List<PlaceMention> list) =>
        list.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Length)
            .First().Key;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> HeadCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> NameCache = new();

    private static Regex HeadFirstRegex(PlaceClass cls, IReadOnlyList<string> words)
    {
        var alternation = string.Join("|", words.OrderByDescending(w => w.Length).Select(Regex.Escape));
        return HeadCache.GetOrAdd(cls + "\u0000" + alternation, _ => new Regex(
            @"(?<![\wа-яё])(?i:" + alternation + @")\s+(?<name>[А-ЯЁA-Z][\wа-яё'’-]*(?:\s+[А-ЯЁA-Z][\wа-яё'’-]*){0,2})",
            RegexOptions.Compiled));
    }

    private static Regex NameFirstRegex(PlaceClass cls, IReadOnlyList<string> words)
    {
        var alternation = string.Join("|", words.OrderByDescending(w => w.Length).Select(Regex.Escape));
        return NameCache.GetOrAdd(cls + "\u0000" + alternation, _ => new Regex(
            @"(?<name>[А-ЯЁA-Z][\wа-яё'’-]*(?:\s+[А-ЯЁA-Z][\wа-яё'’-]*){0,2})\s*[—–-]\s*(?i:" + alternation + @")(?![\wа-яё])",
            RegexOptions.Compiled));
    }

    /// <summary>Убрать кавычки и лишние слова из захваченного имени.</summary>
    private static string Cleanup(string name)
    {
        var text = name.Replace("«", "").Replace("»", "").Replace("\"", "").Trim();
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 1 && StopWords.Contains(parts[^1]))
            parts.RemoveAt(parts.Count - 1);
        return string.Join(' ', parts);
    }

    private static bool IsUsable(string name)
    {
        if (name.Length < 3) return false;
        var first = name.Split(' ')[0];
        if (StopWords.Contains(first)) return false;
        // Одиночное слово из одного слога («Ну», «Эх») именем места не считается.
        if (name.Length < 4 && !name.Contains(' ')) return false;
        return name.Any(char.IsLetter);
    }

    private static string Snippet(string line, int max = 200)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private sealed class SeenComparer : IEqualityComparer<(string Name, string File, int Line)>
    {
        public bool Equals((string Name, string File, int Line) x, (string Name, string File, int Line) y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.File, y.File, StringComparison.OrdinalIgnoreCase) &&
            x.Line == y.Line;

        public int GetHashCode((string Name, string File, int Line) obj) =>
            HashCode.Combine(obj.Name.ToLowerInvariant(), obj.File.ToLowerInvariant(), obj.Line);
    }
}
