using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Группа 3 (item 29): интертекстуальные связи — общие 4-граммы («дословно»)
/// и общие 3-граммы вне дословных совпадений («близко») между файлами.
/// </summary>
public class IntertextFinderTests
{
    private static CorpusText T(string text, string file) => new(file, @"C:\корпус\" + file, text);

    [Fact]
    public void Exact_repeats_across_files_are_found()
    {
        var data = IntertextFinder.Find([
            T("старый наг сидел у храма всю ночь", "a.txt"),
            T("наг сидел у храма всю ночь и слушал", "b.txt"),
        ]);

        var pair = Assert.Single(data);
        Assert.Equal("a.txt", pair.FileA);
        Assert.Equal("b.txt", pair.FileB);
        Assert.Equal(3, pair.SharedCount);
        Assert.All(pair.Matches, m => Assert.Equal("дословно", m.Kind));
        Assert.Contains("наг сидел у храма", pair.Matches[0].FragmentA, StringComparison.Ordinal);
    }

    [Fact]
    public void Near_repeats_are_reported_when_exact_absent()
    {
        var data = IntertextFinder.Find([
            T("тёмный наг сидел у воды тихо", "a.txt"),
            T("старый наг сидел у стены мрачно", "b.txt"),
        ]);

        var pair = Assert.Single(data);
        var match = Assert.Single(pair.Matches);
        Assert.Equal("близко", match.Kind);
        Assert.Contains("наг сидел у", match.FragmentA, StringComparison.Ordinal);
        Assert.Contains("наг сидел у", match.FragmentB, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_file_repeats_are_not_paired()
    {
        var data = IntertextFinder.Find([
            T("наг сидел у храма и снова наг сидел у храма", "a.txt"),
        ]);

        Assert.Empty(data);
    }

    [Fact]
    public void Lines_and_both_fragments_are_reported()
    {
        var data = IntertextFinder.Find([
            T("первая строка\nстарый наг сидел у храма", "a.txt"),
            T("и снова ночь\nнаг сидел у храма тихо", "b.txt"),
        ]);

        var pair = Assert.Single(data);
        var match = pair.Matches[0];
        Assert.Equal(2, match.LineA);
        Assert.Equal(2, match.LineB);
        Assert.Contains("храма", match.FragmentA, StringComparison.Ordinal);
        Assert.Contains("храма", match.FragmentB, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_common_shingles_are_skipped()
    {
        var data = IntertextFinder.Find([
            T("наг сидел у храма сегодня", "a.txt"),
            T("наг сидел у храма вчера", "b.txt"),
        ], maxFilesPerShingle: 1);

        Assert.Empty(data);
    }

    [Fact]
    public void Empty_corpus_and_single_file_yield_nothing()
    {
        Assert.Empty(IntertextFinder.Find([]));
        Assert.Empty(IntertextFinder.Find([T("наг сидел у храма", "a.txt")]));
        Assert.Empty(IntertextFinder.Find([T("", "a.txt"), T("", "b.txt")]));
    }

    [Fact]
    public void Short_files_without_shingles_yield_nothing()
    {
        Assert.Empty(IntertextFinder.Find([T("наг храм", "a.txt"), T("наг храм", "b.txt")]));
    }
}
