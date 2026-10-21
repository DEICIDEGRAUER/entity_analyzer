using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

public class VerdictResolverTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        "ea_verdict_resolver_" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
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
}
