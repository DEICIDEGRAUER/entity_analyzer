using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>End-to-end engine runs over a synthetic corpus in a temp folder.</summary>
public class AnalysisEngineTests : IDisposable
{
    private readonly string _corpus = Path.Combine(Path.GetTempPath(), "ea-engine-" + Guid.NewGuid().ToString("N"));
    private readonly string _out = Path.Combine(Path.GetTempPath(), "ea-out-" + Guid.NewGuid().ToString("N"));

    private static readonly EntityDefinition Nug = new(
        "Наг и Йеб", "Nug and Yeb",
        ["Наг и Йеб", "Йеб"], ["Владыка"]);

    public AnalysisEngineTests()
    {
        // The engine registers this too, but fixture files are written before it runs.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        Directory.CreateDirectory(_corpus);

        // UTF-8 file: 3 valid mentions separated by unique fillers + glued false positives.
        File.WriteAllText(Path.Combine(_corpus, "utf8.txt"),
            "Первая строка без имени.\n" +
            "Маятник часов мерно качался под сводами старой библиотеки.\n" +
            "Пыль оседала на кожаные корки редких изданий.\n" +
            "За окном шумел дождь третьи сутки подряд.\n" +
            "Здесь Наг и Йеб впервые явились исследователям.\n" +
            "Лампа мигнула и погасла на целую минуту.\n" +
            "Нагиз и Йебовый — ложные склейки, не должны считаться.\n" +
            "Профессор записал наблюдения в потрёпанный журнал.\n" +
            "Опять Наг и Йеб пришли в ночной тиши.\n" +
            "Скрипнула дверь подвала, но никто не вздрогнул.\n" +
            "Свеча оплывала быстрее обычного воска.\n" +
            "И снова Йеб растворился в полумраке архива.\n",
            new System.Text.UTF8Encoding(false));

        // CP1251-encoded file (legacy encodings must be auto-detected).
        File.WriteAllBytes(Path.Combine(_corpus, "cp1251.txt"),
            System.Text.Encoding.GetEncoding(1251).GetBytes(
                "Старый файл в кодировке.\nВладыка — так звали Наг и Йеб в деревне.\n"));

        // Empty-hit file.
        File.WriteAllText(Path.Combine(_corpus, "empty.txt"), "Тут ничего интересного нет.\n");

        // Oversized extension filtered out by discovery.
        File.WriteAllText(Path.Combine(_corpus, "skip.log"), "Наг и Йеб");
    }

    public void Dispose()
    {
        try { Directory.Delete(_corpus, true); } catch { }
        try { Directory.Delete(_out, true); } catch { }
    }

    private AnalysisEngine MakeEngine(AnalyzerOptions? options = null)
    {
        options ??= new AnalyzerOptions();
        options.Corpus.Dir = _corpus;
        options.Output.Dir = _out;
        // Изоляция от машинного состояния: дефолтный Cache.Dir=".cache"
        // относителен и делится между всеми прогонами тестов (вскрыто R1-11).
        options.Cache.Dir = Path.Combine(_out, ".cache");
        Presets.Apply(Presets.Resolve("extended"), options.Processing);
        return new AnalysisEngine(options, new EntityCatalog([Nug]), presetName: "extended");
    }

    [Fact]
    public void Discovers_only_configured_extensions()
    {
        var session = MakeEngine(new AnalyzerOptions()).Run(CancellationToken.None);
        Assert.Equal(3, session.Files.Count);
    }

    [Fact]
    public void Finds_mentions_and_skips_glued_false_positives()
    {
        var session = MakeEngine().Run(CancellationToken.None);
        var utf8 = session.Files.Single(f => f.FileName == "utf8.txt");

        Assert.Equal(3, utf8.Mentions.Count);          // exactly three clean mentions
        Assert.True(utf8.TotalFound >= utf8.Mentions.Count);

        Assert.Contains(utf8.Mentions, m => m.Variant == "Йеб");
        // Glued words («Нагиз», «Йебовый») must never become mentions themselves.
        Assert.All(utf8.Mentions, m => Assert.DoesNotContain("Нагиз", m.Variant));
        Assert.All(utf8.Mentions, m => Assert.DoesNotContain("Йебовый", m.Variant));
        Assert.All(utf8.Mentions, m => Assert.DoesNotContain("ложные склейки", m.Variant));
    }

    [Fact]
    public void Cp1251_files_are_decoded()
    {
        var session = MakeEngine().Run(CancellationToken.None);
        var old = session.Files.Single(f => f.FileName == "cp1251.txt");
        Assert.True(old.CharCount > 0);
        Assert.NotEmpty(old.Mentions);                 // «Наг и Йеб» found after decoding
        Assert.DoesNotContain("\uFFFD", old.Mentions[0].Context);
    }

    [Fact]
    public void Line_numbers_point_into_original_file()
    {
        var session = MakeEngine().Run(CancellationToken.None);
        var utf8 = session.Files.Single(f => f.FileName == "utf8.txt");
        var first = utf8.Mentions.MinBy(m => m.Line)!;
        Assert.Equal(5, first.Line);                   // mention on line 5 of the file
    }

    [Fact]
    public void Title_mention_requires_reinforcing_marker()
    {
        var session = MakeEngine().Run(CancellationToken.None);
        var old = session.Files.Single(f => f.FileName == "cp1251.txt");
        // «Владыка» stands next to «Наг и Йеб» → kept as a title mention.
        Assert.Contains(old.Mentions, m => m.Variant == "Владыка");
    }

