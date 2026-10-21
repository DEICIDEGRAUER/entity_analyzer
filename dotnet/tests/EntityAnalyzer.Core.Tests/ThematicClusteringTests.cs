using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-3 «Тематическая кластеризация корпуса»: TF-IDF по токенам +
/// k-means без единого NuGet → каждый файл получает тему; детерминированно
/// (инициализация по разнесённым точкам, фиксированные итерации).
/// </summary>
public class ThematicClusteringTests
{
    private static string Sea() =>
        "море волны корабль берег море прилив шторм море волны";

    private static string Cult() =>
        "культ алтарь жрец ритуал культ алтарь жрец ритуал культ алтарь";

    [Fact]
    public void Empty_corpus_gives_no_themes()
    {
        var result = TfidfKMeans.Cluster(new Dictionary<string, string>(), k: 2);
        Assert.Empty(result.Assignments);
    }

    [Fact]
    public void Distinct_word_sets_split_into_two_themes()
    {
        var docs = new Dictionary<string, string>
        {
            ["sea1.txt"] = Sea(),
            ["sea2.txt"] = "волны моря бьются о берег шторм прилив волны",
            ["cult1.txt"] = Cult(),
            ["cult2.txt"] = "жрец читает ритуал у алтаря культа жрец ритуал",
        };
        var result = TfidfKMeans.Cluster(docs, k: 2);
        Assert.Equal(4, result.Assignments.Count);
        // Морские документы в одной теме, культовые — в другой.
        Assert.Equal(
            result.Assignments["sea1.txt"],
            result.Assignments["sea2.txt"]);
        Assert.Equal(
            result.Assignments["cult1.txt"],
            result.Assignments["cult2.txt"]);
        Assert.NotEqual(
            result.Assignments["sea1.txt"],
            result.Assignments["cult1.txt"]);
    }

    [Fact]
    public void Clustering_is_deterministic()
    {
        var docs = new Dictionary<string, string>
        {
            ["a.txt"] = Sea(), ["b.txt"] = Cult(),
            ["c.txt"] = "море шторм", ["d.txt"] = "жрец алтарь",
        };
        var r1 = TfidfKMeans.Cluster(docs, k: 2);
        var r2 = TfidfKMeans.Cluster(docs, k: 2);
        Assert.Equal(r1.Assignments, r2.Assignments);
        Assert.Equal(r1.TopTermsPerTheme, r2.TopTermsPerTheme);
    }

    [Fact]
    public void Top_terms_reflect_theme_vocabulary()
    {
        var docs = new Dictionary<string, string> { ["s.txt"] = Sea(), ["c.txt"] = Cult() };
        var result = TfidfKMeans.Cluster(docs, k: 2);
        var seaTheme = result.Assignments["s.txt"];
        var terms = result.TopTermsPerTheme[seaTheme];
        Assert.Contains(terms, t => t.Contains("море") || t.Contains("волны"));
    }

    [Fact]
    public void K_larger_than_docs_is_safe()
    {
        var docs = new Dictionary<string, string> { ["only.txt"] = "текст слов" };
        var result = TfidfKMeans.Cluster(docs, k: 3);
        Assert.Single(result.Assignments);
    }
}
