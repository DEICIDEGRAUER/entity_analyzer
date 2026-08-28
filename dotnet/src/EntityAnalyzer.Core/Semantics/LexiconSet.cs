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

    public static LexiconSet Merge(LexiconSet baseSet, LexiconSet? overrides) 
    {
        var result = new LexiconSet();
        foreach (var name in baseSet.Names)
            result.Add(name, baseSet.WordsOf(name));
        if (overrides is not null)
            foreach (var name in overrides.Names)
                result.Add(name, overrides.WordsOf(name));
        return result;
    }

    public IEnumerable<string> WordsOf(string name) =>
        _lexicons.TryGetValue(name, out var s) ? s : [];
}

internal sealed class LexiconFileDto
{
    [JsonPropertyName("lexicons")] public Dictionary<string, List<string>> Lexicons { get; set; } = [];

    public static LexiconSet Parse(string json)
    {
        var set = new LexiconSet();
        var dto = JsonSerializer.Deserialize<LexiconFileDto>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (dto is not null)
            foreach (var (name, words) in dto.Lexicons)
                set.Add(name, words);
        return set;
    }
}
