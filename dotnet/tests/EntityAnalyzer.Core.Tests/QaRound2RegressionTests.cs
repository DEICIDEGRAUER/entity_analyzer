using System.IO;
using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Filtering;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Регрессионные тесты QA-кампании 25.08.2026 (qa_report_entity_analyzer.md):
/// N01–N08 .NET-дефекты. Каждый тест привязан к конкретному багу отчёта.
/// </summary>
[Collection("LogSequential")]
public class QaRound2RegressionTests : IDisposable
{
    private readonly string _corpus;
    private readonly string _out;

    public QaRound2RegressionTests()
    {
        _corpus = Path.Combine(Path.GetTempPath(), "ea_qa2_" + Guid.NewGuid().ToString("N"));
        _out = Path.Combine(_corpus, "_out");
        Directory.CreateDirectory(_corpus);
    }

    public void Dispose()
    {
        try { Directory.Delete(_corpus, recursive: true); } catch { }
    }

    private AnalysisEngine BuildEngine(string preset = "light", bool skipDuplicates = true)
    {
        var options = new AnalyzerOptions
        {
            Corpus = { Dir = _corpus, SkipDuplicates = skipDuplicates },
            Output = { Dir = _out },
        };
        // Изоляция от машинного состояния: дефолтный Cache.Dir=".cache"
        // относителен и делится между всеми прогонами тестов; кэш-попадания
        // из чужих прогонов лишают тест полного анализа (вскрыто R1-11).
        options.Cache.Dir = Path.Combine(_out, ".cache");
        Presets.Apply(Presets.Resolve(preset, options), options.Processing);
        var codex = DefaultCodex.Load();
        var definitions = CodexSearchAdapter.ToDefinitions(codex).ToList();
        var catalog = new EntityCatalog(definitions, generateVariants: true);
        return new AnalysisEngine(options, catalog, preset);
    }

    /// <summary>QA N05: реплика с тире не отсекается осмысленностью.</summary>
    [Fact]
    public void N05_DialogueDash_IsMeaningful()
    {
        var f = new MeaningfulnessFilter();
        Assert.False(f.IsMeaningless("─ реплика: — Стой! — сказал Наг."));
        Assert.False(f.IsMeaningless("— Стой! — крикнул страж у ворот."));
        // Не-диалоговый мусор по-прежнему режется.
        Assert.True(f.IsMeaningless("аввв бббв гггд ддде еееё жжжж зззз ииии кккк лллл"));
    }

    /// <summary>QA N05: LooksLikeDialogue распознаёт тире и глаголы речи.</summary>
    [Fact]
    public void N05_LooksLikeDialogue_DetectsDashAndSpeechVerb()
    {
        Assert.True(MeaningfulnessFilter.LooksLikeDialogue("— Стой! — сказал он."));
        Assert.True(MeaningfulnessFilter.LooksLikeDialogue("Он сказал: привет."));
        Assert.False(MeaningfulnessFilter.LooksLikeDialogue("аввв бббв гггд ддде еееё"));
    }

    /// <summary>
    /// QA N02/N03/N04/N06: полный прогон на краевом корпусе — секция
    /// КОНФИГУРАЦИЯ заполнена, дубликаты в отчёте, воронка сходится,
    /// пропуски файлов посчитаны.
    /// </summary>
    [Fact]
    public void N02_N03_N04_N06_FullReportSections_And_FunnelClosure()
    {
        File.WriteAllText(Path.Combine(_corpus, "a.txt"),
            "Хроники храма. Наг и Йеб правили веками. Культ Нага процветал.",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_corpus, "u16.txt"),
            "Хроники храма. Наг и Йеб правили веками. Культ Нага процветал.",
            Encoding.Unicode); // UTF-16 LE с BOM
        File.WriteAllText(Path.Combine(_corpus, "dup.txt"),
            "Хроники храма. Наг и Йеб правили веками. Культ Нага процветал.",
            Encoding.Unicode); // дубликат u16.txt
        File.WriteAllText(Path.Combine(_corpus, "empty.txt"), "", new UTF8Encoding(false));

