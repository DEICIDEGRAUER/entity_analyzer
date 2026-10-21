using EntityAnalyzer.Core.Matching;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Matching;

public class AhoCorasickTests
{
    private static List<AhoMatch<string>> Find(string[] patterns, string text)
    {
        var ac = new AhoCorasick<string>();
        foreach (var p in patterns) ac.Add(p, p);
        ac.Build();
        return ac.Find(text).ToList();
    }

    [Fact]
    public void Finds_repeated_non_overlapping_occurrences()
    {
        var ms = Find(new[] { "ab" }, "abab");
        Assert.Equal(2, ms.Count);
        Assert.Equal(new[] { 0, 2 }, ms.Select(m => m.Start));
        Assert.All(ms, m => Assert.Equal("ab", m.Pattern));
        Assert.All(ms, m => Assert.Equal(2, m.End - m.Start));
    }

    [Fact]
    public void Finds_overlapping_occurrences_of_self_overlapping_pattern()
    {
        // "aa" in "aaaa" must be found at 0,1,2 (pyahocorasick-compatible enumeration)
        var ms = Find(new[] { "aa" }, "aaaa");
        Assert.Equal(new[] { 0, 1, 2 }, ms.Select(m => m.Start));
    }

    [Fact]
    public void Finds_multiple_patterns_including_nested_ones()
    {
        var ms = Find(new[] { "he", "she", "hers", "his" }, "ushers");
        var found = ms.Select(m => (m.Pattern, m.Start)).OrderBy(x => x.Pattern).ToList();
        Assert.Contains(("he", 2), found);
        Assert.Contains(("she", 1), found);
        Assert.Contains(("hers", 2), found);
        Assert.DoesNotContain(found, x => x.Pattern == "his");
    }

    [Fact]
    public void Matching_is_case_insensitive_but_returns_original_pattern()
    {
        var ms = Find(new[] { "Йеб" }, "текст про йеба тут. ЙЕБ отдельно.");
        Assert.Equal(2, ms.Count);
        Assert.All(ms, m => Assert.Equal("Йеб", m.Pattern));
    }

    [Fact]
    public void Cyrillic_and_latin_mixed_text_works()
    {
        var ms = Find(new[] { "Nug and Yeb", "Наг и Йеб" }, "...Наг и Йеб... Nug and Yeb...");
        Assert.Equal(2, ms.Count);
        Assert.Contains(ms, m => m.Pattern == "Nug and Yeb");
        Assert.Contains(ms, m => m.Pattern == "Наг и Йеб");
    }

    [Fact]
    public void Non_bmp_case_mapping_uses_string_lowercase()
    {
        var ac = new AhoCorasick<int>();
        ac.Add("\U00010428", 1); // дезеретская строчная
        ac.Build();
        var ms = ac.Find("\U00010400").ToList(); // заглавная
        Assert.Single(ms);
        Assert.Equal(1, ms[0].Payload);
        Assert.Equal(0, ms[0].Start);
    }

    [Fact]
    public void Empty_text_yields_no_matches()
    {
        Assert.Empty(Find(new[] { "ab" }, ""));
    }

    [Fact]
    public void Find_without_Build_throws()
    {
        var ac = new AhoCorasick<string>();
        ac.Add("x", "x");
        Assert.Throws<InvalidOperationException>(() => ac.Find("abc").ToList());
    }

    [Fact]
    public void Duplicate_lowercase_patterns_both_reported_with_own_payloads()
    {
        var ac = new AhoCorasick<int>();
        ac.Add("Yeb", 1);
        ac.Add("yeb", 2);
        ac.Build();
        var ms = ac.Find("yeb yeb").ToList();
        Assert.Equal(4, ms.Count); // each position matched by both patterns
        Assert.Equal(2, ms.Count(m => m.Payload == 1));
        Assert.Equal(2, ms.Count(m => m.Payload == 2));
    }

    [Fact]
    public void Long_text_finds_all_occurrences()
    {
        var text = string.Concat(Enumerable.Repeat("наг и йеб; ", 5000));
        var ms = Find(new[] { "наг и йеб" }, text);
        Assert.Equal(5000, ms.Count);
    }
}
