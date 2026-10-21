using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

public class ReviewNavigatorTests
{
    private static FileResult FileWith(string path, params VerdictKind?[] verdicts)
    {
        var mentions = verdicts
            .Select((verdict, index) => new Mention(
                index + 1, "Наг", "Наг", index + 1, "контекст")
            {
                Verdict = verdict,
            })
            .ToList();
        return new FileResult(Path.GetFileName(path), path, mentions,
            mentions.Count, 0, 0, 0);
    }

    [Fact]
    public void Summary_counts_reviewed_mentions_and_files_with_work()
    {
        var first = FileWith("a.txt", null, VerdictKind.Confirmed);
        var empty = FileWith("b.txt");
        var third = FileWith("c.txt", VerdictKind.Rejected, null, VerdictKind.Uncertain);

        var summary = ReviewNavigator.Summarize([first, empty, third]);

        Assert.Equal(5, summary.Total);
        Assert.Equal(3, summary.Reviewed);
        Assert.Equal(2, summary.Unreviewed);
        Assert.Equal(2, summary.FilesWithUnreviewed);
        Assert.False(summary.IsComplete);
    }

    [Fact]
    public void Empty_corpus_is_complete()
    {
        var summary = ReviewNavigator.Summarize([]);

        Assert.Equal(0, summary.Total);
        Assert.Equal(0, summary.Unreviewed);
        Assert.Equal(0, summary.FilesWithUnreviewed);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void Next_file_skips_reviewed_files_and_wraps_around()
    {
        var reviewedFirst = FileWith("a.txt", VerdictKind.Confirmed);
        var unreviewedFirst = FileWith("b.txt", (VerdictKind?)null);
        var reviewedMiddle = FileWith("c.txt", VerdictKind.Rejected);
        var unreviewedLast = FileWith("d.txt", (VerdictKind?)null);
        FileResult[] files = [reviewedFirst, unreviewedFirst, reviewedMiddle, unreviewedLast];

        Assert.Same(unreviewedFirst, ReviewNavigator.NextFileWithUnreviewed(files, reviewedFirst));
        Assert.Same(unreviewedLast, ReviewNavigator.NextFileWithUnreviewed(files, unreviewedFirst));
        Assert.Same(unreviewedFirst, ReviewNavigator.NextFileWithUnreviewed(files, unreviewedLast));
    }

    [Fact]
    public void Next_file_starts_at_first_when_current_is_not_in_the_list()
    {
        var reviewed = FileWith("a.txt", VerdictKind.Confirmed);
        var unreviewed = FileWith("b.txt", (VerdictKind?)null);
        var missing = FileWith("missing.txt", (VerdictKind?)null);
        FileResult[] files = [reviewed, unreviewed];

        Assert.Same(unreviewed, ReviewNavigator.NextFileWithUnreviewed(files, missing));
    }

    [Fact]
    public void Next_file_returns_null_when_every_mention_has_a_verdict()
    {
        FileResult[] files =
        [
            FileWith("a.txt", VerdictKind.Confirmed),
            FileWith("b.txt", VerdictKind.Rejected, VerdictKind.Uncertain),
            FileWith("c.txt"),
        ];

        Assert.Null(ReviewNavigator.NextFileWithUnreviewed(files, files[0]));
    }
}
