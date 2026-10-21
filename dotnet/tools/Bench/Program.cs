using System.Diagnostics;
using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Filtering;
using EntityAnalyzer.Core.IO;
using EntityAnalyzer.Core.Matching;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Text;
using EntityAnalyzer.Core.Variants;

// ─────────────────────────────────────────────────────────────
// Bench: регрессионный замер ядра (аналог BenchmarkDotNet,
// без внешних зависимостей). Median of N, warmup, baseline JSON.
// Запуск: dotnet run -c Release -- --corpus <dir> [--n 5]
// R1-14 performance-gate: --gate [--threshold 1.3] — сравнить свежие
// замеры с bench/baseline.json; замедление стадии сверх порога = код 3.
// ─────────────────────────────────────────────────────────────

string? corpus = null;
var n = 5;
var gate = false;
var threshold = EntityAnalyzer.Core.Persistence.PerformanceGate.DefaultThreshold;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--corpus") corpus = args[i + 1];
    if (args[i] == "--n") n = Math.Max(1, int.Parse(args[i + 1]));
    if (args[i] == "--threshold" &&
        double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 1)
        threshold = t;
}
gate = args.Contains("--gate");

Console.WriteLine("ENTITY ANALYZER BENCH");
Console.WriteLine("=====================");
if (gate) Console.WriteLine($"Режим: performance-gate (порог ×{threshold:0.00})");
var results = new List<(string Name, double MedianMs, double MinMs, double MaxMs)>();

double Bench(string name, int iterations, Action body)
{
    for (var w = 0; w < 2; w++) body(); // warmup
    var times = new List<double>(iterations);
    for (var i = 0; i < iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        body();
        sw.Stop();
        times.Add(sw.Elapsed.TotalMilliseconds);
    }
    times.Sort();
    var median = times[times.Count / 2];
    results.Add((name, median, times[0], times[^1]));
    Console.WriteLine($"  {name.PadRight(34)} median {median,9:0.00} ms   min {times[0],9:0.00}   max {times[^1],9:0.00}");
    return median;
}

// --- данные ---
var sampleText = File.Exists(Path.Combine(corpus ?? "", "sample.txt"))
    ? File.ReadAllText(Path.Combine(corpus!, "sample.txt"))
    : string.Concat(Enumerable.Repeat(
        "Жрец Нага шептал у алтаря Йеба, и тьма отвечала ему из глубины подземелья. ",
        2000));

var catalog = new EntityCatalog([new EntityDefinition("Наг и Йеб", "Nug and Yeb", ["Нага", "Йеба"], [])]);
var automaton = catalog.BuildAutomaton();
var bytes = Encoding.UTF8.GetBytes(sampleText);

Console.WriteLine($"Образец текста: {sampleText.Length:N0} символов; вариантов в каталоге: {catalog.VariantsByEntity.Values.Sum(v => v.Count)}");
Console.WriteLine();

Bench("transliterate+variants", n, () => _ = new VariantFactory(600).Generate("Nyarlathotep", true, 2));
Bench("decode utf8", n * 4, () => _ = DocumentReader.DecodeText(bytes));
Bench("normalize", n * 2, () => _ = TextNormalizer.Normalize(sampleText));
Bench("sentence split", n * 2, () => _ = SentenceSplitter.SplitWithSpans(sampleText));

List<RawMatch> hits = [];
Bench("automaton scan", n * 2, () =>
{
    hits = [];
    foreach (var h in automaton.Find(sampleText)) hits.Add(new RawMatch(h.Start, h.End, h.Payload.Variant, h.Payload.Entity));
});
Console.WriteLine($"  → совпадений на образец: {hits.Count}");

var spans = SentenceSplitter.SplitWithSpans(sampleText);
Bench("context build x50", n, () =>
{
    for (var i = 0; i < 50 && i < hits.Count; i++)
        _ = ContextBuilder.Build(sampleText, spans, hits[i].Start, hits[i].End, 75);
});

var listing = new ListingDensityFilter(new HashSet<string> { "бог", "древний" });
Bench("listing filter x200", n * 2, () =>
{
    for (var i = 0; i < 200; i++)
        _ = listing.IsListing(sampleText[..Math.Min(600, sampleText.Length)], "Наг и Йеб", catalog.AllNames);
});

