using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R2-13: Хранилище вердиктов пользователя ═══════════════
// Экспертные решения о находках («подтверждено / ложное срабатывание / спорно»)
// — невозобновляемый ресурс; хранятся как append-only JSONL с ключом
// (file_hash, offset, variant). Контракт повторяет HistoryStore: переживает
// битые строки, ноль зависимостей, последний вердикт по ключу — победитель.

public class VerdictStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        "ea_verdicts_" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private static VerdictRecord Sample(
        string hash = "abc123", long offset = 42, string variant = "Наг",
        VerdictKind kind = VerdictKind.Confirmed, string note = "явный контекст") => new(
        DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
        hash, offset, variant, "Наг и Йеб", kind, note, Tag: "глава-1");

    [Fact]
    public void Append_then_find_survives_reopen()
    {
        new VerdictStore(_path).Append(Sample());

        var found = new VerdictStore(_path).Find("abc123", 42, "Наг");
        Assert.NotNull(found);
        Assert.Equal(VerdictKind.Confirmed, found!.Verdict);
        Assert.Equal("явный контекст", found.Note);
        Assert.Equal("глава-1", found.Tag);
        Assert.Equal("Наг и Йеб", found.Entity);
    }

    [Fact]
    public void Latest_verdict_wins_per_key()
    {
        var store = new VerdictStore(_path);
        store.Append(Sample(kind: VerdictKind.Confirmed));
        store.Append(Sample(kind: VerdictKind.Rejected, note: "перечень имён"));

        var found = store.Find("abc123", 42, "Наг");
        Assert.Equal(VerdictKind.Rejected, found!.Verdict);
        Assert.Equal("перечень имён", found.Note);

        // В живом индексе — одна запись на ключ.
        Assert.Single(store.All);
    }

    [Fact]
    public void Keys_are_independent()
    {
        var store = new VerdictStore(_path);
        store.Append(Sample(hash: "h1", offset: 10));
        store.Append(Sample(hash: "h2", offset: 10));
        store.Append(Sample(hash: "h1", offset: 10, variant: "Йеб"));

        Assert.Equal(3, store.All.Count);
        Assert.Equal(VerdictKind.Confirmed, store.Find("h1", 10, "Наг")!.Verdict);
        Assert.Equal(VerdictKind.Confirmed, store.Find("h2", 10, "Наг")!.Verdict);
        Assert.NotNull(store.Find("h1", 10, "Йеб"));
    }

    [Fact]
    public void Missing_verdict_returns_null()
    {
        Assert.Null(new VerdictStore(_path).Find("nope", 0, "Наг"));
    }

    [Fact]
    public void Corrupt_lines_are_skipped()
    {
        var store = new VerdictStore(_path);
        store.Append(Sample(hash: "ok1"));
        File.AppendAllText(_path, "{битая json-строка\n");
        File.AppendAllText(_path, "\n");
        store.Append(Sample(hash: "ok2"));

        var reopened = new VerdictStore(_path);
        Assert.Equal(2, reopened.All.Count);
        Assert.NotNull(reopened.Find("ok1", 42, "Наг"));
        Assert.NotNull(reopened.Find("ok2", 42, "Наг"));
    }

    [Fact]
    public void Stats_count_latest_verdicts_by_kind()
    {
        var store = new VerdictStore(_path);
        store.Append(Sample(hash: "a", kind: VerdictKind.Confirmed));
        store.Append(Sample(hash: "b", kind: VerdictKind.Rejected));
        store.Append(Sample(hash: "c", kind: VerdictKind.Uncertain));
        store.Append(Sample(hash: "b", kind: VerdictKind.Confirmed)); // перезапись b

        var stats = store.Stats();
        Assert.Equal(2, stats[VerdictKind.Confirmed]);
        Assert.Equal(0, stats[VerdictKind.Rejected]);
        Assert.Equal(1, stats[VerdictKind.Uncertain]);
    }

    [Fact]
    public void Remove_restores_unreviewed_state_and_survives_reopen()
    {
        var store = new VerdictStore(_path);
        store.Append(Sample(hash: "keep", offset: 1));
        store.Append(Sample(hash: "undo", offset: 2));

        Assert.True(store.Remove("undo", 2, "Наг"));
        Assert.Null(store.Find("undo", 2, "Наг"));
        Assert.NotNull(store.Find("keep", 1, "Наг"));

        var reopened = new VerdictStore(_path);
        Assert.Single(reopened.All);
        Assert.Null(reopened.Find("undo", 2, "Наг"));
        Assert.NotNull(reopened.Find("keep", 1, "Наг"));
    }

    [Fact]
    public void RemoveRange_removes_batch_atomically_and_survives_reopen()
    {
        var store = new VerdictStore(_path);
        store.Append(Sample(hash: "keep", offset: 1));
        store.Append(Sample(hash: "undo1", offset: 2));
        store.Append(Sample(hash: "undo2", offset: 3));

        var removed = store.RemoveRange([
            ("undo1", 2L, "Наг"),
            ("undo2", 3L, "Наг"),
            ("missing", 4L, "Наг"),
        ]);

        Assert.Equal(2, removed);
        Assert.Single(store.All);
        Assert.Null(store.Find("undo1", 2, "Наг"));
        Assert.Null(store.Find("undo2", 3, "Наг"));
        Assert.NotNull(store.Find("keep", 1, "Наг"));

        var reopened = new VerdictStore(_path);
        Assert.Single(reopened.All);
        Assert.Null(reopened.Find("undo1", 2, "Наг"));
        Assert.NotNull(reopened.Find("keep", 1, "Наг"));
    }

    [Fact]
    public void Remove_missing_key_is_noop()
    {
        var store = new VerdictStore(_path);
        store.Append(Sample());

        Assert.False(store.Remove("missing", 42, "Наг"));
        Assert.Single(store.All);
    }

    [Fact]
    public void AppendRange_writes_one_batch_and_latest_wins()
    {
        var store = new VerdictStore(_path);
        store.AppendRange([
            Sample(hash: "h1", kind: VerdictKind.Confirmed),
            Sample(hash: "h2", kind: VerdictKind.Rejected),
            Sample(hash: "h1", kind: VerdictKind.Uncertain),
        ]);

        Assert.Equal(2, store.All.Count);
        Assert.Equal(VerdictKind.Uncertain, store.Find("h1", 42, "Наг")!.Verdict);
        Assert.Equal(VerdictKind.Rejected, store.Find("h2", 42, "Наг")!.Verdict);
        Assert.Equal(3, File.ReadAllLines(_path).Length);
    }

    [Fact]
    public void Default_path_lives_in_profile_directory()
    {
        // Канонический путь: <каталог профиля>\verdicts.jsonl. Каталог профиля —
        // %APPDATA%\EntityAnalyzer, а на изолированном профиле (смоук, верификация,
        // тесты) — значение ENTITYANALYZER_DATA_DIR (см. ProfilePaths).
        Assert.Equal(
            Path.Combine(ProfilePaths.DirectoryPath, "verdicts.jsonl"),
            VerdictStore.DefaultPath);
    }

    [Fact]
    public void Enum_serializes_as_stable_string()
    {
        // Стабильность формата: в файле читаемые имена, не числа.
        new VerdictStore(_path).Append(Sample(kind: VerdictKind.Rejected));
        var line = File.ReadAllText(_path);
        Assert.Contains("\"rejected\"", line);
    }
}
