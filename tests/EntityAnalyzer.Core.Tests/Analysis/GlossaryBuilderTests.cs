using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Группа 3 (item 25): кандидаты в глоссарий — заглавные слова внутри
/// предложения, не сущности; начало фразы и стоп-слова отбрасываются.
/// </summary>
public class GlossaryBuilderTests
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
    public void Mid_sentence_capitalized_words_are_candidates()
    {
        var session = Session(F("a.txt",
            M("Наг", "в храме Некрономикон лежал свиток"),
            M("Наг", "рядом Некрономикон гудел")));

        var data = GlossaryBuilder.Build(session);

        var candidate = Assert.Single(data);
        Assert.Equal("Некрономикон", candidate.Word);
        Assert.Equal(2, candidate.Count);
    }

    [Fact]
    public void Sentence_start_and_stopwords_are_skipped()
    {
        var session = Session(F("a.txt",
            M("Наг", "Наг спал"),
            M("Наг", "Странник видел Наг"),
            M("Наг", "был Он рядом с Наг")));

        Assert.Empty(GlossaryBuilder.Build(session));
    }

    [Fact]
    public void Known_entities_and_variants_are_excluded()
    {
        var session = Session(F("a.txt",
            M("Йеб", "великий Йеб пришёл"),
            M("Йеб", "снова Йеб ушёл")));

        Assert.Empty(GlossaryBuilder.Build(session));
    }

    [Fact]
    public void Min_count_filters_single_occurrences()
    {
        var session = Session(F("a.txt",
            M("Наг", "в храме Некрономикон лежал")));

        Assert.Empty(GlossaryBuilder.Build(session));
    }

    [Fact]
    public void Examples_are_attached()
    {
        var session = Session(F("a.txt",
            M("Наг", "в храме Некрономикон лежал свиток"),
            M("Наг", "рядом Некрономикон гудел"),
            M("Наг", "и снова Некрономикон")));

        var data = GlossaryBuilder.Build(session);

        var candidate = Assert.Single(data);
        Assert.Equal(3, candidate.Count);
        Assert.Equal(GlossaryBuilder.MaxExamples, candidate.Examples.Count);
        Assert.All(candidate.Examples, e => Assert.Contains("Некрономикон", e, StringComparison.Ordinal));
    }
}
