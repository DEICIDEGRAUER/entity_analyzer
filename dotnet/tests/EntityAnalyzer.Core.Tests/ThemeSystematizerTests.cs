using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Themes;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Группа 3 (item 53): систематизация текстов по тематическим словам —
/// оценки тем по файлам, главная тема (в т.ч. ничья и «Без темы»),
/// группировка, свои темы вроде «Космологии».
/// </summary>
public class ThemeSystematizerTests
{
    private static AnalysisSession SessionOf(params FileResult[] files) => new(
        Files: files,
        Summary: new AnalysisSummary(
            files.Sum(f => f.Mentions.Count),
            files.Length,
            1,
            files.Sum(f => f.CharCount),
            files.SelectMany(f => f.Mentions)
                .GroupBy(m => m.Entity, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)),
        Quality: new QualityReport(10, 2, 1, 0, 0, 0, 3, 0, 1, 2, Array.Empty<(string, string)>(), "1.0s"),
        PresetName: "extended");

    private static FileResult File(string name, params string[] contexts)
    {
        var mentions = contexts
            .Select((context, i) => new Mention(i + 1, "Наг", "Наг", i + 1, context))
            .ToArray();
        return new FileResult(name, "corpus/" + name, mentions, TotalFound: mentions.Length,
            ContextSize: 60, CharCount: 1000, DensityPer10k: 10.0);
    }

    private static ThemeDictionary Geography() => ThemeCatalog.Builtin[0];

    [Fact]
    public void Build_counts_repeats_and_sorts_descending()
    {
        var session = SessionOf(File("a.txt", "Моря и моря — волны у берега.", "Меч и меч."));

        var file = Assert.Single(ThemeSystematizer.Build(session, ThemeCatalog.Builtin));

        Assert.Equal(["География", "Битвы"], file.Scores.Select(s => s.Theme));
        Assert.Equal(3, file.Scores[0].Hits); // «моря» ×2 + «берега»
        Assert.Equal(2, file.Scores[1].Hits); // «меч» ×2
        Assert.Equal("География", file.Dominant);
        Assert.Contains("море", file.Scores[0].Words);
    }

    [Fact]
    public void Build_tie_prefers_earlier_dictionary()
    {
        var session = SessionOf(File("tie.txt", "И море, и меч."));

        var file = Assert.Single(ThemeSystematizer.Build(session, ThemeCatalog.Builtin));

        Assert.Equal(1, file.Scores[0].Hits);
        Assert.Equal(1, file.Scores[1].Hits);
        Assert.Equal(Geography().Name, file.Dominant); // География идёт раньше Битв
    }

    [Fact]
    public void Build_marks_files_without_theme_words()
    {
        var session = SessionOf(File("plain.txt", "Просто погода и урожай."));

        var file = Assert.Single(ThemeSystematizer.Build(session, ThemeCatalog.Builtin));

        Assert.Empty(file.Scores);
        Assert.Equal("", file.Dominant);
        Assert.Equal(ThemeSystematizer.NoTheme, file.DominantText);
    }

    [Fact]
    public void Build_shows_each_marker_word_once()
    {
        var session = SessionOf(File("dup.txt", "Море и моря, и морями."));

        var file = Assert.Single(ThemeSystematizer.Build(session, ThemeCatalog.Builtin));

        Assert.Equal(3, file.Scores[0].Hits);
        Assert.Equal(["море"], file.Scores[0].Words);
    }

    [Fact]
    public void Build_limits_shown_words_but_counts_all_hits()
    {
        var session = SessionOf(File("many.txt", "Море, океан, берег, остров, горы, скала, холмы и долина."));

        var top = Assert.Single(ThemeSystematizer.Build(session, ThemeCatalog.Builtin)).Scores[0];

        Assert.Equal(ThemeSystematizer.MaxWordsShown, top.Words.Count);
        Assert.True(top.Hits > ThemeSystematizer.MaxWordsShown);
    }

    [Fact]
    public void Build_respects_user_themes_like_cosmology()
    {
        var themes = ThemeCatalog.Merge(new Dictionary<string, List<string>>
        {
            ["Космология"] = ["эфир", "первоатом"],
        });
        var session = SessionOf(File("c.txt", "Эфир был везде и во всём."));

        var file = Assert.Single(ThemeSystematizer.Build(session, themes));

        Assert.Equal("Космология", file.Dominant);
        Assert.Equal("Космология", file.Scores[0].Theme);
        Assert.Contains("эфир", file.Scores[0].Words);
    }

    [Fact]
    public void Group_orders_by_file_count_with_untitled_last()
    {
        var session = SessionOf(
            File("g1.txt", "Море и берег."),
            File("g2.txt", "Горы и долина."),
            File("b1.txt", "Меч и щит в битве."),
            File("plain.txt", "Просто погода."));

        var groups = ThemeSystematizer.Group(ThemeSystematizer.Build(session, ThemeCatalog.Builtin));

        Assert.Equal(["География", "Битвы", ThemeSystematizer.NoTheme], groups.Select(g => g.Theme));
        Assert.Equal(2, groups[0].Files.Count);
        Assert.Single(groups[1].Files);
        Assert.Single(groups[2].Files);
        Assert.Equal("g1.txt", groups[0].Files[0].File.FileName);
    }
}
