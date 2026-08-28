using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Exporting;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R1-20: Санитайзер HTML-отчётов (CSP + экранирование) ═══════════════
// Корпус — недоверенный ввод: имена файлов, варианты и контексты попадают в
// self-contained HTML-отчёты. Контракт: (а) каждый отчёт объявляет Content-Security-Policy,
// (б) разметка из корпуса никогда не попадает в HTML как разметка.

public class HtmlSecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-htmlsec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Сессия с враждебным контентом из корпуса во всех текстовых полях.</summary>
    private static AnalysisSession HostileSession() => new(
        Files:
        [
            new FileResult("<script>alert('file')</script>.txt", "corpus/x.txt",
            [
                new Mention(1, "<img src=x onerror=alert(2)>", "<b>Наг</b>", 3,
                    "Жрец читал: <script>alert('ctx')</script> перед алтарём <img src=x onerror=alert(4)>.",
                    Theme: "<i>Ритуал</i>")
            ], TotalFound: 1, ContextSize: 60, CharCount: 500, DensityPer10k: 20.0)
        ],
        Summary: new AnalysisSummary(1, 1, 1, 500,
            new Dictionary<string, int> { ["<img src=x onerror=alert(2)>"] = 1 }),
        Quality: new QualityReport(5, 1, 1, 0, 0, 0, 1, 0, 0, 1, Array.Empty<(string, string)>(), "0.1s"),
        PresetName: "extended");

    [Fact]
    public void Html_report_declares_static_csp()
    {
        var html = File.ReadAllText(ReportExporter.WriteHtml(HostileSession(), _dir));
        Assert.Contains("http-equiv=\"Content-Security-Policy\"", html);
        Assert.Contains("default-src 'none'", html);
        // Отчёт без скриптов: скрипты запрещены полностью.
        Assert.DoesNotContain("script-src", html);
    }

    [Fact]
    public void Html_report_neutralizes_markup_from_corpus()
    {
        var html = File.ReadAllText(ReportExporter.WriteHtml(HostileSession(), _dir));
        // Разметка корпуса обязана прийти экранированной…
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(4)&gt;", html);
        // …и никогда — живой разметкой. Проверяем сырые полезные нагрузки из корпуса:
        // собственная разметка отчёта (<b>, <div>…) легитимна и не должна учитываться.
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img src=x onerror", html);
    }

    [Fact]
    public void Graph_and_cloud_declare_scripted_csp()
    {
        var graphPath = ReportExporter.WriteCoOccurrenceGraph(HostileSession(), _dir);
        Assert.True(graphPath.Length > 0, "граф должен быть записан для непустой сессии");
        var graph = File.ReadAllText(graphPath);
        Assert.Contains("http-equiv=\"Content-Security-Policy\"", graph);
        Assert.Contains("script-src 'unsafe-inline'", graph);
        Assert.Contains("default-src 'none'", graph);

        var cloudPath = ReportExporter.WriteVariantCloud(HostileSession(), _dir);
        Assert.True(cloudPath.Length > 0);
        var cloud = File.ReadAllText(cloudPath);
        Assert.Contains("http-equiv=\"Content-Security-Policy\"", cloud);
        Assert.Contains("script-src 'unsafe-inline'", cloud);
    }

    [Fact]
    public void Html_atlas_declares_scripted_csp()
    {
        var doc = new EntityAnalyzer.Core.Codex.CodexDocument();
        var html = CodexExporters.BuildHtmlAtlas(doc);
        Assert.Contains("http-equiv=\"Content-Security-Policy\"", html);
        Assert.Contains("script-src 'unsafe-inline'", html);
        Assert.Contains("default-src 'none'", html);
    }

    [Fact]
    public void Escape_covers_quotes_as_well_as_angle_brackets()
    {
        // Кавычки экранируются — защита на случай будущих атрибутных вставок.
        Assert.Equal("&lt;a href=&quot;x&quot;&gt;&#39;&amp;",
            ReportExporter.Escape("<a href=\"x\">'&"));
    }
}
