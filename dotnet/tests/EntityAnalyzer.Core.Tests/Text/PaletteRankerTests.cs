using EntityAnalyzer.Core.Text;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

public class PaletteRankerTests
{
    private sealed record Candidate(string Title, string SearchText, int Priority = 0)
        : IPaletteSearchItem;

    [Fact]
    public void Title_match_outranks_context_only_match()
    {
        Candidate[] items =
        [
            new("Файл · отчёт", "Файл · отчёт 5 цитат · Упоминание Гуль", 0),
            new("Гуль", "Гуль · Мифологическое описание", 0),
        ];

        var ranked = PaletteRanker.Rank(items, "гуль");

        Assert.Equal(2, ranked.Count);
        Assert.Equal("Гуль", ranked[0].Title);
    }

    [Fact]
    public void Priority_boosts_title_matches()
    {
        Candidate[] items =
        [
            new("Гуль", "Гуль", 1),
            new("Гуль", "Гуль", 10),
        ];

        var ranked = PaletteRanker.Rank(items, "гуль");

        Assert.Equal(10, ranked[0].Priority);
        Assert.Equal(1, ranked[1].Priority);
    }

    [Fact]
    public void Context_priority_cannot_override_title_match()
    {
        Candidate[] items =
        [
            new("Файл · архив", "Файл · архив гуль", 1000),
            new("Гуль", "Гуль", 0),
        ];

        var ranked = PaletteRanker.Rank(items, "гуль");

        Assert.Equal("Гуль", ranked[0].Title);
    }

    [Fact]
    public void Empty_query_keeps_source_order_without_limit()
    {
        var items = Enumerable.Range(0, 205)
            .Select(i => new Candidate($"Команда {i}", $"Команда {i}"))
            .ToArray();

        var ranked = PaletteRanker.Rank(items, " ");

        Assert.Equal(205, ranked.Count);
        Assert.Equal("Команда 0", ranked[0].Title);
        Assert.Equal("Команда 204", ranked[^1].Title);
    }

    [Fact]
    public void Nonempty_query_limits_to_200_after_ranking()
    {
        var items = Enumerable.Range(0, 205)
            .Select(i => new Candidate($"Гуль {i}", $"Гуль {i}"))
            .ToArray();

        var ranked = PaletteRanker.Rank(items, "гуль");

        Assert.Equal(200, ranked.Count);
    }

    [Fact]
    public void Duplicate_search_texts_are_not_collapsed()
    {
        Candidate[] items =
        [
            new("Упоминание · Гуль", "Упоминание · Гуль один", 35),
            new("Упоминание · Гуль", "Упоминание · Гуль один", 35),
        ];

        var ranked = PaletteRanker.Rank(items, "гуль");

        Assert.Equal(2, ranked.Count);
        Assert.All(ranked, item => Assert.Equal("Упоминание · Гуль", item.Title));
    }

    [Fact]
    public void Title_match_survives_very_long_context()
    {
        var aliases = string.Join(' ', Enumerable.Repeat("длинный_алиас", 1000));
        Candidate[] items =
        [
            new("Кодекс · Наг и Йеб", $"Кодекс · Наг и Йеб {aliases}", 40),
        ];

        var ranked = PaletteRanker.Rank(items, "наг");

        Assert.Single(ranked);
    }

    [Fact]
    public void Codex_title_with_nag_is_found()
    {
        Candidate[] items =
        [
            new("Кодекс · Наг и Йеб",
                "Кодекс · Наг и Йеб Nug and Yeb Наг и Йеб The Twin Blasphemies", 40),
        ];

        var ranked = PaletteRanker.Rank(items, "наг");

        Assert.Single(ranked);
        Assert.Equal("Кодекс · Наг и Йеб", ranked[0].Title);
    }

    [Fact]
    public void Unmatched_query_returns_empty()
    {
        Candidate[] items = [new("Обзор", "Обзор · страница")];

        Assert.Empty(PaletteRanker.Rank(items, "zzz"));
    }
}
