using EntityAnalyzer.Core.Fuzzy;
using EntityAnalyzer.Core.Themes;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

public class FuzzyFinderTests
{
    private static EntityCatalog Catalog() => new(
    [
        new EntityDefinition("Наг и Йеб", "Nug and Yeb", ["Наг и Йеб", "Нуг и Йеб"], [])
    ]);

    private readonly FuzzyFinder _finder = new(threshold: 85);

    [Fact]
    public void Single_typo_in_missed_entity_is_caught()
    {
        var text = "Жрец произнёл: Приблизься, Наг и Йэб, и услышь.";
        var hits = _finder.FindMissed(Catalog(), text, new HashSet<string>());
        Assert.Contains(hits, h => h.Entity == "Наг и Йеб" && h.Score >= 85 && h.Window.Contains("Йэб"));
    }

    [Fact]
    public void Already_found_entities_are_skipped()
    {
        var text = "Наг и Йэб пришли. Наг и Йеб тоже тут.";
        var hits = _finder.FindMissed(Catalog(), text, new HashSet<string> { "Наг и Йеб" });
        Assert.Empty(hits);
    }

    [Fact]
    public void Exact_variant_hit_scores_hundred()
    {
        // Entity deliberately marked as NOT found although the text contains it verbatim.
        var text = "Одинокий шёл по улице.";
        var catalog = new EntityCatalog([new EntityDefinition("Одинокий", "Lonely One", ["Одинокий"], [])]);
        var hits = _finder.FindMissed(catalog, text, new HashSet<string>());
        Assert.Contains(hits, h => h.Score == 100 && h.MatchedVariant == "Одинокий");
    }

    [Fact]
    public void Weak_similarities_rejected()
    {
        var hits = _finder.FindMissed(Catalog(), "Совершенно посторонний текст про погоду и урожай.", new HashSet<string>());
        Assert.Empty(hits);
    }
}

public class ThemeClassifierTests
{
    [Fact]
    public void Ritual_context_classified()
    {
        var theme = ThemeClassifier.Classify("Призыв свершился у алтаря; жертва была принята кругом огня.");
        Assert.Equal("Ритуал и призывание", theme);
    }

    [Fact]
    public void Narrative_context_classified()
    {
        var theme = ThemeClassifier.Classify("Он сказал это тихо, потом пошёл к двери и увидел тень.");
        Assert.Equal("Художественный сюжет", theme);
    }

    [Fact]
    public void Neutral_text_has_no_theme()
    {
        Assert.Equal("", ThemeClassifier.Classify("Просто текст про погоду и урожай."));
    }

    [Fact]
    public void Distribution_counts_only_themed_contexts()
    {
        var dist = ThemeClassifier.Distribution(new[]
        {
            "Призыв у алтаря",
            "Он сказал и пошёл",
            "нейтральный набор слов",
            "ритуал заклинания"
        });
        Assert.Equal(2, dist["Ритуал и призывание"]);
        Assert.Equal(1, dist["Художественный сюжет"]);
        Assert.Equal(3, dist.Values.Sum());
    }
}
