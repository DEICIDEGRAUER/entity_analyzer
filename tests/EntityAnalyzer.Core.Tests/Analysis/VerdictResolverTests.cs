using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

public class VerdictResolverTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        "ea_verdict_resolver_" + Guid.NewGuid().ToString("N") + ".jsonl");
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "ea_verdict_resolver_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Mention MentionAt(int num, long offset, string variant, int line) =>
        new(num, "Наг", variant, line, "контекст", Offset: offset);

    private static FileResult FileWith(string path, params Mention[] mentions) =>
        new(Path.GetFileName(path), path, mentions, mentions.Length, 0, 0, 0);

    private static VerdictRecord Record(
        string hash, long offset, string variant, VerdictKind kind) =>
        new(DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
            hash, offset, variant, "Наг", kind);

    [Fact]
    public void Applies_matching_verdicts_uses_line_fallback_and_clears_unknown_hash()
    {
        var store = new VerdictStore(_path);
        store.Append(Record("h1", 10, "Наг", VerdictKind.Confirmed));
        store.Append(Record("h1", 20, "Нага", VerdictKind.Rejected));

        var exact = MentionAt(1, 10, "Наг", 1);
        var byLine = MentionAt(2, 0, "Нага", 20);
        var missing = MentionAt(3, 30, "Наг", 3);
        var first = FileWith(@"C:\corpus\a.txt", exact, byLine, missing);

        var stale = MentionAt(1, 10, "Наг", 1);
        stale.Verdict = VerdictKind.Confirmed;
        var second = FileWith(@"C:\corpus\b.txt", stale);

        var hashed = new List<string>();
        var applied = VerdictResolver.ApplyTo([first, second], store, path =>
        {
            hashed.Add(path);
            return path.EndsWith("a.txt", StringComparison.Ordinal) ? "h1" : null;
        });

        Assert.Equal(2, applied);
        Assert.Equal(VerdictKind.Confirmed, exact.Verdict);
        Assert.Equal(VerdictKind.Rejected, byLine.Verdict);
        Assert.Null(missing.Verdict);
        Assert.Null(stale.Verdict);
        Assert.Equal(2, hashed.Count);
    }

    [Fact]
    public void Empty_store_clears_verdicts_without_hashing_files()
    {
        var mention = MentionAt(1, 10, "Наг", 1);
        mention.Verdict = VerdictKind.Confirmed;
        var file = FileWith(@"C:\corpus\a.txt", mention);
        var hashCalls = 0;

        var applied = VerdictResolver.ApplyTo([file], new VerdictStore(_path), _ =>
        {
            hashCalls++;
            return "h1";
        });

        Assert.Equal(0, applied);
        Assert.Null(mention.Verdict);
        Assert.Equal(0, hashCalls);
    }

    [Fact]
    public void Files_without_mentions_are_not_hashed()
    {
        var store = new VerdictStore(_path);
        store.Append(Record("h1", 10, "Наг", VerdictKind.Confirmed));
        var empty = FileWith(@"C:\corpus\empty.txt");
        var mentioned = FileWith(@"C:\corpus\a.txt", MentionAt(1, 10, "Наг", 1));
        var hashed = new List<string>();

        var applied = VerdictResolver.ApplyTo([empty, mentioned], store, path =>
        {
            hashed.Add(path);
            return "h1";
        });

        Assert.Equal(1, applied);
        Assert.Contains(@"C:\corpus\a.txt", hashed);
        Assert.DoesNotContain(@"C:\corpus\empty.txt", hashed);
    }

    private static AnalysisSession SessionWith(string path, params Mention[] mentions) => new(
        Files: [FileWith(path, mentions)],
        Summary: new AnalysisSummary(mentions.Length, 1, 1, 100,
            new Dictionary<string, int> { ["Наг"] = mentions.Length }),
        Quality: new QualityReport(0, 0, 0, 0, 0, 0, mentions.Length, 0, 0, 0,
            Array.Empty<(string, string)>(), "0s"),
        PresetName: "extended");

    [Fact]
    public void ApplyStoredTo_uses_hash_from_incremental_index()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "Наг и Йеб пришли.");
        var hash = VerdictResolver.HashFile(file);
        Assert.NotNull(hash);

        var indexPath = Path.Combine(_dir, "incremental.json");
        var index = new IncrementalIndexStore(indexPath);
        index.StoreHash(file, hash!);
        index.Flush();

        var store = new VerdictStore(Path.Combine(_dir, "verdicts.jsonl"));
        store.Append(Record(hash!, 7, "Наг", VerdictKind.Confirmed));
        var mention = MentionAt(1, 7, "Наг", 1);
        var session = SessionWith(file, mention);

        var applied = VerdictResolver.ApplyStoredTo(session, store,
            new IncrementalIndexStore(indexPath));

        Assert.Equal(1, applied);
        Assert.Equal(VerdictKind.Confirmed, mention.Verdict);
    }

    [Fact]
    public void ApplyStoredTo_falls_back_to_reading_file_without_index()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "b.txt");
        File.WriteAllText(file, "Наг и Йеб пришли.");
        var hash = VerdictResolver.HashFile(file);
        Assert.NotNull(hash);

        var store = new VerdictStore(Path.Combine(_dir, "verdicts.jsonl"));
        store.Append(Record(hash!, 3, "Йеб", VerdictKind.Rejected));
        var mention = MentionAt(1, 3, "Йеб", 1);
        var session = SessionWith(file, mention);

        var applied = VerdictResolver.ApplyStoredTo(session, store, index: null);

        Assert.Equal(1, applied);
        Assert.Equal(VerdictKind.Rejected, mention.Verdict);
    }

    [Fact]
    public void ApplyStoredTo_empty_store_does_not_touch_missing_files()
    {
        var mention = MentionAt(1, 7, "Наг", 1);
        mention.Verdict = VerdictKind.Confirmed;
        var session = SessionWith(Path.Combine(_dir, "missing.txt"), mention);

        var applied = VerdictResolver.ApplyStoredTo(session,
            new VerdictStore(Path.Combine(_dir, "verdicts.jsonl")), index: null);

        Assert.Equal(0, applied);
        Assert.Null(mention.Verdict);
    }
}
