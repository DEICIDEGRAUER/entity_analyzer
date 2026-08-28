using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R1-10 «Timeline корпуса»: лента файлов по датам модификации с отметками
/// упоминаний — группировка по месяцам, хронологический порядок.
/// </summary>
public class TimelineModelTests
{
    [Fact]
    public void Empty_input_gives_no_entries()
    {
        Assert.Empty(TimelineModel.Build([]));
    }

    [Fact]
    public void Files_are_grouped_by_month()
    {
        var entries = TimelineModel.Build(
        [
            ("a.txt", new DateTime(1928, 3, 5), 4),
            ("b.txt", new DateTime(1928, 3, 20), 2),
            ("c.txt", new DateTime(1928, 7, 1), 1),
        ]);
        Assert.Equal(2, entries.Count);
        var march = entries[0];
        Assert.Equal("1928-03", march.Label);
        Assert.Equal(2, march.FileCount);
        Assert.Equal(6, march.Mentions);
    }

    [Fact]
    public void Months_are_chronological()
    {
        var entries = TimelineModel.Build(
        [
            ("late.txt", new DateTime(1935, 1, 1), 1),
            ("early.txt", new DateTime(1919, 12, 31), 2),
        ]);
        Assert.Equal(["1919-12", "1935-01"], entries.Select(e => e.Label));
    }

    [Fact]
    public void Entries_keep_file_details_sorted_by_date()
    {
        var entries = TimelineModel.Build(
        [
            ("b.txt", new DateTime(1928, 3, 20), 1),
            ("a.txt", new DateTime(1928, 3, 5), 2),
        ]);
        Assert.Equal(["a.txt", "b.txt"], entries[0].Files.Select(f => f.FileName));
    }
}
