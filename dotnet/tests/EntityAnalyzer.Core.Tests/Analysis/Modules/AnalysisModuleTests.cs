using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Analysis.Modules;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Semantics;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

// ═══════════════ R1-11 «Единый контракт модулей анализа» ═══════════════
// IAnalysisModule — общий интерфейс подключаемых стадий анализа:
//   ProcessFile  — независимые от словаря находки (семантические паттерны);
//   EnrichFile   — обогащение после воронки сущностей (fuzzy-спасение);
//   OnSessionCompleted — сводная обработка всех файлов (межфайловый дедуп).
// Фазы необязательны (default-методы): модуль реализует только нужные.
// Воронка сущностей остаётся в движке сознательно: она владеет кэшем
// и счётчиками качества; миграция описана в README.

// LogSequential: конструктор/Dispose переключают процесс-глобальную
// EA_SEMANTIC_USER_DIR — вне последовательной коллекции это «переключало»
// пользовательский каталог под ногами параллельных тестов (флейк подписи
// кэша в TextPreprocessorTests.Signature_changes_only_with_preprocessors).
[Collection("LogSequential")]
public sealed class AnalysisModuleTests : IDisposable
{
    private readonly string _tempDir;

    public AnalysisModuleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ea_modules_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("EA_SEMANTIC_USER_DIR", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EA_SEMANTIC_USER_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* занято — не критично */ }
    }

    // ─── Фейки для проверки конвейера ───

    private sealed class FakeModule(string id, bool enabled) : IAnalysisModule
    {
        public string Id => id;
        public bool IsEnabled(AnalyzerOptions options) => enabled;
        public int SessionStarts { get; private set; }
        public int FilesProcessed { get; private set; }
        public int FilesEnriched { get; private set; }
        public int SessionsCompleted { get; private set; }

        public void OnSessionStarting(ModuleSessionContext context) => SessionStarts++;
        public void ProcessFile(FileModuleContext context) => FilesProcessed++;
        public void EnrichFile(FileModuleContext context) => FilesEnriched++;
        public void OnSessionCompleted(SessionModuleContext context) => SessionsCompleted++;
        public void AppendConfigSignature(StringBuilder sb, AnalyzerOptions options) => sb.Append('|').Append(Id);
    }

    private static AnalyzerOptions Options(bool semantic = false, bool fuzzy = false, bool crossDedup = false) =>
        new()
        {
            Processing = new ProcessingOptions
            {
                SemanticPatternsEnabled = semantic,
                FuzzyEnabled = fuzzy,
                CrossFileDeduplication = crossDedup,
            },
        };

    [Fact]
    public void Pipeline_keeps_only_enabled_modules()
    {
        var pipeline = new ModulePipeline(
            [new FakeModule("on", true), new FakeModule("off", false)], Options());

        Assert.Equal(["on"], pipeline.EnabledIds);
    }

    [Fact]
    public void Pipeline_aggregates_signatures_in_module_order()
    {
        var pipeline = new ModulePipeline(
            [new FakeModule("a", true), new FakeModule("b", true), new FakeModule("c", false)],
            Options());

        var sb = new StringBuilder("base");
        pipeline.AppendConfigSignature(sb);
        Assert.Equal("base|a|b", sb.ToString());
    }

    [Fact]
    public void Pipeline_dispatches_all_phases_to_enabled_modules()
    {
        var module = new FakeModule("m", true);
        var pipeline = new ModulePipeline([module], Options());
        var quality = new QualityCollector();

        pipeline.OnSessionStarting(new ModuleSessionContext
            { Catalog = null!, Options = Options(), Quality = quality });

        var fileCtx = new FileModuleContext
        {
            Path = "t.txt",
            NormalizedText = "текст",
            LineStarts = [0],
            Quality = quality,
            Mentions = [],
            FoundEntities = [],
        };
        pipeline.ProcessFile(fileCtx);
        pipeline.EnrichFile(fileCtx);

        pipeline.OnSessionCompleted(new SessionModuleContext { Results = [], Quality = quality });

        Assert.Equal(1, module.SessionStarts);
        Assert.Equal(1, module.FilesProcessed);
        Assert.Equal(1, module.FilesEnriched);
        Assert.Equal(1, module.SessionsCompleted);
    }

    // ─── Семантический модуль ───

    [Fact]
    public void Semantic_module_is_disabled_by_option()
    {
        var module = new SemanticPatternsModule();
        Assert.False(module.IsEnabled(Options(semantic: false)));
        Assert.True(module.IsEnabled(Options(semantic: true)));
    }

    [Fact]
    public void Semantic_module_signature_contains_SEM_part_when_enabled()
    {
        // Пользовательский паттерн через официальный сериализатор → отпечаток SEM.
        var pattern = new Semantics.SemanticPattern("t1", "Тест", "описание",
            [new Semantics.SemanticToken(Semantics.TokenKind.Literal, "ритуал")],
            Window: 6, Enabled: true);
        File.WriteAllText(Path.Combine(_tempDir, "semantic_patterns.json"),
            Semantics.SemanticPatternJson.Serialize([pattern]));

        var module = new SemanticPatternsModule();
        var options = Options(semantic: true);
        Assert.True(module.IsEnabled(options));

        var sb = new StringBuilder("base");
        module.AppendConfigSignature(sb, options);
        Assert.Contains("|SEM:", sb.ToString());

        // Выключенная опция — модуль не пишет в подпись вовсе.
        var off = new StringBuilder("base");
        module.AppendConfigSignature(off, Options(semantic: false));
        Assert.DoesNotContain("|SEM:", off.ToString());
    }

    [Fact]
    public void Semantic_module_scans_file_and_fills_context()
    {
        // Встроенные паттерны и словари (пользовательский каталог пуст).
        var module = new SemanticPatternsModule();
        var options = Options(semantic: true);
        var catalog = new EntityCatalog([NugAndYeb]);
        module.OnSessionStarting(new ModuleSessionContext
            { Catalog = catalog, Options = options, Quality = new QualityCollector() });

        var text = Text.TextNormalizer.Normalize("Верховный жрец хранил запретный фолиант.");
        var ctx = new FileModuleContext
        {
            Path = "t.txt",
            NormalizedText = text,
            LineStarts = [0],
            Quality = new QualityCollector(),
            Mentions = [],
            FoundEntities = [],
        };
        module.ProcessFile(ctx);

        Assert.Contains(ctx.SemanticMatches, m => m.PatternId == "forbidden-book");
    }

    // ─── Fuzzy-спасение как модуль ───

    private static readonly EntityDefinition NugAndYeb =
        new("Наг и Йеб", "Nug and Yeb", ["Нага"], []);

    private static readonly EntityDefinition Yeb =
        new("Йеб", "Yeb", ["Йеб"], []);

    [Fact]
    public void Fuzzy_rescue_appends_marked_mentions_after_funnel()
    {
        // Воронка нашла «Наг и Йеб», но не «Йеб» — её и спасает fuzzy.
        var catalog = new EntityCatalog([NugAndYeb, Yeb]);
        var module = new FuzzyRescueModule();
        var options = Options(fuzzy: true);
        options.Processing.FuzzyThreshold = 50; // «Йебаа»≈«Йеб» проходит
        Assert.True(module.IsEnabled(options));
        module.OnSessionStarting(new ModuleSessionContext
            { Catalog = catalog, Options = options, Quality = new QualityCollector() });

        var text = Text.TextNormalizer.Normalize("Жрец Нага шептал у алтаря Йебаа.");
        var ctx = new FileModuleContext
        {
            Path = "t.txt",
            NormalizedText = text,
            LineStarts = [0],
            Quality = new QualityCollector(),
            Mentions = [new Mention(1, "Наг и Йеб", "Нага", 1, "контекст")],
            FoundEntities = ["Наг и Йеб"],
        };
        module.EnrichFile(ctx);

        Assert.Equal(2, ctx.Mentions.Count);
        var rescued = ctx.Mentions[1];
        Assert.True(rescued.IsFuzzy);
        Assert.Equal("Йеб", rescued.Entity);
        Assert.Contains("[fuzzy:", rescued.Variant);
    }

    [Fact]
    public void Fuzzy_rescue_skips_empty_funnel_results()
    {
        var catalog = new EntityCatalog([NugAndYeb, Yeb]);
        var module = new FuzzyRescueModule();
        var options = Options(fuzzy: true);
        module.OnSessionStarting(new ModuleSessionContext
            { Catalog = catalog, Options = options, Quality = new QualityCollector() });

        var text = Text.TextNormalizer.Normalize("Жрец Нагаа шептал у алтаря.");
        var ctx = new FileModuleContext
        {
            Path = "t.txt",
            NormalizedText = text,
            LineStarts = [0],
            Quality = new QualityCollector(),
            Mentions = [],              // воронка ничего не нашла → спасение не запускается
            FoundEntities = [],
        };
        module.EnrichFile(ctx);
        Assert.Empty(ctx.Mentions);
    }

    // ─── Межфайловый дедуп как модуль ───

    [Fact]
    public void Cross_file_dedup_collapses_identical_contexts_on_session_complete()
    {
        var module = new CrossFileDedupModule();
        var options = Options(crossDedup: true);
        Assert.True(module.IsEnabled(options));

        var quality = new QualityCollector();
        var a = new FileResult("a.txt", "a.txt",
            [new Mention(1, "Нага", "Нага", 1, "Общий контекст про алтарь")], 1, 8, 100, 100);
        var b = new FileResult("b.txt", "b.txt",
            [new Mention(1, "Нага", "Нага", 1, "Общий контекст про алтарь")], 1, 8, 100, 100);

        var ctx = new SessionModuleContext { Results = [a, b], Quality = quality };
        module.OnSessionCompleted(ctx);

        // Повтор схлопнут в первое вхождение: в a.txt счётчик 2, b.txt опустел.
        Assert.Equal(2, ctx.Results[0].Mentions[0].Repeats);
        Assert.Empty(ctx.Results[1].Mentions);
    }
}
