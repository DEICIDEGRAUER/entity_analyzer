using System.Text.Json;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Позиция варианта в корпусе.</summary>
public sealed record IndexEntry(string FileName, int Offset);

/// <summary>
/// R1-2 «Инвертированный индекс корпуса»: вариант → [(файл, позиция)].
/// Строится из нормализованных текстов (BuildFromText) или наполняется
/// вручную; персистится JSON'ом рядом с кэшем. Поиск поверх индекса —
/// мгновенный, без перечитывания корпуса.
/// </summary>
public sealed class InvertedIndex
{
    private readonly Dictionary<string, List<IndexEntry>> _postings =
        new(StringComparer.Ordinal);

    /// <summary>Число уникальных вариантов в индексе.</summary>
    public int VariantCount => _postings.Count;

    public void Add(string variant, string fileName, int offset)
    {
        if (variant.Length == 0) return;
        if (!_postings.TryGetValue(variant, out var list))
            _postings[variant] = list = [];
        list.Add(new IndexEntry(fileName, offset));
    }

    /// <summary>Слияние другого индекса в этот (для пофайловой сборки).</summary>
    public void Merge(InvertedIndex other)
    {
        foreach (var (variant, entries) in other._postings)
            foreach (var e in entries)
                Add(variant, e.FileName, e.Offset);
    }

    public IReadOnlyList<IndexEntry> LookupExact(string variant) =>
        _postings.TryGetValue(variant, out var list) ? list : [];

    /// <summary>Группы «вариант → позиции» для вариантов с данным префиксом.</summary>
    public IReadOnlyList<IGrouping<string, IndexEntry>> SearchByPrefix(string prefix) =>
    [
        .. _postings
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (IGrouping<string, IndexEntry>)new EntryGroup(kv.Key, kv.Value)),
    ];

    public void Save(string path)
    {
        var dto = _postings.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(e => new[] { e.FileName, e.Offset.ToString() }).ToList(),
            StringComparer.Ordinal);
        File.WriteAllText(path, JsonSerializer.Serialize(dto), System.Text.Encoding.UTF8);
    }

    public static InvertedIndex Load(string path)
    {
        var index = new InvertedIndex();
        var dto = JsonSerializer.Deserialize<Dictionary<string, List<string[]>>>(File.ReadAllText(path));
        if (dto is null) return index;
        foreach (var (variant, entries) in dto)
            foreach (var e in entries)
                if (e.Length == 2 && int.TryParse(e[1], out var offset))
                    index.Add(variant, e[0], offset);
        return index;
    }

    /// <summary>Индексация нормализованного текста: каждое слово → позиция.</summary>
    public static InvertedIndex BuildFromText(string normalizedText, string fileName)
    {
        var index = new InvertedIndex();
        var pos = 0;
        foreach (var raw in normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var wordStart = normalizedText.IndexOf(raw, pos, StringComparison.Ordinal);
            if (wordStart < 0) wordStart = pos;
            var bare = raw.Trim(',', '.', ';', ':', '!', '?');
            if (bare.Length > 0) index.Add(bare, fileName, wordStart);
            pos = wordStart + raw.Length;
        }
        return index;
    }

    private sealed class EntryGroup(string key, IEnumerable<IndexEntry> items)
        : IGrouping<string, IndexEntry>
    {
        private readonly List<IndexEntry> _items = [.. items];
        public string Key => key;
        public IEnumerator<IndexEntry> GetEnumerator() => _items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }
}
