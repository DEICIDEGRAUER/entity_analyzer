using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Группа 3 (item 8): обнаруженные fuzzy-проходом варианты имён вне каталога —
/// маркер «[fuzzy:N%]» снимается, известные написания отбрасываются.
/// </summary>
public class CatalogDiscovererTests
{
    private static Mention M(string entity, string variant, bool fuzzy = true, string context = "контекст") =>
        new(1, entity, variant, 1, context, IsFuzzy: fuzzy);

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

    private static EntityCatalog Catalog(params string[] irregular) =>
        new([new EntityDefinition("Наг", "Наг", irregular, [])], generateVariants: true);

    [Fact]
    public void Fuzzy_spellings_outside_catalog_are_discovered()
    {
        var session = Session(F("a.txt",
            M("Наг", "Кулху [fuzzy:87%]", context: "увидел Кулху в тумане")));

        var data = CatalogDiscoverer.Discover(session, Catalog());

        var candidate = Assert.Single(data);
        Assert.Equal("Наг", candidate.Entity);
        Assert.Equal("Кулху", candidate.Spelling);
        Assert.Equal(1, candidate.Count);
        Assert.Equal("увидел Кулху в тумане", candidate.Example);
    }

    [Fact]
    public void Known_variants_are_skipped()
    {
        var session = Session(F("a.txt",
            M("Наг", "Наге [fuzzy:95%]"),
            M("Наг", "Наг [fuzzy:92%]")));

        Assert.Empty(CatalogDiscoverer.Discover(session, Catalog("Наге")));
    }

    [Fact]
    public void Counts_are_aggregated_case_insensitively()
    {
        var session = Session(F("a.txt",
            M("Наг", "Кулху [fuzzy:87%]"),
            M("Наг", "кулху [fuzzy:90%]"),
            M("Наг", "Кулху [fuzzy:88%]")));

        var candidate = Assert.Single(CatalogDiscoverer.Discover(session, Catalog()));
        Assert.Equal(3, candidate.Count);
        Assert.Equal("Кулху", candidate.Spelling); // первое встреченное написание
    }

    [Fact]
    public void Non_fuzzy_and_short_spellings_are_ignored()
    {
        var session = Session(F("a.txt",
            M("Наг", "Кулху", fuzzy: false),
            M("Наг", "Ху [fuzzy:88%]")));

        Assert.Empty(CatalogDiscoverer.Discover(session, Catalog()));
    }

    [Fact]
    public void Results_are_sorted_by_count_desc()
    {
        var session = Session(F("a.txt",
            M("Наг", "Кулху [fuzzy:87%]"),
            M("Наг", "Кулху [fuzzy:87%]"),
            M("Наг", "Ктолху [fuzzy:86%]"),
            M("Наг", "Йебба [fuzzy:85%]")));

        var data = CatalogDiscoverer.Discover(session, Catalog());

        Assert.Equal(3, data.Count);
        Assert.Equal(2, data[0].Count);
        Assert.Equal("Кулху", data[0].Spelling);
        Assert.Equal("Йебба", data[1].Spelling);
        Assert.Equal("Ктолху", data[2].Spelling);
    }
}