// Полный прогон корпуса, если указан.
if (corpus is not null && Directory.Exists(corpus))
{
    var options = new AnalyzerOptions
    {
        Corpus = new CorpusOptions { Dir = corpus },
        Output = new OutputOptions { Dir = Path.Combine(Path.GetTempPath(), "ea_bench_out") },
        Cache = new CacheOptions { Enabled = false },
    };
    var engine = new AnalysisEngine(options, catalog, "bench");
    var t = Bench("FULL RUN (cold cache)", Math.Min(3, n), () => engine.Run(CancellationToken.None));
}

// Baseline JSON / performance-gate (R1-14).
// Эталон живёт в <корень репозитория>/bench/baseline.json; корень ищется
// по маркеру решения — привязка к AppContext.BaseDirectory ломалась
// при переносе сборки (файл оказывался рядом с проектом).
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EntityAnalyzer.slnx")))
        dir = dir.Parent!;
    return dir?.FullName ?? AppContext.BaseDirectory;
}
var benchDir = Path.Combine(FindRepoRoot(), "bench");
var baselinePath = Path.GetFullPath(Path.Combine(benchDir, "baseline.json"));

// В режиме гейта эталон читается ДО замеров и никогда не перезаписывается —
// иначе следующее сравнение сравнивало бы прогресс с самим собой.
List<EntityAnalyzer.Core.Persistence.PerfSample> baseline = [];
if (gate)
{
    try
    {
        if (File.Exists(baselinePath))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(baselinePath));
            baseline = doc.RootElement.EnumerateArray()
                .Select(e => new EntityAnalyzer.Core.Persistence.PerfSample(
                    e.GetProperty("name").GetString() ?? "",
                    e.GetProperty("medianMs").GetDouble()))
                .ToList();
        }
    }
    catch { /* битый baseline = его отсутствие */ }
}

try
{
    Directory.CreateDirectory(benchDir);
    var lines = results.Select(r => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "{{ \"name\": \"{0}\", \"medianMs\": {1:0.00}, \"minMs\": {2:0.00}, \"maxMs\": {3:0.00} }}",
        r.Name, r.MedianMs, r.MinMs, r.MaxMs));
    var target = gate ? Path.Combine(benchDir, "last.json") : baselinePath;
    File.WriteAllText(target, "[" + string.Join(",\n", lines) + "]");
    Console.WriteLine();
    Console.WriteLine(gate
        ? "Свежие замеры сохранены: bench/last.json (эталон не тронут)"
        : "Baseline сохранён: tests/../bench/baseline.json");
}
catch { }

if (gate)
{
    if (baseline.Count == 0)
    {
        // Первый запуск гейта: фиксируем эталон свежими замерами — иначе
        // сравнивать будет не с чем, и ворота никогда не закроются.
        try
        {
            Directory.CreateDirectory(benchDir);
            var lines0 = results.Select(r => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{{ \"name\": \"{0}\", \"medianMs\": {1:0.00}, \"minMs\": {2:0.00}, \"maxMs\": {3:0.00} }}",
                r.Name, r.MedianMs, r.MinMs, r.MaxMs));
            File.WriteAllText(baselinePath, "[" + string.Join(",\n", lines0) + "]");
            Console.WriteLine("Baseline отсутствовал — эталон зафиксирован текущими замерами (зелёный).");
        }
        catch { }
        return 0;
    }

    var current = results.Select(r => new EntityAnalyzer.Core.Persistence.PerfSample(r.Name, r.MedianMs));
    var regressions = EntityAnalyzer.Core.Persistence.PerformanceGate.Compare(baseline, current, threshold);

    Console.WriteLine();
    Console.WriteLine($"PERFORMANCE GATE: стадий в эталоне {baseline.Count}, регрессий {regressions.Count}");
    foreach (var r in regressions)
        Console.WriteLine($"  ✗ {r.Name}: {r.BaselineMs:0.00} ms → {r.CurrentMs:0.00} ms (×{r.Ratio:0.00} > ×{threshold:0.00})");

    return regressions.Count > 0 ? 3 : 0;
}

return 0;
