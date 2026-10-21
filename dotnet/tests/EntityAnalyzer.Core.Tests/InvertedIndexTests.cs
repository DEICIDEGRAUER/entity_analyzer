using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R1-2 «Инвертированный индекс корпуса»: вариант → [(файл, позиция)];
/// мгновенный поиск поверх индекса без перечитывания корпуса.
/// </summary>
[Collection("LogSequential")]
public class InvertedIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea_idx_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Empty_index_gives_no_results()
    {
        var index = new InvertedIndex();
        Assert.Empty(index.LookupExact("Ктулху"));
        Assert.Equal(0, index.VariantCount);
    }

    [Fact]
    public void Exact_lookup_finds_all_positions()
    {
        var index = new InvertedIndex();
        index.Add("Ктулху", "a.txt", 10);
        index.Add("Ктулху", "b.txt", 42);
        index.Add("Ктулху", "a.txt", 100);

        var hits = index.LookupExact("Ктулху");
        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, h => h.FileName == "b.txt" && h.Offset == 42);
    }

    [Fact]
    public void Prefix_search_groups_variants()
    {
        var index = new InvertedIndex();
        index.Add("Ктулху", "a.txt", 1);
        index.Add("Ктулхув", "a.txt", 2);
        index.Add("Нага", "b.txt", 3);

        var results = index.SearchByPrefix("Ктулх");
        Assert.Equal(2, results.Count); // Ктулху + Ктулхув
        Assert.All(results, g => Assert.StartsWith("Ктулх", g.Key));
    }

    [Fact]
    public void Save_load_roundtrip_preserves_data()
    {
        Directory.CreateDirectory(_dir);
        var index = new InvertedIndex();
        index.Add("Ктулху", "a.txt", 10);
        index.Add("Нага", "b.txt", 20);
        var path = Path.Combine(_dir, "index.json");

        index.Save(path);
        var loaded = InvertedIndex.Load(path);

        Assert.Equal(index.VariantCount, loaded.VariantCount);
        Assert.Equal(20, loaded.LookupExact("Нага").Single().Offset);
    }

    [Fact]
    public void Build_from_session_texts_indexes_words()
    {
        var text = EntityAnalyzer.Core.Text.TextNormalizer.Normalize("Жрец Ктулху шептал. Ктулху спал.");
        var index = InvertedIndex.BuildFromText(text, "doc.txt");
        Assert.True(index.LookupExact("жрец").Count > 0 || index.LookupExact("Жрец").Count > 0);
        Assert.Equal(2, index.LookupExact("Ктулху").Count);
    }
}
