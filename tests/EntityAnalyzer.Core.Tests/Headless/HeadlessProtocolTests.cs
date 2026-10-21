using System.Text.Json;
using EntityAnalyzer.Core.Headless;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Headless;

// ═══════════════ R1-12 «Headless API-хост поверх Core» ═══════════════
// Протокол удалённого анализа: разбор JSON-запроса и сборка JSON-ответа.
// Транспорт (HTTP) живёт в CLI; ядро знает только модели и правила
// валидации — они здесь и протестированы.

public class HeadlessProtocolTests
{
    [Fact]
    public void Parses_full_request()
    {
        var json = """
            {"corpus":"C:/corpus","out":"C:/out","preset":"light",
             "extraDirs":["C:/extra"],"excludes":["шум.*"],
             "maxMentions":3,"patternsOnly":true,"formats":["json","md"]}
            """;
        var r = HeadlessProtocol.ParseRequest(json);

        Assert.True(r.IsOk);
        var req = r.Request!;
        Assert.Equal("C:/corpus", req.Corpus);
        Assert.Equal("C:/out", req.Out);
        Assert.Equal("light", req.Preset);
        Assert.Single(req.ExtraDirs, "C:/extra");
        Assert.Single(req.Excludes, "шум.*");
        Assert.Equal(3, req.MaxMentions);
        Assert.True(req.PatternsOnly);
        Assert.Equal(["json", "md"], req.Formats);
    }

    [Fact]
    public void Minimal_request_gets_defaults()
    {
        var r = HeadlessProtocol.ParseRequest("""{"corpus":"D:/texts"}""");
        Assert.True(r.IsOk);
        var req = r.Request!;
        Assert.Null(req.Out);              // движок возьмёт <corpus>/_out? — нет: сервер решает
        Assert.Equal("extended", req.Preset);
        Assert.Empty(req.ExtraDirs);
        Assert.Empty(req.Excludes);
        Assert.Null(req.MaxMentions);      // действует пресетный лимит
        Assert.False(req.PatternsOnly);
        Assert.Equal(["txt", "json", "csv", "html", "md"], req.Formats);
    }

    [Fact]
    public void Missing_corpus_is_rejected_with_message()
    {
        var r = HeadlessProtocol.ParseRequest("""{"preset":"light"}""");
        Assert.False(r.IsOk);
        Assert.Contains("corpus", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Broken_json_is_rejected_with_message()
    {
        var r = HeadlessProtocol.ParseRequest("{не-json");
        Assert.False(r.IsOk);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Theory]
    [InlineData(-5)]
    public void Negative_max_mentions_is_rejected(int bad)
    {
        var r = HeadlessProtocol.ParseRequest($$"""{"corpus":"c","maxMentions":{{bad}}}""");
        Assert.False(r.IsOk);
        Assert.Contains("maxMentions", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Error_response_serializes_ok_false()
    {
        var json = HeadlessProtocol.ToJson(HeadlessResponse.Fail("корпус не найден"));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("корпус не найден", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Success_response_carries_summary_and_reports_dir()
    {
        var resp = new HeadlessResponse(
            Ok: true,
            TotalMentions: 7, FilesTotal: 5, FilesWithMentions: 2,
            DurationMs: 1234.5, ReportsDir: @"C:\out",
            EntityCounts: new Dictionary<string, int> { ["Наг и Йеб"] = 7 },
            Funnel: new HeadlessFunnel(RawMatches: 10, FilterRejected: 2, OverlapsResolved: 4,
                DisplayCapped: 0, Deduplicated: 2, DuplicatesGrouped: 0, FinalMentions: 7),
            Error: null);

        var json = HeadlessProtocol.ToJson(resp);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(7, doc.RootElement.GetProperty("totalMentions").GetInt32());
        Assert.Equal(@"C:\out", doc.RootElement.GetProperty("reportsDir").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("funnel").GetProperty("rawMatches").GetInt32());
    }
}