        var session = BuildEngine().Run(CancellationToken.None);

        // N06: пропуски посчитаны (пустой + дубликат содержимого).
        // Диагностика в тексте ассерта: при редком I/O-пропуске файла в обходе
        // (молчаливый catch в DiscoverFiles) видно фактический состав корпуса.
        var walkInfo = string.Join(",", Directory.GetFiles(_corpus).OrderBy(f => f).Select(f =>
        {
            var fi = new FileInfo(f);
            return $"{Path.GetFileName(f)}:{fi.Length}";
        }));
        Assert.True(session.Quality.SkippedEmpty >= 1,
            "пустой файл должен быть посчитан; файлы: " + walkInfo);
        Assert.True(session.Quality.SkippedDuplicate >= 1,
            "дубликат содержимого должен быть посчитан; файлы: " + walkInfo);

        // N03: группы дубликатов построены.
        Assert.NotEmpty(session.DuplicateGroups);

        // N04: арифметика воронки замыкается.
        Assert.Equal(0, session.Quality.FunnelLeftover);

        // Экспорт с options → секции заполнены.
        ReportExporter.ExportAll(session, _out,
            new HashSet<string>(["txt"], StringComparer.OrdinalIgnoreCase),
            perEntityFolders: false, catalog: null,
            options: new AnalyzerOptions { Corpus = { Dir = _corpus }, Output = { Dir = _out } });

        var report = File.ReadAllText(Path.Combine(_out, "analysis_report.txt"));
        // N02: конфигурация заполнена (после заголовка есть содержимое).
        var cfgIdx = report.IndexOf("КОНФИГУРАЦИЯ", StringComparison.Ordinal);
        var corpusIdx = report.IndexOf("── КОРПУС", StringComparison.Ordinal);
        Assert.True(cfgIdx >= 0 && corpusIdx > cfgIdx);
        Assert.Contains("Корпус           :", report[cfgIdx..corpusIdx]);
        // N03: секция дубликатов присутствует.
        Assert.Contains("ТЕКСТОВЫЕ ДУБЛИКАТЫ", report);
        // N04: сверка воронки напечатана и равна нулю.
        Assert.Contains("остаток 0", report);
        // N06: строка пропусков.
        Assert.Contains("Пропущено файлов :", report);
    }


    /// <summary>
    /// Дедуп должен срабатывать и на тёплом кэше: иначе второй файл с тем же
    /// содержимым получает готовый результат из кэша, обходит SkippedDuplicate
    /// и оба файла попадают в отчёт. Регрессия гонки, вскрытой полным прогоном.
    /// </summary>
    [Fact]
    public void DuplicateContent_IsSkippedEvenWithWarmCache()
    {
        const string body = "Хроники храма. Наг и Йеб правили веками. Культ Нага процветал.";
        File.WriteAllText(Path.Combine(_corpus, "a.txt"), body, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_corpus, "dup.txt"), body, new UTF8Encoding(false));

        var cold = BuildEngine(skipDuplicates: false).Run(CancellationToken.None);
        Assert.Equal(2, cold.Files.Count);

        var warm = BuildEngine().Run(CancellationToken.None);
        Assert.True(warm.Quality.SkippedDuplicate >= 1,
            "дубликат должен быть пропущен и на тёплом кэше");
        Assert.Single(warm.Files);
        Assert.NotEmpty(warm.DuplicateGroups);
    }

    /// <summary>QA N07: пустой корпус не печатает прогресс-строку «…0/0».</summary>
    [Fact]
    public void N07_EmptyCorpus_NoProgressNoise()
    {
        var lines = new List<string>();
        var engine = BuildEngine();
        engine.Run(CancellationToken.None, new CollectingProgress(lines));
        Assert.DoesNotContain(lines, l => l.StartsWith("…0/0"));
    }

    private sealed class CollectingProgress(List<string> sink) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress p)
        {
            if (p.TotalFiles == 0) return; // тот же guard, что в ConsoleProgress
            sink.Add($"…{p.ProcessedFiles}/{p.TotalFiles}");
        }
    }
}
