using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Сквозные метрики L2-кэша (память) в отчёте о качестве: холодный прогон
/// наполняет диск, тёплый отдаётся из RAM без чтения файлов кэша, отключение
/// слоя и его очистка возвращают прогон на дисковый путь. Коллекция
/// LogSequential: L2 живёт в статическом состоянии процесса.
/// </summary>
[Collection("LogSequential")]
public class MemoryCacheEngineTests : IDisposable
{
    private readonly string _corpus;
    private readonly string _out;
    private readonly string _cacheDir;

    public MemoryCacheEngineTests()
    {
        _corpus = Path.Combine(Path.GetTempPath(), "ea_l2e_" + Guid.NewGuid().ToString("N"));
        _out = Path.Combine(_corpus, "_out");
        _cacheDir = Path.Combine(_out, ".cache");
        Directory.CreateDirectory(_corpus);
        MemoryCacheLayer.Clear(_cacheDir); // изоляция от прошлых тестов процесса
    }

    public void Dispose()
    {
        MemoryCacheLayer.Clear(_cacheDir);
        try { Directory.Delete(_corpus, recursive: true); } catch { }
    }

    private AnalysisEngine BuildEngine(bool memory = true, int budgetMb = 64)
    {
        var options = new AnalyzerOptions
        {
            Corpus = { Dir = _corpus },
            Output = { Dir = _out },
            // Герметичность: абсолютный каталог кэша в temp, а не CWD-относительный.
            Cache = { Dir = _cacheDir, MemoryEnabled = memory, MemoryBudgetMb = budgetMb },
        };
        Presets.Apply(Presets.Resolve("light", options), options.Processing);
        var codex = DefaultCodex.Load();
        var definitions = CodexSearchAdapter.ToDefinitions(codex).ToList();
        var catalog = new EntityCatalog(definitions, generateVariants: true);
        return new AnalysisEngine(options, catalog, "light");
    }

    private void WriteCorpusFile() =>
        File.WriteAllText(Path.Combine(_corpus, "a.txt"),
            "Хроники храма. Наг и Йеб правили веками. Культ Нага процветал у стен древнего города.",
            new UTF8Encoding(false));

    [Fact]
    public void Warm_run_is_served_from_memory_layer()
    {
        WriteCorpusFile();

        var cold = BuildEngine().Run(CancellationToken.None);
        Assert.Equal(0, cold.Quality.L2Hits);
        Assert.True(cold.Quality.L2Misses >= 1, "холодный прогон обязан уходить на диск");

        var warm = BuildEngine().Run(CancellationToken.None);
        Assert.Equal(1, warm.Quality.FilesUnchangedSkipped);
        Assert.Equal(1, warm.Quality.L2Hits);   // файл отдан из RAM
        Assert.Equal(0, warm.Quality.L2Misses); // диск не читался
        Assert.Equal(cold.Summary.TotalMentions, warm.Summary.TotalMentions);
    }

    [Fact]
    public void Disabled_memory_layer_keeps_disk_cache_working()
    {
        WriteCorpusFile();

        _ = BuildEngine(memory: false).Run(CancellationToken.None);
        var warm = BuildEngine(memory: false).Run(CancellationToken.None);

        Assert.Equal(1, warm.Quality.FilesUnchangedSkipped); // инкрементальный индекс + диск
        Assert.Equal(0, warm.Quality.L2Hits);                // слой не подключался вовсе
        Assert.Equal(0, warm.Quality.L2Misses);
    }

    [Fact]
    public void Clearing_memory_layer_falls_back_to_disk()
    {
        WriteCorpusFile();
        _ = BuildEngine().Run(CancellationToken.None);
        var warm = BuildEngine().Run(CancellationToken.None);
        Assert.Equal(1, warm.Quality.L2Hits);

        MemoryCacheLayer.Clear(_cacheDir); // как кнопка «Очистить кэш» в GUI

        var third = BuildEngine().Run(CancellationToken.None);
        Assert.Equal(1, third.Quality.FilesUnchangedSkipped); // запись нашлась на диске
        Assert.Equal(0, third.Quality.L2Hits);
        Assert.True(third.Quality.L2Misses >= 1, "после очистки запрос обязан уйти на диск");
    }
}
