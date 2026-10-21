using EntityAnalyzer.Core.Filtering;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Filtering;

public class MatchFiltersTests
{
    [Theory]
    [InlineData(" наг ", true)]      // exact word — ok
    [InlineData(" хнаг", false)]     // letter glued on the left
    [InlineData("наги ", false)]     // letter glued on the right
    [InlineData("магнагия", false)]  // embedded mid-word
    public void Letter_adjacent_matches_rejected(string text, bool shouldPass)
    {
        var start = text.IndexOf("наг", StringComparison.Ordinal);
        Assert.Equal(shouldPass, !MatchFilters.IsFalsePositive(text, start, start + 3));
    }

    [Fact]
    public void Digit_adjacent_match_rejected()
    {
        var text = "наг5";
        Assert.True(MatchFilters.IsFalsePositive(text, 0, 3));
    }

    [Fact]
    public void Hyphen_and_apostrophe_neighbors_allowed()
    {
        // Names like "Наг-Иа" or possessive "yeb's" legitimately touch connectors.
        Assert.False(MatchFilters.IsBrokenWord("наг-иа", 0, 3));
        Assert.False(MatchFilters.IsBrokenWord("the yeb's book", 4, 7));
    }
}

public class MatchResolverTests
{
    private static RawMatch M(int s, int e, string variant, string entity) => new(s, e, variant, entity);

    [Fact]
    public void Non_overlapping_matches_all_kept_in_order()
    {
        var input = new List<RawMatch> { M(10, 14, "йеб", "E"), M(0, 5, "наг", "N"), M(20, 26, "культ", "C") };
        var result = MatchResolver.ResolveOverlaps(input);
        Assert.Equal(new[] { 0, 10, 20 }, result.Select(m => m.Start));
    }

    [Fact]
    public void Inner_match_nested_in_outer_is_dropped()
    {
        var outer = M(0, 11, "наг и йеб", "N");
        var inner = M(4, 11, "и йеб", "?");
        var result = MatchResolver.ResolveOverlaps(new List<RawMatch> { inner, outer });
        Assert.Single(result);
        Assert.Equal("наг и йеб", result[0].Variant);
    }

    [Fact]
    public void Later_outer_match_replaces_earlier_inner()
    {
        var inner = M(0, 3, "йеб", "?");
        var outer = M(0, 11, "наг и йеб", "N");
        var result = MatchResolver.ResolveOverlaps(new List<RawMatch> { inner, outer });
        Assert.Single(result);
        Assert.Equal("наг и йеб", result[0].Variant);
    }

    [Fact]
    public void Identical_spans_keep_first_on_priority_tie()
    {
        var a = M(0, 3, "йеб", "A");
        var b = M(0, 3, "йеб", "B");
        var result = MatchResolver.ResolveOverlaps(new List<RawMatch> { a, b });
        Assert.Single(result);
        Assert.Equal("A", result[0].Entity);
    }

    [Fact]
    public void Identical_spans_lower_noise_wins()
    {
        var noisy = new RawMatch(0, 3, "сон", "Sleep", NoiseScore: 0.8);
        var clean = new RawMatch(0, 3, "Сон", "Deity", NoiseScore: 0.1);
        var result = MatchResolver.ResolveOverlaps(new List<RawMatch> { noisy, clean });
        Assert.Single(result);
        Assert.Equal("Deity", result[0].Entity);
    }

    [Fact]
    public void Partial_overlaps_are_both_kept()
    {
        var a = M(0, 6, "абвгде", "?");
        var b = M(4, 10, "гдеёжз", "?");
        var result = MatchResolver.ResolveOverlaps(new List<RawMatch> { a, b });
        Assert.Equal(2, result.Count);
    }
}

public class ListingDensityFilterTests
{
    private static readonly HashSet<string> Signals = new(StringComparer.OrdinalIgnoreCase)
        { "бог", "древний", "ужас", "культ", "god", "ancient" };

    private static readonly string[] All = ["Наг и Йеб", "Ктулху", "Азатот", "Йог-Сотот", "Шуб-Ниггурат", "Юггот"];

    private readonly ListingDensityFilter _f = new(Signals);

    private string Ctx(params string[] names) => string.Join(", ", names);

    [Fact]
    public void Four_or_more_other_entities_is_listing()
    {
        var ctx = Ctx("Наг и Йеб", "Ктулху", "Азатот", "Йог-Сотот", "Юггот") + " упоминаются рядом";
        Assert.True(_f.IsListing(ctx, "Наг и Йеб", All));
    }

    [Fact]
    public void Two_others_with_myth_signal_word_pass()
    {
        var ctx = "Великий бог Ктулху и Азатот спали";
        Assert.False(_f.IsListing(ctx, "Наг и Йеб", All));
    }

    [Fact]
    public void Two_others_without_signal_word_is_listing()
    {
        var ctx = "Ктулху и Азатот сидели в баре";
        Assert.True(_f.IsListing(ctx, "Наг и Йеб", All));
    }

    [Fact]
    public void Single_other_entity_never_lists()
    {
        var ctx = "Ктулху ждал";
        Assert.False(_f.IsListing(ctx, "Наг и Йеб", All));
    }
}

public class ContextRulesTests
{
    private static readonly HashSet<string> Signals = new(StringComparer.OrdinalIgnoreCase)
        { "бог", "древний", "ужас", "культ", "god", "ancient" };

    [Fact]
    public void Common_word_entity_requires_signal_word()
    {
        var rule = new CommonWordContextRule(Signals);
        Assert.False(rule.Allows("просто сон на диване", "Сон", entityIsCommonWord: true));
        Assert.True(rule.Allows("древний бог Сон пробудился", "Сон", entityIsCommonWord: true));
    }

    [Fact]
    public void Non_common_entities_always_pass()
    {
        var rule = new CommonWordContextRule(Signals);
        Assert.True(rule.Allows("что угодно без сигналов", "Наг и Йеб", entityIsCommonWord: false));
    }

    [Fact]
    public void Bare_title_without_reinforcing_marker_rejected()
    {
        var v = new TitleValidator();
        var markers = new[] { "Наг и Йеб", "Nug and Yeb", "Владыка" };
        Assert.False(v.IsValid("Владыка вошёл в комнату", "Наг и Йеб", "Владыка", markers));
    }

    [Fact]
    public void Title_with_canon_nearby_accepted()
    {
        var v = new TitleValidator();
        var markers = new[] { "Наг и Йеб", "Nug and Yeb", "Владыка" };
        Assert.True(v.IsValid("Владыка — так звали Наг и Йеб в деревне", "Наг и Йеб", "Владыка", markers));
    }

    [Fact]
    public void Title_with_no_other_markers_accepted()
    {
        var v = new TitleValidator();
        Assert.True(v.IsValid("Владыка пришёл", "Одинокий", "Владыка", Array.Empty<string>()));
    }
}