    [Fact]
    public void Density_computed_per_10k_chars()
    {
        var session = MakeEngine().Run(CancellationToken.None);
        var utf8 = session.Files.Single(f => f.FileName == "utf8.txt");
        Assert.True(utf8.DensityPer10k > 0);
        Assert.Equal(Math.Round(utf8.TotalFound * 10000.0 / utf8.CharCount, 2), utf8.DensityPer10k, 1);
    }

    [Fact]
    public void Summary_aggregates_session()
    {
        var session = MakeEngine().Run(CancellationToken.None);
        Assert.Equal(2, session.Summary.FilesWithMentions);
        Assert.Equal(session.Files.Sum(f => f.Mentions.Count), session.Summary.TotalMentions);
        Assert.Contains("Наг и Йеб", session.Summary.EntityCounts.Keys);
    }

    [Fact]
    public void Cancellation_stops_the_run()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => MakeEngine().Run(cts.Token));
    }

    [Fact]
    public void Progress_reports_every_file()
    {
        var seen = new List<string>();
        var progress = new SynchronousProgress(p => seen.Add(p.CurrentFile));
        MakeEngine().Run(CancellationToken.None, progress);

        Assert.Contains("utf8.txt", seen);
        Assert.Contains("empty.txt", seen);
    }

    [Fact]
    public void Cache_restamp_updates_nested_semantic_paths()
    {
        var oldPath = Path.Combine("old", "nested", "old.txt");
        var newPath = Path.Combine("new", "nested", "renamed.txt");
        var cached = new FileResult(
            "old.txt", oldPath, [], 0, 0, 0, 0,
            SemanticMatches:
            [
                new SemanticMatch("era-period", "Эпохи", oldPath, 7, "сниппет"),
                new SemanticMatch("forbidden-book", "Книги", oldPath, 9, "сниппет"),
            ]);

        var stamped = AnalysisEngine.StampFileIdentity(cached, newPath);

        Assert.Equal("renamed.txt", stamped.FileName);
        Assert.Equal(newPath, stamped.FullPath);
        Assert.All(stamped.SemanticMatches, s => Assert.Equal(newPath, s.FilePath));
    }

    [Fact]
    public void Unknown_sidecar_keeps_raw_rows_and_blocks_trusted_on_cache_hit()
    {
        // First run populates the content cache with detector-only metadata.
        var first = MakeEngine().Run(CancellationToken.None);
        Assert.All(first.Files, f => Assert.Equal(SourceQuality.Usable, f.SourceQuality));

        // Sidecar is added after the cache is warm: the override must still apply.
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, SourceQualitySidecar.FileName),
            "file,source_quality,source_reason,evidence_level,batch_id\n" +
            "utf8.txt,unknown,candidate_tail,not_assessed,unknown-267\n" +
            "cp1251.txt,usable,detector_negative,detector_only,none\n" +
            "empty.txt,usable,detector_negative,detector_only,none\n",
            new System.Text.UTF8Encoding(false));

        var session = MakeEngine().Run(CancellationToken.None);
        var utf8 = session.Files.Single(f => f.FileName == "utf8.txt");
        Assert.Equal(SourceQuality.Unknown, utf8.SourceQuality);
        Assert.Equal(SourceQuality.ReasonCandidateTail, utf8.SourceReason);
        Assert.Equal(3, utf8.Mentions.Count);
        Assert.True(session.Quality.CacheHits > 0, "test must exercise the cache-hit path");

        var doc = new CodexDocument();
        doc.Cards.Add(Card.Create("deity", "Наг и Йеб"));
        ReportExporter.ExportAll(session, _out, new HashSet<string> { "csv" }, codex: doc);

        var raw = File.ReadAllText(Path.Combine(_out, "entities_table.csv"));
        Assert.Equal(3, raw.Split('\n').Count(line => line.StartsWith("\"utf8.txt\";")));

        var trust = File.ReadAllText(Path.Combine(_out, "entities_trust.csv"));
        var trustRows = trust.Split('\n').Where(line => line.StartsWith("\"utf8.txt\"")).ToList();
        Assert.Equal(3, trustRows.Count);
        Assert.All(trustRows, row => Assert.Contains(",review,", row));
        Assert.Contains("trust blocked by non-usable source (unknown)", trust);
    }

    [Fact]
    public void Duplicate_representative_is_stable_and_inherits_worst_sidecar_quality()
    {
        // Same bytes under two names. The old race kept whichever worker finished
        // first; the representative must be stable and the worst copy must gate it.
        File.Copy(Path.Combine(_corpus, "utf8.txt"), Path.Combine(_corpus, "z-copy.txt"));
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, SourceQualitySidecar.FileName),
            "file,source_quality,source_reason,evidence_level,batch_id\n" +
            "utf8.txt,usable,detector_negative,detector_only,none\n" +
            "z-copy.txt,unknown,candidate_tail,not_assessed,unknown-267\n" +
            "cp1251.txt,usable,detector_negative,detector_only,none\n" +
            "empty.txt,usable,detector_negative,detector_only,none\n",
            new System.Text.UTF8Encoding(false));

        var session = MakeEngine().Run(CancellationToken.None);

        var group = Assert.Single(session.DuplicateGroups);
        Assert.Equal("utf8.txt", Path.GetFileName(group.Files[0]));
        var kept = Assert.Single(session.Files, f => f.FileName == "utf8.txt");
        Assert.Equal(3, kept.Mentions.Count);
        Assert.Equal(SourceQuality.Unknown, kept.SourceQuality);
        Assert.Equal(SourceQuality.ReasonCandidateTail, kept.SourceReason);
        Assert.DoesNotContain(session.Files, f => f.FileName == "z-copy.txt");
    }

    private sealed class SynchronousProgress(Action<AnalysisProgress> sink) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => sink(value);
    }
}
