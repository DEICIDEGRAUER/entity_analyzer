using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Chronology;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Chronology;

/// <summary>
/// Группа 4 (item 6): автособираемая хронология мира — годы, давность,
/// эры/эпохи и временные маркеры, сортировка от древнего к новому.
/// </summary>
public class WorldChronologyTests
{
    private static CorpusText T(string text, string file = "мифы.txt") =>
        new(file, @"C:\корпус\" + file, text);

    private static string[] Entities => ["Ктулху", "Гиперборея"];

    [Fact]
    public void Chronology_collects_years_years_ago_eras_and_markers()
    {
        var entries = WorldChronology.Build(
        [
            T("""
              В древние времена Ктулху спал под водой.
              Согласно хроникам, в 1928 году экспедиция достигла берегов.
              Миллион лет назад здесь был океан.
              Наступила Гиперборейская эра.
              В незапамятные времена жрецы возвели храм.
              """)
        ], Entities);

        Assert.Contains(entries, e => e.Kind == ChronoKind.Year && e.Label == "1928" && e.SortKey == 1928);
        var ancient = entries.Single(e => e.Kind == ChronoKind.YearsAgo && e.Label.Contains("млн"));
        Assert.Equal(-1_000_000, ancient.SortKey);
        Assert.Equal("Давность", ancient.PatternName);
        Assert.Contains(entries, e => e.Kind == ChronoKind.Era && e.Label == "Гиперборейская эра");
        Assert.Contains(entries, e => e.Kind == ChronoKind.Marker && e.Label.StartsWith("В древние", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Kind == ChronoKind.Marker && e.Label.StartsWith("В незапамятные", StringComparison.Ordinal));
    }

    [Fact]
    public void Chronology_is_ordered_from_ancient_to_modern()
    {
        var entries = WorldChronology.Build(
        [
            T("""
              1928 год.
              Миллиард лет назад.
              Древние времена.
              1700 год.
              """)
        ], Entities);

        Assert.Equal(-1_000_000_000, entries[0].SortKey);
        var years = entries.Where(e => e.Kind == ChronoKind.Year).Select(e => e.SortKey).ToList();
        Assert.Equal([1700L, 1928L], years);
        Assert.True(entries.Take(2).All(e => e.SortKey is not null));
    }

    [Theory]
    [InlineData("Миллион лет назад.", "≈ 1 млн лет назад", -1_000_000L)]
    [InlineData("Сто тысяч лет назад.", "≈ 100 тыс. лет назад", -100_000L)]
    [InlineData("Сотни тысяч лет назад.", "≈ 100 тыс. лет назад", -100_000L)]
    [InlineData("Десятки тысяч лет назад.", "≈ 10 тыс. лет назад", -10_000L)]
    [InlineData("10 тысяч лет назад.", "≈ 10 тыс. лет назад", -10_000L)]
    [InlineData("Двадцать лет назад.", "≈ 20 лет назад", -20L)]
    [InlineData("250 лет назад.", "≈ 250 лет назад", -250L)]
    [InlineData("Миллиард лет назад.", "≈ 1 млрд лет назад", -1_000_000_000L)]
    public void Years_ago_formats_and_sorts(string text, string label, long sortKey)
    {
        var entries = WorldChronology.Build([T(text)], Entities);

        var entry = Assert.Single(entries, e => e.Kind == ChronoKind.YearsAgo);
        Assert.Equal(label, entry.Label);
        Assert.Equal(sortKey, entry.SortKey);
    }

    [Fact]
    public void Entities_are_attached_from_the_same_line_with_word_boundaries()
    {
        var entries = WorldChronology.Build(
        [
            T("В 1928 году Ктулху проснулся.\nКтулхуподобный ужас не считается.")
        ], Entities);

        var year = entries.Single(e => e.Kind == ChronoKind.Year && e.Label == "1928");
        Assert.Contains("Ктулху", year.Entities);

        var found = WorldChronology.FindEntities("Ктулхуподобный ужас", Entities);
        Assert.DoesNotContain("Ктулху", found);
        Assert.Contains("Ктулху", WorldChronology.FindEntities("старый Ктулху спит", Entities));
    }

    [Fact]
    public void Chronology_deduplicates_repeated_lines_and_caps_entries()
    {
        var text = string.Join('\n', Enumerable.Repeat("В древние времена.", 50));
        var entries = WorldChronology.Build([T(text)], Entities);

        Assert.Equal(WorldChronology.MaxSameKindPerFile,
            entries.Count(e => e.Kind == ChronoKind.Marker));
        Assert.True(entries.Count <= WorldChronology.MaxEntries);
    }

    [Theory]
    [InlineData(1900, "19 век")]
    [InlineData(1928, "20 век")]
    [InlineData(2000, "20 век")]
    [InlineData(2001, "21 век")]
    public void Century_label_is_human_readable(int year, string label)
    {
        Assert.Equal(label, WorldChronology.CenturyLabel(year));
    }
}
