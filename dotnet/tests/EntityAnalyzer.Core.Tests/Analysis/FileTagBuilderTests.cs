using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Группа 3 (item 11): автотегирование файлов по преобладающим темам
/// упоминаний (тема уже посчитана движком, здесь — только агрегация).
/// </summary>
public class FileTagBuilderTests
{
    private static Mention M(string theme, string entity = "Наг") =>
        new(1, entity, entity, 1, "контекст", theme);

    private static AnalysisSession MakeSession(params FileResult[] files) =>
        new(files,
            new AnalysisSummary(
                files.Sum(f => f.Mentions.Count),
                files.Count(f => f.Mentions.Count > 0),
                files.SelectMany(f => f.Mentions).Select(m => m.Entity).Distinct().Count(),
                files.Sum(f => f.CharCount),
                new Dictionary<string, int>()),
            new QualityReport(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], "0s"),
            "light", DateTimeOffset.Now, TimeSpan.Zero);

    private static FileResult File(string name, params Mention[] mentions) =>
        new(name, name, mentions, mentions.Length, 40, 600, 60.0);

    [Fact]
    public void Dominant_themes_become_tags()
    {
        var mentions = new List<Mention>();
        for (var i = 0; i < 6; i++) mentions.Add(M("Космология"));
        for (var i = 0; i < 3; i++) mentions.Add(M("Ритуал и призывание"));
        mentions.Add(M("Мифологическое описание"));

        var tags = FileTagBuilder.TagsFor(mentions);

        Assert.Equal(2, tags.Count);
        Assert.Equal("Космология", tags[0]);
        Assert.Equal("Ритуал и призывание", tags[1]);
    }

    [Fact]
    public void Rare_theme_needs_count_and_share()
    {
        var mentions = new List<Mention>();
        for (var i = 0; i < 2; i++) mentions.Add(M("Космология"));
        mentions.Add(M("Ритуал и призывание")); // 1 упоминание из 20 = 5 % — мало
        for (var i = 0; i < 17; i++) mentions.Add(M(""));

        var tags = FileTagBuilder.TagsFor(mentions);

        var tag = Assert.Single(tags);
        Assert.Equal("Космология", tag);
    }

    [Fact]
    public void Small_files_get_their_themes()
    {
        var single = FileTagBuilder.TagsFor([M("Ритуал и призывание")]);
        Assert.Equal("Ритуал и призывание", Assert.Single(single));

        var pair = FileTagBuilder.TagsFor([M("Космология"), M("Ритуал и призывание")]);
        Assert.Equal(2, pair.Count);
    }

    [Fact]
    public void Tags_are_capped()
    {
        var mentions = new List<Mention>();
        foreach (var theme in new[] { "Тема A", "Тема B", "Тема C", "Тема D", "Тема E", "Тема F" })
            for (var i = 0; i < 2; i++) mentions.Add(M(theme));

        var tags = FileTagBuilder.TagsFor(mentions);

        Assert.Equal(FileTagBuilder.MaxTags, tags.Count);
        Assert.Equal("Тема A", tags[0]);
        Assert.Equal("Тема D", tags[3]);
    }

    [Fact]
    public void Build_covers_all_files_and_unions_tags()
    {
        var withThemes = File("с темами.txt",
            M("Космология"), M("Космология"),
            M("Ритуал и призывание"), M("Ритуал и призывание"));
        var withoutThemes = File("без тем.txt", M(""));

        var all = FileTagBuilder.Build(MakeSession(withThemes, withoutThemes));

        Assert.Equal(2, all.Count);
        Assert.Empty(all[1].Tags);
        var union = FileTagBuilder.AllTags(all);
        Assert.Equal(2, union.Count);
        Assert.Contains("Космология", union);
        Assert.Contains("Ритуал и призывание", union);
    }
}
