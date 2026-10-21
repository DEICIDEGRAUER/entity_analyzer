using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Протокол проверки (review_report.md/.csv): аддитивность обычных отчётов,
/// состав решений и машинные коды вердиктов.
/// </summary>
public class ReviewReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-review-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AnalysisSession Session(Func<Mention, Mention>? customizeSecond = null)
    {
        var second = new Mention(1, "Наг и Йеб", "Нуг и Йеб", 1, "Нуг и Йеб спали.", Offset: 7);
        if (customizeSecond is not null) second = customizeSecond(second);
        return new AnalysisSession(
            Files:
            [
                new FileResult("a.txt", "corpus/a.txt",
                [
                    new Mention(1, "Наг и Йеб", "Наг и Йеб", 2, "Здесь Наг и Йеб пришли.", Offset: 14),
                    new Mention(2, "Наг и Йеб", "Йеб", 5, "И снова Йеб остался.", Offset: 40)
                ], TotalFound: 5, ContextSize: 60, CharCount: 2000, DensityPer10k: 25.0),
                new FileResult("b.txt", "corpus/b.txt", [second],
                    TotalFound: 1, ContextSize: 60, CharCount: 800, DensityPer10k: 12.5)
            ],
            Summary: new AnalysisSummary(3, 2, 1, 2800,
                new Dictionary<string, int> { ["Наг и Йеб"] = 3 }),
            Quality: new QualityReport(10, 2, 1, 0, 0, 0, 3, 0, 1, 2,
                Array.Empty<(string, string)>(), "1.0s"),
            PresetName: "extended");
    }

    [Fact]
    public void Without_verdicts_nothing_is_written()
    {
        var path = ReportExporter.WriteReviewReport(Session(), _dir);
        Assert.Equal("", path);
        Assert.False(File.Exists(Path.Combine(_dir, "review_report.md")));
        Assert.False(File.Exists(Path.Combine(_dir, "review_report.csv")));
    }

    [Fact]
    public void Markdown_lists_decisions_and_summary_only()
    {
        var session = Session();
        session.Files[0].Mentions[0].Verdict = VerdictKind.Confirmed; // a.txt, строка 2, офсет 14
        session.Files[0].Mentions[1].Verdict = VerdictKind.Rejected;  // a.txt, строка 5, офсет 40
        // b.txt остаётся непроверенным.

        var path = ReportExporter.WriteReviewReport(session, _dir);
        Assert.Equal(Path.Combine(_dir, "review_report.md"), path);
        Assert.True(File.Exists(path));

        var md = File.ReadAllText(path);
        Assert.Contains("- **Всего цитат:** 3", md);
        Assert.Contains("- **Проверено:** 2", md);
        Assert.Contains("- **Подтверждено:** 1", md);
        Assert.Contains("- **Ложные срабатывания:** 1", md);
        Assert.Contains("- **Спорно:** 0", md);
        Assert.Contains("- **Осталось непроверенных:** 1", md);
        Assert.Contains("- **Файлов с непроверенными цитатами:** 1", md);

        // Только решённые цитаты, порядок сессии.
        Assert.Contains("| a.txt | 2 | Наг и Йеб | Наг и Йеб | подтверждено | 14 |", md);
        Assert.Contains("| a.txt | 5 | Наг и Йеб | Йеб | ложное срабатывание | 40 |", md);
        // Непроверенный файл/цитата в таблицу решений не попадают.
        Assert.DoesNotContain("| b.txt", md);
    }

    [Fact]
    public void Markdown_escapes_pipes_and_backslashes()
    {
        var session = new AnalysisSession(
            Files:
            [
                new FileResult("odd|.txt", "corpus/odd.txt",
                    [new Mention(1, "Сущ\\ность", "Ва|риант", 1, "ctx")],
                    TotalFound: 1, ContextSize: 60, CharCount: 10, DensityPer10k: 10)
            ],
            Summary: new AnalysisSummary(1, 1, 1, 10, new Dictionary<string, int>()),
            Quality: new QualityReport(1, 1, 1, 0, 0, 0, 1, 0, 1, 0,
                Array.Empty<(string, string)>(), "0.1s"),
            PresetName: "extended");
        session.Files[0].Mentions[0].Verdict = VerdictKind.Uncertain;

        ReportExporter.WriteReviewReport(session, _dir);
        var md = File.ReadAllText(Path.Combine(_dir, "review_report.md"));
        Assert.Contains("| odd\\|.txt | 1 | Сущ\\\\ность | Ва\\|риант |", md);
    }

    [Fact]
    public void Csv_uses_stable_verdict_codes_and_escapes_quotes()
    {
        var session = Session(second => second with
        {
            Entity = "Сущность; с кавычкой \"внутри\"",
            Verdict = VerdictKind.Confirmed
        });
        session.Files[0].Mentions[0].Verdict = VerdictKind.Rejected;
        // цитата «И снова Йеб остался.» остаётся непроверенной

        ReportExporter.WriteReviewReport(session, _dir);
        var csv = File.ReadAllText(Path.Combine(_dir, "review_report.csv"));
        Assert.StartsWith("file;line;offset;entity;variant;verdict", csv);
        // Стабильные коды вердиктов — как в verdicts.jsonl; все поля в кавычках (RFC 4180).
        Assert.Contains("\"a.txt\";2;14;\"Наг и Йеб\";\"Наг и Йеб\";\"rejected\"", csv);
        // Кавычки удваиваются, как в RFC 4180.
        Assert.Contains("\"Сущность; с кавычкой \"\"внутри\"\"\"", csv);
        // Непроверенная цитата в CSV не попадает.
        Assert.DoesNotContain("Йеб;rejected", csv);
        Assert.Equal(3, csv.CountLines());
    }

    [Fact]
    public void Export_all_adds_review_files_and_keeps_existing_outputs()
    {
        var outDir = Path.Combine(_dir, "out");

        // Первый прогон: без вердиктов — никаких файлов протокола.
        ReportExporter.ExportAll(Session(), outDir,
            new HashSet<string>(["txt", "csv"], StringComparer.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(outDir, "review_report.md")));
        Assert.False(File.Exists(Path.Combine(outDir, "review_report.csv")));
        var reference = File.ReadAllBytes(Path.Combine(outDir, "entities_table.csv"));

        // Второй прогон: с вердиктами — протокол добавляется, прежние отчёты остаются.
        var session = Session();
        session.Files[0].Mentions[0].Verdict = VerdictKind.Confirmed;
        ReportExporter.ExportAll(session, outDir,
            new HashSet<string>(["txt", "csv"], StringComparer.OrdinalIgnoreCase));

        Assert.True(File.Exists(Path.Combine(outDir, "review_report.md")));
        Assert.True(File.Exists(Path.Combine(outDir, "review_report.csv")));
        Assert.Equal(reference, File.ReadAllBytes(Path.Combine(outDir, "entities_table.csv")));
    }
}

file static class CsvLineExtensions
{
    /// <summary>Число строк в CSV: заголовок + по одной на каждую запись.</summary>
    public static int CountLines(this string text) =>
        text.Split('\n').Count(line => line.TrimEnd('\r').Length > 0);
}
