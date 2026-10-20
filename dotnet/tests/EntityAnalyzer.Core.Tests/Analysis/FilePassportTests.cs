using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Exporting;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Группа 3 (item 24) «Паспорт файла»: агрегация данных прогона по одному
/// файлу + разбивка по главам + Markdown-экспорт.
/// </summary>
public class FilePassportTests
{
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

    private static FileResult File(string name, long chars, params Mention[] mentions) =>
        new(name, name, mentions, mentions.Length, 40, chars,
            chars > 0 ? mentions.Length * 10000.0 / chars : 0);

    [Fact]
    public void Aggregates_entities_patterns_and_themes()
    {
        var mentions = new List<Mention>
        {
            new(1, "Наг и Йеб", "Наг", 1, "жрец у алтаря", "Мифологическое описание", false, 1, 10),
            new(2, "Наг и Йеб", "Йеба", 2, "алтарь Йеба", "Мифологическое описание", false, 1, 40),
            new(3, "Ктулху", "Ктулху", 5, "сон в глубине", "Ритуал и призывание", true, 2, 90),
        };
        var file = new FileResult("статья.txt", @"C:\c\статья.txt", mentions, 4, 40, 200, 200,
            SemanticMatches: [new SemanticMatch("p1", "Сон и пробуждение", @"C:\c\статья.txt", 5, "пробуждается из сна", 60)]);
        var pass = FilePassportBuilder.Build(MakeSession(file), @"C:\c\статья.txt");

        Assert.True(pass.Found);
        Assert.Equal(3, pass.TotalMentions);
        Assert.Equal(2, pass.EntityCount);

        var top = pass.Entities[0];
        Assert.Equal("Наг и Йеб", top.Entity);
        Assert.Equal(2, top.Count);
        Assert.Equal(0, top.Fuzzy);
        Assert.Equal(2, top.Repeats);
        Assert.Equal(["Йеба", "Наг"], top.Variants);
        Assert.Equal("Ктулху", pass.Entities[1].Entity);
        Assert.Equal(1, pass.Entities[1].Fuzzy);

        var pattern = Assert.Single(pass.Patterns);
        Assert.Equal("Сон и пробуждение", pattern.PatternName);
        Assert.Equal(1, pattern.Count);

        Assert.Equal("Мифологическое описание", pass.Themes[0].Theme);
        Assert.Equal(2, pass.Themes[0].Count);
        Assert.Equal(66.7, pass.Themes[0].Share, 1);
        Assert.Equal("Ритуал и призывание", pass.Themes[1].Theme);

        var tag = Assert.Single(pass.Tags);
        Assert.Equal("Мифологическое описание", tag);
    }

    [Fact]
    public void Chapters_are_detected_by_headings()
    {
        var text = "Предисловие без упоминаний.\nГлава 1\n" +
                   new string('а', 120) + "\nГлава 2\n" + new string('б', 120);
        var g1 = text.IndexOf("Глава 1", StringComparison.Ordinal);
        var g2 = text.IndexOf("Глава 2", StringComparison.Ordinal);
        var file = File("роман.txt", text.Length,
            new Mention(1, "Ктулху", "Ктулху", 3, "контекст", "", false, 1, g1 + 30),
            new Mention(2, "Ктулху", "Ктулху", 5, "контекст", "", false, 1, g2 + 30));

        var pass = FilePassportBuilder.Build(MakeSession(file), "роман.txt", text);

        Assert.True(pass.TextAvailable);
        Assert.True(pass.TextAlignsWithAnalysis);
        Assert.Equal(3, pass.Chapters.Count);
        Assert.Equal("Начало", pass.Chapters[0].Label);
        Assert.Equal(0, pass.Chapters[0].Mentions);
        Assert.Equal("Глава 1", pass.Chapters[1].Label);
        Assert.Equal(1, pass.Chapters[1].Mentions);
        Assert.Equal("Глава 2", pass.Chapters[2].Label);
        Assert.Equal(1, pass.Chapters[2].Mentions);
    }

    [Fact]
    public void Roman_and_caps_headings_are_detected()
    {
        var text = "Начало текста.\nI\nпервая часть текста\n\n\nГИПЕРБОРЕЯ\nостальной текст";
        var file = File("хроника.txt", text.Length);
        var pass = FilePassportBuilder.Build(MakeSession(file), "хроника.txt", text);

        Assert.Equal(3, pass.Chapters.Count);
        Assert.Equal("Начало", pass.Chapters[0].Label);
        Assert.Equal("I", pass.Chapters[1].Label);
        Assert.Equal("ГИПЕРБОРЕЯ", pass.Chapters[2].Label);
    }

    [Fact]
    public void Without_headings_falls_back_to_equal_segments()
    {
        var text = new string('а', 800);
        var file = File("поток.txt", text.Length,
            new Mention(1, "Наг", "Наг", 1, "контекст", "", false, 1, 100));

        var pass = FilePassportBuilder.Build(MakeSession(file), "поток.txt", text, fallbackSegments: 4);

        Assert.Equal(4, pass.Chapters.Count);
        Assert.Equal("Часть 1/4", pass.Chapters[0].Label);
        Assert.Equal("Часть 4/4", pass.Chapters[3].Label);
        Assert.Equal(1, pass.Chapters[0].Mentions);
        Assert.All(pass.Chapters, c => Assert.Equal(200, c.Chars));
    }

