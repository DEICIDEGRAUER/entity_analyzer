using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-29 «Маркетплейс шаблонов отчётов»: декларативные JSON-шаблоны
/// выгрузки (секции/колонки) — безопасность гарантируется отсутствием
/// исполняемого кода: шаблон либо валиден, либо отвергнут с причиной.
/// </summary>
public class ReportTemplateTests
{
    private const string ValidJson = """
        {
          "name": "Хронология событий",
          "sections": [
            { "type": "summary" },
            { "type": "entities", "columns": ["entity", "mentions", "files"], "top": 20 }
          ]
        }
        """;

    [Fact]
    public void Valid_template_parses()
    {
        var template = ReportTemplate.Parse(ValidJson);
        Assert.True(template.IsValid);
        Assert.Equal("Хронология событий", template.Name);
        Assert.Equal(2, template.Sections.Count);
        Assert.Null(template.Error);
    }

    [Fact]
    public void Broken_json_is_rejected_with_reason()
    {
        var template = ReportTemplate.Parse("{ name: ");
        Assert.False(template.IsValid);
        Assert.NotNull(template.Error);
    }

    [Fact]
    public void Unknown_section_type_is_rejected()
    {
        var json = """{"name":"x","sections":[{"type":"exec"}]}""";
        var template = ReportTemplate.Parse(json);
        Assert.False(template.IsValid);
        Assert.Contains("exec", template.Error);
    }

    [Fact]
    public void Unknown_column_is_rejected()
    {
        var json = """{"name":"x","sections":[{"type":"entities","columns":["entity","drop table"]}]}""";
        var template = ReportTemplate.Parse(json);
        Assert.False(template.IsValid);
    }

    [Fact]
    public void Empty_sections_are_rejected()
    {
        var template = ReportTemplate.Parse("""{"name":"x","sections":[]}""");
        Assert.False(template.IsValid);
    }

    [Fact]
    public void Render_produces_markdown_with_requested_sections()
    {
        var template = ReportTemplate.Parse(ValidJson);
        var session = MakeSession();
        var md = ReportTemplate.Render(template, session);
        Assert.Contains("Хронология событий", md);
        Assert.Contains("Ктулху", md); // таблица сущностей
    }

    private static AnalysisSession MakeSession()
    {
        var f1 = new FileResult("a.txt", "a.txt",
            [new Mention(1, "Ктулху", "Ктулху", 3, "Спит Ктулху в Р'льехе.")],
            1, 40, 5000, 4.0);
        return new AnalysisSession([f1],
            new AnalysisSummary(1, 1, 1, 5000, new Dictionary<string, int> { ["Ктулху"] = 1 }),
            new QualityReport(2, 0, 0, 0, 0, 0, 1, 0, 0, 0, [], "0s"),
            "light", DateTimeOffset.Now, TimeSpan.Zero);
    }

    [Fact]
    public void Built_in_chronology_template_is_valid()
    {
        var template = ReportTemplate.Parse(BuiltInTemplates.Chronology);
        Assert.True(template.IsValid, template.Error ?? "");
    }

    [Fact]
    public void Built_in_frequency_dictionary_template_is_valid()
    {
        var template = ReportTemplate.Parse(BuiltInTemplates.FrequencyDictionary);
        Assert.True(template.IsValid, template.Error ?? "");
    }
}
