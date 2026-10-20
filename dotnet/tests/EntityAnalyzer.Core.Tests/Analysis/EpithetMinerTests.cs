using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Группа 3 (item 23): автосбор словаря эпитетов — окно вокруг упоминания,
/// эвристика прилагательных, биграммы/триграммы, частотный фильтр, уникальность.
/// </summary>
public class EpithetMinerTests
{
    private static Mention M(string entity, string context) =>
        new(1, entity, entity, 1, context);

    private static FileResult F(string name, params Mention[] mentions) =>
        new(name, name, mentions, mentions.Length, 40, 600, 60.0);

    private static AnalysisSession Session(params FileResult[] files) =>
        new(files,
            new AnalysisSummary(
                files.Sum(f => f.Mentions.Count),
                files.Count(f => f.Mentions.Count > 0),
                files.SelectMany(f => f.Mentions).Select(m => m.Entity).Distinct().Count(),
                files.Sum(f => f.CharCount),
                new Dictionary<string, int>()),
            new QualityReport(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], "0s"),
            "light", DateTimeOffset.Now, TimeSpan.Zero);

    [Fact]
    public void Adjectives_around_mention_are_collected()
    {
        var session = Session(F("a.txt",
            M("Ктулху", "древний Ктулху спал в глубине"),
            M("Ктулху", "Ктулху древний шевелился"),
            M("Ктулху", "ужасный Ктулху пробудился"),
            M("Ктулху", "Ктулху ужасный затих")));

        var data = EpithetMiner.Mine(session);

        var entity = Assert.Single(data);
        Assert.Equal("Ктулху", entity.Entity);
        Assert.Equal(2, entity.Epithets.Count);
        Assert.Equal("древний", entity.Epithets[0].Word);
        Assert.Equal(2, entity.Epithets[0].Count);
        Assert.True(entity.Epithets[0].Unique);
        Assert.Equal("ужасный", entity.Epithets[1].Word);
    }

    [Fact]
    public void Nouns_are_not_epithets()
    {
        var session = Session(F("a.txt",
            M("Ктулху", "жрец Ктулху подошёл"),
            M("Ктулху", "жрец Ктулху замер")));

        Assert.Empty(EpithetMiner.Mine(session));
        Assert.True(EpithetMiner.LooksLikeAdjective("древний"));
        Assert.True(EpithetMiner.LooksLikeAdjective("зловещая"));
        Assert.False(EpithetMiner.LooksLikeAdjective("жрец"));
        Assert.False(EpithetMiner.LooksLikeAdjective("спал"));
    }

    [Fact]
    public void Adjective_bigram_and_trigram_are_collected()
    {
        var session = Session(F("a.txt",
            M("Ктулху", "ужасный великий древний Ктулху"),
            M("Ктулху", "ужасный великий древний Ктулху")));

        var data = EpithetMiner.Mine(session);

        var words = data[0].Epithets.Select(e => e.Word).ToList();
        Assert.Contains("древний", words);
        Assert.Contains("великий древний", words);
        Assert.Contains("ужасный великий древний", words);
        Assert.All(data[0].Epithets, e => Assert.Equal(2, e.Count));
    }

    [Fact]
    public void Frequency_filter_drops_single_occurrences()
    {
        var session = Session(F("a.txt",
            M("Ктулху", "древний Ктулху"),
            M("Ктулху", "Ктулху шёл")));

        Assert.Empty(EpithetMiner.Mine(session));
    }

    [Fact]
    public void Shared_epithet_is_marked_as_common()
    {
        var session = Session(F("a.txt",
            M("Азатот", "тёмный Азатот дремал"),
            M("Азатот", "Азатот тёмный"),
            M("Ктулху", "тёмный Ктулху спал"),
            M("Ктулху", "Ктулху тёмный")));

        var data = EpithetMiner.Mine(session);

        Assert.Equal(2, data.Count);
        var azathoth = data.Single(e => e.Entity == "Азатот");
        var epithet = Assert.Single(azathoth.Epithets);
        Assert.Equal("тёмный", epithet.Word);
        Assert.False(epithet.Unique);
        Assert.Equal(["Ктулху"], epithet.AlsoUsedBy);
    }

    [Fact]
    public void Mention_without_variant_in_context_is_skipped()
    {
        var session = Session(F("a.txt",
            M("Ктулху", "древний бог спал"),
            M("Ктулху", "древний бог замер")));

        Assert.Empty(EpithetMiner.Mine(session));
    }
}
