using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.IO;
using EntityAnalyzer.Core.Text;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Регрессионные тесты по QA-отчёту 25.08.2026:
/// BUG-001 (имя карточки в вариантах), BUG-002 (самоподхват отчётов),
/// BUG-007 (BOM в цитатах), BUG-008 (UTF-16), прозрачность воронки.
/// </summary>
[Collection("LogSequential")]
public class QaRegressionTests : IDisposable
{
    private readonly string _dir;

    public QaRegressionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ea_qa_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    // ─── BUG-001: имя карточки ищется фразой и в падежах ───

    [Fact]
    public void CodexAdapter_IncludesCardNameAndCanonInVariants()
    {
        var doc = new CodexDocument();
        doc.Cards.Add(new Card
        {
            Name = "Наг и Йеб",
            Canonical = "Nug and Yeb",
            Enabled = true,
            Aliases = ["Великий Наг"],
        });

        var def = CodexSearchAdapter.ToDefinitions(doc).Single();

        Assert.Contains("Наг и Йеб", def.Irregular);
        Assert.Contains("Nug and Yeb", def.Irregular);
        Assert.Contains("Великий Наг", def.Irregular);
    }

    [Fact]
    public void Catalog_GeneratesMorphologyForRussianCardName()
    {
        var doc = new CodexDocument();
        doc.Cards.Add(new Card
        {
            Name = "Наг и Йеб",
            Canonical = "Nug and Yeb",
            Enabled = true,
            Aliases = [],
        });

        var definitions = CodexSearchAdapter.ToDefinitions(doc).ToList();
        var catalog = new EntityCatalog(definitions, generateVariants: true);

        var variants = catalog.VariantsByEntity["Наг и Йеб"];

        // Точная фраза присутствует…
        Assert.Contains("Наг и Йеб", variants);
        // …и есть порождённые формы имени (морфология/переносы), а не только канон.
        Assert.Contains(variants, v => v.StartsWith("Наг", StringComparison.Ordinal) && v != "Наг и Йеб");
    }

    // ─── BUG-002: выходная папка внутри корпуса не сканируется ───

    private static AnalysisSession RunEngine(string corpus, string output)
    {
        var options = new AnalyzerOptions
        {
            Corpus = { Dir = corpus },
            Output = { Dir = output },
            Cache = { Dir = Path.Combine(output, ".cache") },        // Изоляция от машинного состояния: общий относительный ".cache"
        // делится между прогонами тестов (вскрыто R1-11).
        };
        Presets.Apply(Presets.Resolve("light", options), options.Processing);
        var codex = DefaultCodex.Load();
        var catalog = new EntityCatalog(CodexSearchAdapter.ToDefinitions(codex).ToList(), generateVariants: true);
        return new AnalysisEngine(options, catalog, "light").Run(CancellationToken.None);
    }

    [Fact]
    public void OutputInsideCorpus_IsExcluded_AndRunsAreIdempotent()
    {
        Write("a.txt", "Жрецы проводили древний Наг и его ночной обряд у моря.");
        Write("b.txt", "Йеб спал в глубинах океана.");

        var output = Path.Combine(_dir, "_out_inside");

        var first = RunEngine(_dir, output);
        var second = RunEngine(_dir, output);

        // Ни один файл результата не лежит в выходной папке.
        Assert.All(first.Files, f => Assert.False(
            f.FullPath.StartsWith(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)));

        // Повторный прогон не «находит» отчёты первого — цифры идентичны.
        Assert.Equal(first.Files.Count, second.Files.Count);
        Assert.Equal(first.Summary.TotalMentions, second.Summary.TotalMentions);
        Assert.Equal(first.SemanticMatches.Count, second.SemanticMatches.Count);
    }

    // ─── BUG-007: BOM не попадает в цитаты ───

    [Fact]
    public void Normalizer_StripsLeadingBom()
    {
        var normalized = TextNormalizer.Normalize("\uFEFFНаг и Йеб после BOM.");
        Assert.StartsWith("Наг", normalized);
        Assert.DoesNotContain('\uFEFF', normalized);
    }

    // ─── BUG-008: UTF-16 декодируется по BOM ───

    [Fact]
    public void DocumentReader_DecodesUtf16ByBom()
    {
        var text = "Йеб спит в Р'лиехе.";

        var leBytes = new List<byte> { 0xFF, 0xFE };
        leBytes.AddRange(System.Text.Encoding.Unicode.GetBytes(text));
        var decodedLe = DocumentReader.Extract("f.txt", [.. leBytes]);
        Assert.Contains("Йеб", decodedLe);

        var beBytes = new List<byte> { 0xFE, 0xFF };
        beBytes.AddRange(System.Text.Encoding.BigEndianUnicode.GetBytes(text));
        var decodedBe = DocumentReader.Extract("f.txt", [.. beBytes]);
        Assert.Contains("Йеб", decodedBe);
    }

    // ─── Прозрачность воронки: лимит отображения учитывается отдельно ───

    [Fact]
    public void QualityReport_SeparatesFiltersFromDisplayCap()
    {
        var q = new QualityReport(
            100, 3, 2, 1, 0, 0, 10, 0, 0, 0,
            [], "1s",
            DisplayCapped: 84);

        // Фильтры: только реальные отклонения (3+2+1), без лимита показа.
        Assert.Equal(6, q.TotalFilterRejected);
        Assert.Equal(84, q.DisplayCapped);
    }

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content, new System.Text.UTF8Encoding(false));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
