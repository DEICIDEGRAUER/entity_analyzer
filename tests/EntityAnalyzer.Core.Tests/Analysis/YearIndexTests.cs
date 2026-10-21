using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Группа 3 (item 28): указатель упоминаний по годам — четырёхзначные годы
/// из текстов, схлопывание повторов в файле, хронологическая шкала.
/// </summary>
public class YearIndexTests
{
    private static CorpusText T(string text, string file = "файл.txt") =>
        new(file, @"C:\корпус\" + file, text);

    [Fact]
    public void Years_are_extracted_with_line_and_snippet()
    {
        var hits = YearIndexer.Extract([T("Заметка 1998 года.\nЕщё строка 1928 г.")]);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1998, hits[0].Year);
        Assert.Equal(1, hits[0].Line);
        Assert.Contains("1998", hits[0].Snippet, StringComparison.Ordinal);
        Assert.Equal(1928, hits[1].Year);
        Assert.Equal(2, hits[1].Line);
        Assert.Equal("файл.txt", hits[0].File);
    }

    [Fact]
    public void Out_of_range_and_glued_numbers_are_ignored()
    {
        var hits = YearIndexer.Extract([T("0999 12345 3199 42 12045")]);

        Assert.Empty(hits);
    }

    [Fact]
    public void Duplicate_years_in_file_are_counted_once()
    {
        var hits = YearIndexer.Extract([T("В 1998 и снова в 1998, затем 1999.")]);
        var timeline = YearIndexer.Timeline(hits);

        Assert.Equal(2, timeline.Count);
        Assert.Equal(1998, timeline[0].Year);
        Assert.Equal(2, timeline[0].Total);
        var file = Assert.Single(timeline[0].Files);
        Assert.Equal(2, file.Count);
        Assert.Equal(1, file.Line);
        Assert.Equal(1999, timeline[1].Year);
    }

    [Fact]
    public void Timeline_is_sorted_ascending_and_groups_files()
    {
        var hits = YearIndexer.Extract([
            T("1998 1928 1998", "a.txt"),
            T("1998 2001", "b.txt"),
        ]);

        var timeline = YearIndexer.Timeline(hits);

        Assert.Equal([1928, 1998, 2001], timeline.Select(e => e.Year));
        var e1998 = timeline[1];
        Assert.Equal(3, e1998.Total);
        Assert.Equal(2, e1998.Files.Count);
        Assert.Equal("a.txt", e1998.Files[0].File); // больше вхождений в файле
        Assert.Equal(2, e1998.Files[0].Count);
        Assert.Equal("b.txt", e1998.Files[1].File);
        Assert.Equal(1, e1998.Files[1].Count);
    }

    [Fact]
    public void Empty_and_timeless_texts_yield_nothing()
    {
        Assert.Empty(YearIndexer.Extract([]));
        Assert.Empty(YearIndexer.Extract([T("")]));
        Assert.Empty(YearIndexer.Extract([T("Никаких дат здесь: только слова.")]));
        Assert.Empty(YearIndexer.Timeline([]));
    }

    [Fact]
    public void Per_file_cap_is_respected()
    {
        var hits = YearIndexer.Extract([T("1998 1999 2000 2001")], maxPerFile: 2);

        Assert.Equal(2, hits.Count);
    }
}