    [Fact]
    public void Without_text_distributes_mentions_by_offsets()
    {
        var file = File("битый.txt", 600,
            new Mention(1, "Наг", "Наг", 1, "контекст", "", false, 1, 450));

        var pass = FilePassportBuilder.Build(MakeSession(file), "битый.txt", fallbackSegments: 3);

        Assert.False(pass.TextAvailable);
        Assert.False(pass.TextAlignsWithAnalysis);
        Assert.Equal(3, pass.Chapters.Count);
        Assert.Equal("Часть 3/3", pass.Chapters[2].Label);
        Assert.Equal(1, pass.Chapters[2].Mentions);
        Assert.Contains("недоступен", pass.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Neighbors_are_sorted_by_shared_entities()
    {
        var target = File("а.txt", 500,
            new Mention(1, "Ктулху", "Ктулху", 1, "контекст", "", false, 1, 10),
            new Mention(2, "Наг и Йеб", "Наг", 2, "контекст", "", false, 1, 20));
        var rich = File("б.txt", 500,
            new Mention(1, "Ктулху", "Ктулху", 1, "контекст", "", false, 1, 10),
            new Mention(2, "Наг и Йеб", "Йеб", 2, "контекст", "", false, 1, 20),
            new Mention(3, "Йог-Сотот", "Йог-Сотот", 3, "контекст", "", false, 1, 30));
        var poor = File("в.txt", 500,
            new Mention(1, "Ктулху", "Ктулху", 1, "контекст", "", false, 1, 10));
        var unrelated = File("г.txt", 500,
            new Mention(1, "Древние", "Древние", 1, "контекст", "", false, 1, 10));

        var pass = FilePassportBuilder.Build(MakeSession(target, rich, poor, unrelated), "а.txt");

        Assert.Equal(2, pass.Neighbors.Count);
        Assert.Equal("б.txt", pass.Neighbors[0].FileName);
        Assert.Equal(2, pass.Neighbors[0].SharedEntities);
        Assert.Equal(["Ктулху", "Наг и Йеб"], pass.Neighbors[0].Entities);
        Assert.Equal("в.txt", pass.Neighbors[1].FileName);
        Assert.Equal(1, pass.Neighbors[1].SharedEntities);
    }

    [Fact]
    public void Mismatched_text_is_flagged()
    {
        var text = new string('а', 400);
        var file = File("изменённый.txt", 900,
            new Mention(1, "Наг", "Наг", 1, "контекст", "", false, 1, 10));

        var pass = FilePassportBuilder.Build(MakeSession(file), "изменённый.txt", text);

        Assert.True(pass.TextAvailable);
        Assert.False(pass.TextAlignsWithAnalysis);
        Assert.Contains("изменился", pass.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_file_returns_not_found_passport()
    {
        var file = File("есть.txt", 100, new Mention(1, "Наг", "Наг", 1, "к", "", false, 1, 1));
        var pass = FilePassportBuilder.Build(MakeSession(file), "нет.txt");

        Assert.False(pass.Found);
        Assert.Empty(pass.Entities);
        Assert.Empty(pass.Chapters);
        Assert.NotEmpty(pass.Note);
    }

    [Fact]
    public void Markdown_contains_all_sections()
    {
        var mentions = new List<Mention>
        {
            new(1, "Наг и Йеб", "Наг", 1, "жрец у алтаря", "Мифологическое описание", false, 1, 10),
        };
        var file = new FileResult("статья.txt", @"C:\c\статья.txt", mentions, 1, 40, 120, 83.3,
            SemanticMatches: [new SemanticMatch("p1", "Сон", @"C:\c\статья.txt", 1, "спит", 5)]);
        var society = MakeSession(file, File("друг.txt", 100,
            new Mention(1, "Наг и Йеб", "Наг", 1, "к", "", false, 1, 5)));
        var pass = FilePassportBuilder.Build(society, @"C:\c\статья.txt", new string('а', 120));

        var md = FilePassportMarkdown.Build(pass);

        Assert.Contains("# Паспорт файла: статья.txt", md, StringComparison.Ordinal);
        Assert.Contains("## Сущности", md, StringComparison.Ordinal);
        Assert.Contains("## Паттерны", md, StringComparison.Ordinal);
        Assert.Contains("## Темы", md, StringComparison.Ordinal);
        Assert.Contains("## Плотность по главам", md, StringComparison.Ordinal);
        Assert.Contains("## Связи с другими файлами", md, StringComparison.Ordinal);
        Assert.Contains("Теги: Мифологическое описание", md, StringComparison.Ordinal);
        Assert.Contains("Наг и Йеб", md, StringComparison.Ordinal);
        Assert.Contains("друг.txt", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_for_missing_file_contains_note()
    {
        var pass = FilePassportBuilder.Build(null, "нет.txt");
        var md = FilePassportMarkdown.Build(pass);

        Assert.Contains("не найден", md, StringComparison.Ordinal);
    }
}
