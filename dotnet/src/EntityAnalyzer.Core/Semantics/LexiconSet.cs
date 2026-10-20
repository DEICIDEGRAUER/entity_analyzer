using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityAnalyzer.Core.Semantics;

/// <summary>Набор именованных словарей (нормализация — нижний регистр).</summary>
public sealed class LexiconSet
{
    private readonly Dictionary<string, HashSet<string>> _lexicons = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Names => _lexicons.Keys;

    public bool Contains(string lexiconName, string word) =>
        _lexicons.TryGetValue(lexiconName, out var set) && set.Contains(word);

    public void Add(string lexiconName, IEnumerable<string> words)
    {
        if (!_lexicons.TryGetValue(lexiconName, out var set))
            _lexicons[lexiconName] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in words)
            if (!string.IsNullOrWhiteSpace(w))
                set.Add(w.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// Item 46: удалить слова из словаря. Возвращает число реально удалённых
    /// записей (слова, которых в словаре не было, не считаются).
    /// </summary>
    public int Remove(string lexiconName, IEnumerable<string> words)
    {
        if (!_lexicons.TryGetValue(lexiconName, out var set)) return 0;
        var removed = 0;
        foreach (var w in words)
            if (!string.IsNullOrWhiteSpace(w) && set.Remove(w.Trim().ToLowerInvariant()))
                removed++;
        return removed;
    }

    /// <summary>
    /// Слияние встроенных словарей с пользовательскими. Item 46: слово с
    /// префиксом «!» в пользовательском файле удаляет одноимённое слово из
    /// объединённого словаря — так можно чистить встроенные словари
    /// (например, убрать ложное срабатывание «epithets»), не трогая код.
    /// </summary>
    public static LexiconSet Merge(LexiconSet baseSet, LexiconSet? overrides) 
    {
        var result = new LexiconSet();
        foreach (var name in baseSet.Names)
            result.Add(name, baseSet.WordsOf(name));
        if (overrides is not null)
            foreach (var name in overrides.Names)
            {
                var words = overrides.WordsOf(name).ToList();
                var removals = words
                    .Where(w => w.StartsWith('!'))
                    .Select(w => w[1..])
                    .ToList();
                result.Add(name, words.Where(w => !w.StartsWith('!')));
                if (removals.Count > 0)
                    result.Remove(name, removals);
            }
        return result;
    }

    public IEnumerable<string> WordsOf(string name) =>
        _lexicons.TryGetValue(name, out var s) ? s : [];
}

internal sealed class LexiconFileDto
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [JsonPropertyName("lexicons")] public Dictionary<string, List<string>> Lexicons { get; set; } = [];

    public static LexiconSet Parse(string json)
    {
        var set = new LexiconSet();
        var dto = JsonSerializer.Deserialize<LexiconFileDto>(json, Opts);
        if (dto is not null)
            foreach (var (name, words) in dto.Lexicons)
                set.Add(name, words);
        return set;
    }

    /// <summary>Item 46/48: сырые пользовательские правки словарей (порядок слов сохраняется).</summary>
    public static Dictionary<string, List<string>> ParseRaw(string json) =>
        JsonSerializer.Deserialize<LexiconFileDto>(json, Opts)?.Lexicons ?? [];

    /// <summary>Item 48: сериализация пользовательских правок словарей для файла.</summary>
    public static string Serialize(Dictionary<string, List<string>> lexicons) =>
        JsonSerializer.Serialize(new LexiconFileDto { Lexicons = lexicons }, Opts);
}
