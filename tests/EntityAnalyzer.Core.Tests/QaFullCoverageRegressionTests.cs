using System.Diagnostics;
using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Text;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Регрессия кампании полного покрытия 25.08.2026 (qa_full_coverage_report):
/// BUG-N09 (round-trip настроек — уровень App), N10 (квадратичные контексты),
/// N11 (тихий regex), N12 (документированный дедуп) + наблюдение о YAML.
/// </summary>
[Collection("LogSequential")]
public class QaFullCoverageRegressionTests : IDisposable
{
    private readonly string _dir;

    public QaFullCoverageRegressionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ea_qa3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>BUG-N10: файл-«простыня» с 20k совпадений при -m 0 строится за разумное время.</summary>
    [Fact]
    public void N10_LongLine_UnlimitedMentions_CompletesFast()
    {
        File.WriteAllText(Path.Combine(_dir, "longline.txt"),
            "Жрец возложил печати Наг на жертвенник и прочитал заклинание над свитком храма. ",
            new UTF8Encoding(false));
        // ×4000 ≈ 20k совпадений в одном предложении.
        File.AppendAllText(Path.Combine(_dir, "longline.txt"),
            string.Concat(Enumerable.Repeat("Жрец возложил печати Наг на жертвенник и прочитал заклинание над свитком храма. ", 4000)));

        var options = new AnalyzerOptions
        {
            Corpus = { Dir = _dir },
            Output = { Dir = Path.Combine(_dir, "_out") },
            Cache = { Dir = Path.Combine(_dir, "_out", ".cache") },        // Изоляция от машинного состояния: общий относительный ".cache"
        // делится между прогонами тестов (вскрыто R1-11).
        };
        Presets.Apply(Presets.Resolve("titanic", options), options.Processing); // mentionsPerFile=0
        var codex = DefaultCodex.Load();
        var catalog = new EntityCatalog(CodexSearchAdapter.ToDefinitions(codex).ToList(), generateVariants: true);
        var engine = new AnalysisEngine(options, catalog, "titanic");

        var sw = Stopwatch.StartNew();
        var session = engine.Run(CancellationToken.None);
        sw.Stop();

        Assert.True(session.Summary.TotalMentions > 0, "совпадения должны находиться");
        // Раньше: >75 c и не завершалось. После группировки по предложению — секунды.
        Assert.True(sw.ElapsedMilliseconds < 30_000,
            $"построение контекстов слишком долгое: {sw.ElapsedMilliseconds} мс");
    }

    /// <summary>BUG-N10: FindSpanIndex корректно локализует позицию в спанах.</summary>
    [Fact]
    public void N10_FindSpanIndex_LocatesPosition()
    {
        IReadOnlyList<SentenceSpan> spans =
        [
            new SentenceSpan(0, 10, 2),
            new SentenceSpan(11, 25, 3),
            new SentenceSpan(26, 40, 2),
        ];
        Assert.Equal(0, AnalysisEngine.FindSpanIndex(spans, 5));
        Assert.Equal(1, AnalysisEngine.FindSpanIndex(spans, 11));
        Assert.Equal(1, AnalysisEngine.FindSpanIndex(spans, 24));
        Assert.Equal(2, AnalysisEngine.FindSpanIndex(spans, 39));
        // Позиция за пределами — ближайший предыдущий спан.
        Assert.Equal(2, AnalysisEngine.FindSpanIndex(spans, 100));
    }

    /// <summary>Наблюдение: битый YAML даёт диагностичное сообщение, а не «сущностей нет».</summary>
    [Fact]
    public void BrokenYaml_ReportsParseError_NotEmptyEntities()
    {
        var defs = LegacyYamlImporter.ImportEntities("{{{{:::");
        Assert.Empty(defs);
        Assert.NotNull(LegacyYamlImporter.LastParseError);
        Assert.Contains("разобрать", LegacyYamlImporter.LastParseError, StringComparison.Ordinal);

        // Валидный YAML без секции entities — другая причина.
        var defs2 = LegacyYamlImporter.ImportEntities("foo: bar\n");
        Assert.Empty(defs2);
        Assert.Contains("entities", LegacyYamlImporter.LastParseError, StringComparison.Ordinal);
    }
}
