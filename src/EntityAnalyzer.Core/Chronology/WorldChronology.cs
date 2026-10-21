using System.Globalization;
using System.Text.RegularExpressions;
using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.Core.Chronology;

/// <summary>Вид хронологической записи.</summary>
public enum ChronoKind
{
    /// <summary>«N лет назад» — давность.</summary>
    YearsAgo,
    /// <summary>Четырёхзначный год (1000–2199).</summary>
    Year,
    /// <summary>Эра/эпоха/цикл («Гиперборейская эра»).</summary>
    Era,
    /// <summary>Временной маркер («в древние времена»).</summary>
    Marker,
}

/// <summary>
/// Запись автособираемой хронологии: вид, подпись, ключ сортировки (для годов и
/// давности), имя паттерна, файл/строка/окрестность и найденные рядом сущности.
/// </summary>
public sealed record ChronologyEntry(
    ChronoKind Kind,
    string Label,
    long? SortKey,
    string PatternName,
    string File,
    string Path,
    int Line,
    string Snippet,
    IReadOnlyList<string> Entities);

/// <summary>
/// Item 6 группы 4: автособираемая хронология мира Мифов — годы, давность
/// («миллион лет назад»), эры/эпохи и временные маркеры собираются из
/// нормализованных текстов корпуса по паттернам. Шкала: сначала давность от
/// самой глубокой, затем годы по возрастанию, затем эры и маркеры без даты.
/// Рядом с записью — сущности, чьи имена видны в окрестности.
/// </summary>
public static partial class WorldChronology
{
    /// <summary>Потолок записей, чтобы шкала оставалась читаемой.</summary>
    public const int MaxEntries = 500;

    /// <summary>Потолок однотипных записей на файл (маркеры и эры повторяются).</summary>
    public const int MaxSameKindPerFile = 6;

    [GeneratedRegex(
        @"(?<pre>(\d[\d\s]{0,12})|(сотни|десятки|сто|двести|триста|четыреста|пятьсот|шестьсот|семьсот|восемьсот|девятьсот|двадцать|тридцать|сорок|пятьдесят|шестьдесят|семьдесят|восемьдесят|девяносто))?\s*" +
        @"(?<unit>миллиард\w*|миллион\w*|тысяч\w*|сотн\w*|десятк\w*)?\s*" +
        @"(?<span>лет|год(?:а|ов)?)\s+(?:тому\s+)?назад",
        RegexOptions.IgnoreCase)]
    private static partial Regex YearsAgoRegex();

    [GeneratedRegex(
        @"\b(?<head>эра|эпоха|эон|цикл|век|тысячелетие)\s+[«""]?(?<name>[А-ЯЁ][\wа-яё'’-]*(?:\s+[А-ЯЁ][\wа-яё'’-]*){0,2})")]
    private static partial Regex EraHeadRegex();

    [GeneratedRegex(
        @"\b(?<name>[А-ЯЁ][\wа-яё'’-]*(?:ая|ская|ий|ый|ой|ое|ское))\s+(?<head>эра|эпоха|эон|циклы?)\b")]
    private static partial Regex EraModifierRegex();

