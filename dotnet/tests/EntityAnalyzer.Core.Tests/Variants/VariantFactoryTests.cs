using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Variants;

public class TransliteratorTests
{
    [Theory]
    [InlineData("Yeb", "Йеб")]
    [InlineData("Nug", "Нуг")]
    [InlineData("Cthulhu", "Ктулху")]
    [InlineData("Shub", "Шуб")]
    public void Basic_transliteration_handles_digraphs(string eng, string expectedRus)
        => Assert.Equal(expectedRus, Transliterator.Basic(eng));
}

public class VariantFactoryTests
{
    private readonly VariantFactory _f = new();

    [Fact]
    public void Generates_base_translit_and_hyphen_shapes()
    {
        var vs = _f.Generate("Nug and Yeb", deepCorrupt: false, iterations: 1);
        Assert.Contains("Нуг анд Йеб", vs);
        Assert.Contains("Нуг-анд-Йеб", vs);
        Assert.Contains("НугандЙеб", vs);
    }

    [Fact]
    public void Russian_declensions_appended_for_consonant_endings()
    {
        var vs = _f.Generate("Азатот", deepCorrupt: false, iterations: 1);
        Assert.Contains("Азатот", vs);
        Assert.Contains("Азатота", vs);
        Assert.Contains("Азатоту", vs);
        Assert.Contains("Азатотом", vs);
        Assert.Contains("Азатоте", vs);
    }

    [Fact]
    public void Deep_corruption_mutates_consonants()
    {
        var corrupted = Corruptor.Corrupt("кот");
        Assert.Contains(corrupted, c => c == "гот");  // к→г
        Assert.Contains(corrupted, c => c == "кохт"); // х inserted after vowel? (о→+х)
        Assert.Contains(corrupted, c => c == "кт" || c == "ко" || c == "от"); // deletions
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        var a = new VariantFactory().Generate("Nyarlathotep");
        var b = new VariantFactory().Generate("Nyarlathotep");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Variant_cap_is_respected()
    {
        var f = new VariantFactory(maxVariants: 50);
        Assert.True(f.Generate("Azathoth the Blind Idiot God").Count <= 50);
    }

    [Fact]
    public void Short_variants_filtered_out()
    {
        var vs = _f.Generate("Io", deepCorrupt: false);
        Assert.All(vs, v => Assert.True(v.Length >= 3));
    }

    [Fact]
    public void Empty_canon_yields_nothing()
    {
        Assert.Empty(_f.Generate(""));
    }
}

public class EntityCatalogTests
{
    private static EntityDefinition Def => new(
        Name: "Наг и Йеб",
        Canon: "Nug and Yeb",
        Irregular: ["Наг и Йеб", "Йеб"],
        Titles: ["Владыка Великой Расы"]);

    [Fact]
    public void Catalog_merges_irregular_titles_and_generated()
    {
        var catalog = new EntityCatalog([Def]);
        var variants = catalog.VariantsByEntity["Наг и Йеб"];
        Assert.Contains("Наг и Йеб", variants);          // irregular kept verbatim
        Assert.Contains("Владыка Великой Расы", variants); // title merged
        Assert.Contains("Нуг анд Йеб", variants);          // generated translit
    }

    [Fact]
    public void Automaton_maps_variant_back_to_entity()
    {
        var catalog = new EntityCatalog([Def]);
        var ac = catalog.BuildAutomaton();
        var hits = ac.Find("…славили Йеба и Наг и Йеб…").ToList();
        Assert.Contains(hits, h => h.Payload.Entity == "Наг и Йеб" && h.Pattern == "Наг и Йеб");
    }

    [Fact]
    public void Common_word_detection()
    {
        var sleep = new EntityDefinition("Сон", "Sleep", ["Сон"], []);
        var catalog = new EntityCatalog([Def, sleep]);
        Assert.True(catalog.IsCommonWordEntity("Сон"));
        Assert.False(catalog.IsCommonWordEntity("Наг и Йеб"));
    }

    [Fact]
    public void Variants_are_unique()
    {
        var catalog = new EntityCatalog([Def with { Irregular = ["Йеб", "Йеб", "ЙЕБ"] }]);
        var variants = catalog.VariantsByEntity["Наг и Йеб"];
        Assert.Equal(variants.Count, variants.Distinct().Count());
    }
}
