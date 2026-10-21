using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Exporting;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Exporting;

/// <summary>
/// Группа 3 (item 50): полное приведение энциклопедической статьи о сущности
/// в отчёте — карточка кодекса + аналитика прогона.
/// </summary>
public class EntityArticleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-article-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

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

    private static AnalysisSession Session() => SessionOf(
        new FileResult("a.txt", "corpus/a.txt",
        [
            new Mention(1, "Наг", "Наг", 12, "Здесь Наг явился во сне.", Theme: "Мифологическое описание"),
            new Mention(2, "Наг", "Нага", 40, "Нага пришла к жрецу.", Theme: "Ритуал"),
            new Mention(3, "Наг", "Наг", 44, "Наг и Ктулху говорили."),
        ], TotalFound: 3, ContextSize: 60, CharCount: 2000, DensityPer10k: 25.0),
        new FileResult("b.txt", "corpus/b.txt",
        [
            new Mention(1, "Наг", "Наг", 5, "Наг спит в озере.", Theme: "Мифологическое описание"),
        ], TotalFound: 1, ContextSize: 60, CharCount: 1000, DensityPer10k: 10.0));

    private static CodexDocument Doc()
    {
        var doc = CodexDocument.CreateEmpty(); // встроенные типы/связи уже посеяны

        var type = doc.Types[0]; // «Божество»
        type.Fields.Add(new FieldSchema { Id = "sphere", Name = "Сфера", Order = 1 });
        type.Fields.Add(new FieldSchema { Id = "danger", Name = "Опасность", Order = 2 });

        var nag = Card.Create("deity", "Наг");
        nag.Description = "Древний змей снов.";
        nag.Aliases.Add("Нуг");
        nag.Group = "Великие Древние";
        nag.Tags.Add("сон");
        nag.Fields["sphere"] = FieldValue.FromText("сны");
        nag.Fields["danger"] = FieldValue.FromChoice("высокая");

        var cthulhu = Card.Create("deity", "Ктулху");
        var shy = Card.Create("deity", "Шуб-Ниггурат");
        doc.Cards.Add(nag);
        doc.Cards.Add(cthulhu);
        doc.Cards.Add(shy);

        doc.Links.Add(new CardLink(nag.Id, cthulhu.Id, "enemy", "извечная вражда"));
        doc.Links.Add(new CardLink(shy.Id, nag.Id, "serves"));
        return doc;
    }

    [Fact]
    public void Article_renders_card_header_description_and_fields()
    {
        var text = EntityArticle.Build("Наг", Session(), Doc());

        Assert.StartsWith("# Наг", text);
        Assert.Contains("**Категория:** Божество", text);
        Assert.Contains("**Группа:** Великие Древние", text);
        Assert.Contains("**Теги:** сон", text);
        Assert.Contains("*Также: Нуг*", text);
        Assert.Contains("## Описание", text);
        Assert.Contains("Древний змей снов.", text);
        Assert.Contains("| Сфера | сны |", text);
        Assert.Contains("| Опасность | высокая |", text);
        Assert.DoesNotContain("**Канон:**", text);
    }

    [Fact]
    public void Article_renders_typed_links_forward_and_reverse()
    {
        var text = EntityArticle.Build("Наг", Session(), Doc());

        Assert.Contains("## Связи", text);
        Assert.Contains("- → Ктулху (враждует с) — извечная вражда", text);
        Assert.Contains("- ← Шуб-Ниггурат (повелевает, обратная)", text);
    }

    [Fact]
    public void Article_aggregates_mentions_files_and_themes()
    {
        var text = EntityArticle.Build("Наг", Session(), Doc());

        Assert.Contains("- Упоминаний: 4", text);
        Assert.Contains("- Файлов: 2", text);
        Assert.Contains("| Мифологическое описание | 2 |", text);
        Assert.Contains("| Ритуал | 1 |", text);
        Assert.Contains("| a.txt | 3 | 12, 40, 44 |", text);
        Assert.Contains("| b.txt | 1 | 5 |", text);
    }

    [Fact]
    public void Article_quotes_are_grouped_by_file_and_capped_per_file()
    {
        var text = EntityArticle.Build("Наг", Session(), Doc());
        Assert.Contains("## Цитаты", text);
        Assert.Contains("### a.txt", text);
        Assert.Contains("> [1] Наг · строка 12 · Мифологическое описание", text);
        Assert.Contains("> Здесь Наг явился во сне.", text);
        Assert.Contains("### b.txt", text);
        Assert.Equal(4, text.Split('\n').Count(l => l.StartsWith("> [", StringComparison.Ordinal)));

        // Пять упоминаний в одном файле → в цитаты попадают только первые три.
        var many = SessionOf(
            new FileResult("c.txt", "corpus/c.txt",
            [
                new Mention(1, "Наг", "Наг", 1, "раз"),
                new Mention(2, "Наг", "Наг", 2, "два"),
                new Mention(3, "Наг", "Наг", 3, "три"),
                new Mention(4, "Наг", "Наг", 4, "четыре"),
                new Mention(5, "Наг", "Наг", 5, "пять"),
            ], TotalFound: 5, ContextSize: 60, CharCount: 100, DensityPer10k: 5.0));
        var manyText = EntityArticle.Build("Наг", many, Doc());
        Assert.Equal(3, manyText.Split('\n').Count(l => l.StartsWith("> [", StringComparison.Ordinal)));
        Assert.Contains("| c.txt | 5 | 1, 2, 3, 4, 5 |", manyText);
    }

    [Fact]
    public void Article_without_card_renders_presence_only()
    {
        var session = SessionOf(
            new FileResult("c.txt", "corpus/c.txt",
            [
                new Mention(1, "Йог-Сотот", "Йог-Сотот", 7, "Йог-Сотот спит между мирами."),
            ], TotalFound: 1, ContextSize: 60, CharCount: 100, DensityPer10k: 100.0));

        var text = EntityArticle.Build("Йог-Сотот", session, Doc());

        Assert.StartsWith("# Йог-Сотот", text);
        Assert.Contains("- Упоминаний: 1", text);
        Assert.DoesNotContain("## Описание", text);
        Assert.DoesNotContain("## Поля", text);
        Assert.DoesNotContain("## Связи", text);
    }

    [Fact]
    public void Article_counts_mentions_under_aliases()
    {
        var session = SessionOf(
            new FileResult("d.txt", "corpus/d.txt",
            [
                new Mention(1, "Нуг", "Нуг", 3, "Нуг шептал во тьме."),
            ], TotalFound: 1, ContextSize: 60, CharCount: 100, DensityPer10k: 100.0));

        var text = EntityArticle.Build("Наг", session, Doc());

        Assert.Contains("- Упоминаний: 1", text);
        Assert.Contains("> [1] Нуг · строка 3", text);
    }

    [Fact]
    public void Article_for_card_without_mentions_says_not_found()
    {
        var text = EntityArticle.Build("Ктулху", Session(), Doc());

        Assert.StartsWith("# Ктулху", text);
        Assert.Contains("- Упоминаний: 0", text);
        Assert.Contains("не найдено", text);
        Assert.DoesNotContain("## Цитаты", text);
    }

    [Fact]
    public void WriteAll_creates_articles_with_index()
    {
        var written = EntityArticle.WriteAll(Session(), Doc(), _dir);

        Assert.Equal(3, written.Count);
        Assert.Contains(Path.Combine(_dir, "Наг.md"), written);
        Assert.Contains(Path.Combine(_dir, "Ктулху.md"), written);
        Assert.True(File.Exists(Path.Combine(_dir, "index.md")));

        var index = File.ReadAllText(Path.Combine(_dir, "index.md"));
        Assert.Contains("Сущностей: 3 · файлов корпуса: 2", index);
        Assert.Contains("- [Наг](Наг.md) — упоминаний: 4", index);

        var article = File.ReadAllText(Path.Combine(_dir, "Наг.md"));
        Assert.Contains("## Описание", article);
    }
}