    [GeneratedRegex(
        @"\b(?:в\s+(?:те\s+)?(?:древни[ех]|стародавни[ех]|незапамятны[ех]|давни[ех]|былы[ех]|прежни[ех]|стар[ыо]е)\s+(?:времена|дни|годы|эпохи|века)|" +
        @"(?:до|за)\s+(?:начала|сотворения|зари)\s+(?:времён|времен|мира|земли)|" +
        @"на\s+заре\s+(?:времён|времен|человечества|мира)|" +
        @"задолго\s+до\s+[а-яё]+|" +
        @"в\s+незапамятные\s+времена|" +
        @"во\s+время\s+оно)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MarkerRegex();

    private static readonly Dictionary<string, long> Multipliers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["сотни"] = 100,
        ["десятки"] = 10,
        ["сто"] = 100,
        ["двести"] = 200,
        ["триста"] = 300,
        ["четыреста"] = 400,
        ["пятьсот"] = 500,
        ["шестьсот"] = 600,
        ["семьсот"] = 700,
        ["восемьсот"] = 800,
        ["девятьсот"] = 900,
        ["двадцать"] = 20,
        ["тридцать"] = 30,
        ["сорок"] = 40,
        ["пятьдесят"] = 50,
        ["шестьдесят"] = 60,
        ["семьдесят"] = 70,
        ["восемьдесят"] = 80,
        ["девяносто"] = 90,
    };

    /// <summary>
    /// Собрать хронологию из нормализованных текстов корпуса.
    /// <paramref name="knownEntities"/> — имена сущностей для привязки к записям.
    /// </summary>
    public static IReadOnlyList<ChronologyEntry> Build(
        IReadOnlyList<CorpusText> texts,
        IReadOnlyCollection<string> knownEntities)
    {
        var entries = new List<ChronologyEntry>();
        var perFileKind = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1) Четырёхзначные годы — существующим указателем (единая семантика с «Годами»).
        var years = YearIndexer.Extract(texts);
        foreach (var hit in years)
        {
            entries.Add(new ChronologyEntry(
                ChronoKind.Year,
                hit.Year.ToString(CultureInfo.InvariantCulture),
                hit.Year,
                "Годы",
                hit.File,
                hit.Path,
                hit.Line,
                hit.Snippet,
                FindEntities(hit.Snippet, knownEntities)));
        }

        foreach (var text in texts)
        {
            foreach (var line in Lines(text.Text))
            {
                AddMatches(text, line, YearsAgoRegex(), ChronoKind.YearsAgo, "Давность", entries, perFileKind,
                    knownEntities, m => FormatYearsAgo(ParseYearsAgo(m)));
                AddMatches(text, line, EraHeadRegex(), ChronoKind.Era, "Эры и эпохи", entries, perFileKind,
                    knownEntities, m => Capitalize($"{m.Groups["head"].Value} {m.Groups["name"].Value}"));
                AddMatches(text, line, EraModifierRegex(), ChronoKind.Era, "Эры и эпохи", entries, perFileKind,
                    knownEntities, m => Capitalize($"{m.Groups["name"].Value} {m.Groups["head"].Value}"));
                AddMatches(text, line, MarkerRegex(), ChronoKind.Marker, "Временные маркеры", entries, perFileKind,
                    knownEntities, m => Capitalize(m.Value.Trim()));
            }
        }

        var sorted = entries
            .DistinctBy(e => (e.Kind, e.Label, e.File, e.Line))
            .OrderBy(e => e.SortKey ?? long.MaxValue)
            .ThenBy(e => e.Kind)
            .ThenBy(e => e.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Line)
            .Take(MaxEntries)
            .ToList();
        return sorted;
    }

    private static void AddMatches(
        CorpusText text,
        (int Number, string Line) line,
        Regex regex,
        ChronoKind kind,
        string patternName,
        List<ChronologyEntry> entries,
        Dictionary<string, int> perFileKind,
        IReadOnlyCollection<string> knownEntities,
        Func<Match, string> label)
    {
        foreach (Match match in regex.Matches(line.Line))
        {
            var key = text.File + "\u0000" + kind;
            if (kind is ChronoKind.Era or ChronoKind.Marker)
            {
                perFileKind.TryGetValue(key, out var count);
                if (count >= MaxSameKindPerFile) break;
                perFileKind[key] = count + 1;
            }

            var text2 = label(match).Trim();
            if (text2.Length == 0) continue;
            if (kind == ChronoKind.YearsAgo && ParseYearsAgo(match) <= 0) continue;
            long? sortKey = kind switch
            {
                ChronoKind.YearsAgo => -ParseYearsAgo(match),
                _ => null,
            };
            entries.Add(new ChronologyEntry(
                kind,
                text2,
                sortKey,
                patternName,
                text.File,
                text.Path,
                line.Number,
                Snippet(line.Line),
                FindEntities(line.Line, knownEntities)));
        }
    }

