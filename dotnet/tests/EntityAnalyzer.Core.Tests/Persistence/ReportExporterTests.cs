using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

public class ReportExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-export-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AnalysisSession Session() => new(
        Files:
        [
            new FileResult("a.txt", "corpus/a.txt",
            [
                new Mention(1, "Наг и Йеб", "Наг и Йеб", 2, "Здесь Наг и Йеб пришли.", Theme: "Мифологическое описание"),
                new Mention(2, "Наг и Йеб", "Йеб", 5, "И снова Йеб остался.", Theme: "")
            ], TotalFound: 5, ContextSize: 60, CharCount: 2000, DensityPer10k: 25.0),
            new FileResult("b.txt", "corpus/b.txt",
                [new Mention(1, "Наг и Йеб", "Нуг и Йеб", 1, "Нуг и Йеб спали.", Theme: "Ритуал и призывание")],
                TotalFound: 1, ContextSize: 60, CharCount: 800, DensityPer10k: 12.5)
        ],
        Summary: new AnalysisSummary(3, 2, 1, 2800,
            new Dictionary<string, int> { ["Наг и Йеб"] = 3 }),
        Quality: new QualityReport(10, 2, 1, 0, 0, 0, 3, 0, 1, 2, Array.Empty<(string, string)>(), "1.0s"),
        PresetName: "extended");

    [Fact]
    public void Txt_report_written_with_expected_sections()
    {
        var path = ReportExporter.WriteTxt(Session(), _dir);
        Assert.True(File.Exists(path));

        var text = File.ReadAllText(path);
        Assert.Contains("ENTITIES MENTIONS ANALYSIS", text);
        Assert.Contains("a.txt", text);
        Assert.Contains("Наг и Йеб: 3", text);
        Assert.Contains("[1] Наг и Йеб (Наг и Йеб) L2", text);
    }

    [Fact]
    public void Json_report_is_parseable()
    {
        var path = ReportExporter.WriteJson(Session(), _dir);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(3, doc.RootElement.GetProperty("stats").GetProperty("total").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void Csv_report_escapes_and_quotes()
    {
        var path = ReportExporter.WriteCsv(Session(), _dir);
        var csv = File.ReadAllText(path);
        Assert.StartsWith("file;num;entity;variant;line;density_per_10k;theme;context", csv);
        Assert.Contains("\"Здесь Наг и Йеб пришли.\"", csv);
    }

    [Fact]
    public void Html_report_highlights_mentions()
    {
        var path = ReportExporter.WriteHtml(Session(), _dir);
        var html = File.ReadAllText(path);
        Assert.Contains("<mark>", html);
        Assert.Contains("a.txt", html);
    }

    [Fact]
    public void Per_entity_folders_grouped_by_theme()
    {
        ReportExporter.WritePerEntityFolders(Session(), Path.Combine(_dir, "by_entity"));
        var entityDir = Path.Combine(_dir, "by_entity", "Наг_и_Йеб");
        Assert.True(Directory.Exists(entityDir));
        Assert.True(File.Exists(Path.Combine(entityDir, "Наг_и_Йеб_mentions.txt")));
        Assert.True(Directory.Exists(Path.Combine(entityDir, "Ритуал_и_призывание")));
    }

    [Fact]
    public void Trust_sidecar_blocks_degraded_source_even_for_canonical()
    {
        var file = new FileResult("a.txt", "corpus/a.txt",
            [new Mention(1, "Наг и Йеб", "Наг и Йеб", 2, "Здесь Наг и Йеб пришли.")],
            TotalFound: 1, ContextSize: 60, CharCount: 2000, DensityPer10k: 5.0)
        {
            SourceQuality = SourceQuality.DegradedLexical,
            SourceReason = SourceQuality.ReasonLexicalSubstitution,
        };
        var session = new AnalysisSession(
            [file],
            new AnalysisSummary(1, 1, 1, 2000,
                new Dictionary<string, int> { ["Наг и Йеб"] = 1 }),
            new QualityReport(1, 1, 0, 0, 0, 0, 1, 0, 0, 0,
                Array.Empty<(string, string)>(), "0.1s"),
            "minimal");
        var doc = new CodexDocument();
        doc.Cards.Add(Card.Create("deity", "Наг и Йеб"));

        var path = Path.Combine(_dir, "entities_trust.csv");
        Directory.CreateDirectory(_dir);
        ReportExporter.WriteTrustCsv(session, _dir, doc);

        var csv = File.ReadAllText(path);
        Assert.StartsWith("file,num,entity,variant,line,trust,alias_class,marker,source_quality,source_reason,trust_reason,exploratory", csv);
        Assert.Contains("degraded_lexical", csv);
        Assert.Contains("review", csv);
        Assert.Contains("blocked by degraded source", csv);
    }

    [Fact]
    public void Trust_sidecar_blocks_unknown_source_even_for_canonical()
    {
        var file = new FileResult("a.txt", "corpus/a.txt",
            [new Mention(1, "Наг и Йеб", "Наг и Йеб", 2, "Здесь Наг и Йеб пришли.")],
            TotalFound: 1, ContextSize: 60, CharCount: 2000, DensityPer10k: 5.0)
        {
            SourceQuality = SourceQuality.Unknown,
            SourceReason = "old_cache",
        };
        var session = new AnalysisSession(
            [file],
            new AnalysisSummary(1, 1, 1, 2000,
                new Dictionary<string, int> { ["Наг и Йеб"] = 1 }),
            new QualityReport(1, 1, 0, 0, 0, 0, 1, 0, 0, 0,
                Array.Empty<(string, string)>(), "0.1s"),
            "minimal");
        var doc = new CodexDocument();
        doc.Cards.Add(Card.Create("deity", "Наг и Йеб"));

        var path = Path.Combine(_dir, "entities_trust.csv");
        Directory.CreateDirectory(_dir);
        ReportExporter.WriteTrustCsv(session, _dir, doc);

        var csv = File.ReadAllText(path);
        Assert.Contains("unknown", csv);
        Assert.Contains("review", csv);
        Assert.Contains("trust blocked by non-usable source (unknown)", csv);
    }

    [Fact]
    public void Trust_sidecar_keeps_usable_canonical_trusted()
    {
        var file = new FileResult("a.txt", "corpus/a.txt",
            [new Mention(1, "Наг и Йеб", "Наг и Йеб", 2, "Здесь Наг и Йеб пришли.")],
            TotalFound: 1, ContextSize: 60, CharCount: 2000, DensityPer10k: 5.0)
        {
            SourceQuality = SourceQuality.Usable,
            SourceReason = SourceQuality.ReasonDetectorNegative,
        };
        var session = new AnalysisSession(
            [file],
            new AnalysisSummary(1, 1, 1, 2000,
                new Dictionary<string, int> { ["Наг и Йеб"] = 1 }),
            new QualityReport(1, 1, 0, 0, 0, 0, 1, 0, 0, 0,
                Array.Empty<(string, string)>(), "0.1s"),
            "minimal");
        var doc = new CodexDocument();
        doc.Cards.Add(Card.Create("deity", "Наг и Йеб"));

        var path = Path.Combine(_dir, "entities_trust.csv");
        Directory.CreateDirectory(_dir);
        ReportExporter.WriteTrustCsv(session, _dir, doc);

        var csv = File.ReadAllText(path);
        Assert.Contains(",trusted,", csv);
        Assert.DoesNotContain("non-usable source", csv);
    }

    [Fact]
    public void ExportAll_writes_trust_sidecar_and_keeps_main_csv_contract()
    {
        var doc = new CodexDocument();
        doc.Cards.Add(Card.Create("deity", "Наг и Йеб"));

        Directory.CreateDirectory(_dir);
        ReportExporter.ExportAll(Session(), _dir, new HashSet<string> { "csv" }, codex: doc);

        var main = File.ReadAllText(Path.Combine(_dir, "entities_table.csv"));
        var trust = File.ReadAllText(Path.Combine(_dir, "entities_trust.csv"));
        Assert.StartsWith("file;num;entity;variant;line;density_per_10k;theme;context", main);
        Assert.StartsWith("file,num,entity,variant,line,trust,alias_class,marker,source_quality,source_reason,trust_reason,exploratory", trust);
    }

    [Fact]
    public void Variants_index_exported_from_catalog()
    {
        var catalog = new EntityCatalog([new EntityDefinition("Наг и Йеб", "Nug and Yeb", ["Йеб"], [])]);
        var dir = ReportExporter.WriteVariantsIndex(catalog, _dir);
        Assert.True(File.Exists(Path.Combine(dir, "Наг_и_Йеб.txt")));
        Assert.Contains("Йеб", File.ReadAllText(Path.Combine(dir, "Наг_и_Йеб.txt")));
    }
}
