using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Фактический снимок самопроверочного прогона.</summary>
public sealed record SelfCheckSnapshot(
    int FilesTotal,
    int FilesWithMentions,
    int TotalMentions,
    IReadOnlyDictionary<string, int> EntityCounts);

/// <summary>Эталон самопроверки (Resources/selfcheck_baseline.json).</summary>
public sealed record SelfCheckBaseline(
    int FilesTotal,
    int FilesWithMentions,
    int TotalMentions,
    IReadOnlyDictionary<string, int> EntityCounts);

/// <summary>Итог самопроверки: вердикт, список расхождений, длительность.</summary>
public sealed record SelfCheckResult(bool Passed, IReadOnlyList<string> Problems, double DurationMs);

/// <summary>
/// Самопроверка релиза (R2-16 / R1-15): первый запуск новой версии гоняет
/// встроенный мини-корпус (тексты демо-корпуса R2-18) со сверкой эталона.
/// Ловит «тихую» поломку движка до того, как пользователь потратит часы
/// анализа: словарь, автомат, фильтры и сводка проверяются одним прогоном.
/// Опции прогона зафиксированы кодом, а не пресетом — правка пресетов не
/// должна ломать эталон; эталон меняется только осознанно вместе с движком
/// (CLI: --self-check --update-baseline). Прогон идёт во временном каталоге:
/// ни корпус, ни %APPDATA% пользователя не затрагиваются.
/// </summary>
public static class SelfCheck
{
    /// <summary>Имя встроенного ресурса с эталоном.</summary>
    public const string BaselineResourceName = "EntityAnalyzer.Core.Resources.selfcheck_baseline.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Детерминированный прогон движка на встроенном мини-корпусе.
    /// Каталог workDir используется как рабочая зона (корпус/вывод/кэш внутри).
    /// </summary>
    public static SelfCheckSnapshot RunEngine(string workDir)
    {
        var corpusDir = Path.Combine(workDir, "corpus");
        DemoCorpus.WriteTo(corpusDir);

        var options = new AnalyzerOptions();
        options.Corpus.Dir = Path.GetFullPath(corpusDir);
        options.Output.Dir = Path.Combine(workDir, "out");
        options.Cache.Dir = Path.Combine(workDir, ".cache");
        // Фиксация опций кодом — см. описание класса.
        options.Processing.ContextMaxWords = 40;
        options.Processing.MaxMentionsPerFile = 5;
        options.Processing.FuzzyEnabled = false;
        options.Processing.DeepCorrupt = false;
        options.Processing.IncrementalEnabled = false;

        var codex = DefaultCodex.Load();
        var catalog = new EntityCatalog(
            CodexSearchAdapter.ToDefinitions(codex).ToList(), generateVariants: true);

        var session = new AnalysisEngine(options, catalog, presetName: "selfcheck")
            .Run(CancellationToken.None);

        return new SelfCheckSnapshot(
            session.Files.Count,
            session.Summary.FilesWithMentions,
            session.Summary.TotalMentions,
            new Dictionary<string, int>(session.Summary.EntityCounts, StringComparer.Ordinal));
    }

    /// <summary>
    /// Чистая функция сверки «эталон ↔ факт». Пустой список — релиз цел;
    /// каждая проблема описана по-русски с обоими числами для отчёта.
    /// </summary>
    public static IReadOnlyList<string> Verify(SelfCheckBaseline expected, SelfCheckSnapshot actual)
    {
        var problems = new List<string>();

        if (expected.FilesTotal != actual.FilesTotal)
            problems.Add($"Файлов в корпусе: ожидалось {expected.FilesTotal}, получено {actual.FilesTotal}");
        if (expected.FilesWithMentions != actual.FilesWithMentions)
            problems.Add($"Файлов с находками: ожидалось {expected.FilesWithMentions}, получено {actual.FilesWithMentions}");
        if (expected.TotalMentions != actual.TotalMentions)
            problems.Add($"Итого упоминаний: ожидалось {expected.TotalMentions}, получено {actual.TotalMentions}");

        foreach (var name in expected.EntityCounts.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var got = actual.EntityCounts.GetValueOrDefault(name);
            if (got != expected.EntityCounts[name])
                problems.Add($"Сущность «{name}»: ожидалось {expected.EntityCounts[name]}, получено {got}");
        }
        foreach (var name in actual.EntityCounts.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!expected.EntityCounts.ContainsKey(name))
                problems.Add($"Лишняя сущность «{name}»: {actual.EntityCounts[name]} (в эталоне отсутствует)");
        }

        return problems;
    }

    /// <summary>
    /// Оркестрация: прогон + сверка. baseline null → встроенный эталон;
    /// если и его нет — честный провал с объяснением, а не исключение.
    /// </summary>
    public static SelfCheckResult Run(string? workDir = null, SelfCheckBaseline? baseline = null)
    {
        var sw = Stopwatch.StartNew();
        var dir = workDir ?? Path.Combine(Path.GetTempPath(), "ea_selfcheck_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        List<string> problems;
        var effectiveBaseline = baseline ?? LoadEmbeddedBaseline();
        if (effectiveBaseline is null)
        {
            problems = ["В сборке нет эталона Resources/selfcheck_baseline.json — сверка невозможна"];
        }
        else
        {
            problems = [.. Verify(effectiveBaseline, RunEngine(dir))];
        }

        sw.Stop();
        return new SelfCheckResult(problems.Count == 0, problems, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Встроенный эталон из сборки; null — ресурс не вложен.</summary>
    public static SelfCheckBaseline? LoadEmbeddedBaseline()
    {
        using var stream = typeof(SelfCheck).Assembly.GetManifestResourceStream(BaselineResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<SelfCheckBaseline>(reader.ReadToEnd(), JsonOpts);
    }

    /// <summary>Снимок в каноническом JSON — формат файла эталона (--update-baseline).</summary>
    public static string SnapshotJson(SelfCheckSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, JsonOpts);
}