    /// <summary>Число лет «назад» из совпадения; 0 — не распознано.</summary>
    public static long ParseYearsAgo(Match match)
    {
        long multiplier = 1;
        var pre = match.Groups["pre"].Value.Trim();
        if (pre.Length > 0)
        {
            if (Multipliers.TryGetValue(pre, out var wordMultiplier)) multiplier = wordMultiplier;
            else if (long.TryParse(pre.Replace(" ", "").Replace("\u00A0", ""), out var digits)) multiplier = digits;
        }
        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        var scale = unit.Length == 0 ? 1L
            : unit.StartsWith("миллиард", StringComparison.Ordinal) ? 1_000_000_000L
            : unit.StartsWith("миллион", StringComparison.Ordinal) ? 1_000_000L
            : unit.StartsWith("тысяч", StringComparison.Ordinal) ? 1_000L
            : unit.StartsWith("сотн", StringComparison.Ordinal) ? 100L
            : 10L;
        return multiplier * scale;
    }

    /// <summary>«≈ 1 млн лет назад», «≈ 250 тыс. лет назад», «≈ 40 лет назад».</summary>
    public static string FormatYearsAgo(long years) => years switch
    {
        >= 1_000_000_000 => $"≈ {years / 1_000_000_000.0:0.#} млрд лет назад",
        >= 1_000_000 => $"≈ {years / 1_000_000.0:0.#} млн лет назад",
        >= 1_000 => $"≈ {years / 1_000.0:0.#} тыс. лет назад",
        _ => $"≈ {years} лет назад",
    };

    /// <summary>Век по году: 1928 → «XX век».</summary>
    public static string CenturyLabel(int year)
    {
        if (year <= 0) return "";
        var century = (year - 1) / 100 + 1;
        return $"{century} век";
    }

    private static IEnumerable<(int Number, string Line)> Lines(string text)
    {
        var number = 0;
        foreach (var line in text.Split('\n'))
        {
            number++;
            if (line.Trim().Length > 0)
                yield return (number, line);
        }
    }

    private static string Snippet(string line, int max = 200)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Сущности, чьи имена видны в строке: одиночные слова сверяются по словарю,
    /// многословные — подстрокой по границам; порядок — по появлению в тексте.
    /// </summary>
    public static IReadOnlyList<string> FindEntities(string line, IReadOnlyCollection<string> knownEntities)
    {
        if (knownEntities.Count == 0 || line.Length == 0) return [];
        var found = new List<(int Pos, string Name)>();
        foreach (var raw in knownEntities)
        {
            var name = raw.Trim();
            if (name.Length < 3) continue;
            if (name.Any(char.IsWhiteSpace))
            {
                var pos = line.IndexOf(name, StringComparison.OrdinalIgnoreCase);
                while (pos >= 0)
                {
                    var before = pos == 0 || !char.IsLetterOrDigit(line[pos - 1]);
                    var after = pos + name.Length >= line.Length || !char.IsLetterOrDigit(line[pos + name.Length]);
                    if (before && after)
                    {
                        found.Add((pos, name));
                        break;
                    }
                    pos = line.IndexOf(name, pos + 1, StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                var pos = 0;
                while ((pos = line.IndexOf(name, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var before = pos == 0 || !char.IsLetterOrDigit(line[pos - 1]);
                    var after = pos + name.Length >= line.Length || !char.IsLetterOrDigit(line[pos + name.Length]);
                    if (before && after)
                    {
                        found.Add((pos, name));
                        break;
                    }
                    pos++;
                }
            }
        }
        return [.. found.OrderBy(f => f.Pos).Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(5)];
    }
}
