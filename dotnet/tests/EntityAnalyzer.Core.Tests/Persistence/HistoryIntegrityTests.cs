using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R1-21: Цепочка целостности журнала ═══════════════
// history.jsonl становится тампер-евидентным: каждая запись несёт
// prev_hash и собственный хеш (SHA-256 канонического содержимого записи +
// предыдущего хеша). Тихая правка истории прогонов становится обнаружимой.
// Старые записи без хешей легальны — цепочка начинается с первой новой.

public class HistoryIntegrityTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        "ea_hist_" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private static RunRecord Record(string preset = "light", int mentions = 10) => new()
    {
        StartedAt = DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
        Preset = preset,
        TotalMentions = mentions,
        Status = "Успешно",
    };

    [Fact]
    public void Append_builds_chained_hashes()
    {
        var store = new HistoryStore(_path);
        store.Append(Record(mentions: 1));
        store.Append(Record(mentions: 2));

        var records = store.ReadLast(10);
        Assert.Equal(2, records.Count);

        Assert.NotNull(records[0].Hash);
        Assert.Equal(JournalIntegrity.Genesis, records[0].PrevHash);

        // Вторая запись ссылается на хеш первой — разорвать цепь тихо нельзя.
        Assert.Equal(records[0].Hash, records[1].PrevHash);
        Assert.NotEqual(records[0].Hash, records[1].Hash);
    }

    [Fact]
    public void Verify_clean_chain_is_silent()
    {
        var store = new HistoryStore(_path);
        store.Append(Record());
        store.Append(Record());
        store.Append(Record());

        var problems = JournalIntegrity.Verify(store.ReadLast(10));
        Assert.Empty(problems);
    }

    [Fact]
    public void Verify_detects_tampered_record()
    {
        var store = new HistoryStore(_path);
        store.Append(Record(mentions: 100));
        store.Append(Record(mentions: 200));
        store.Append(Record(mentions: 300));

        // «Кто-то» приписал себе впечатляющий результат в середине журнала.
        var records = store.ReadLast(10).ToList();
        records[1].TotalMentions = 9999; // RunRecord — мутируемый класс, как и в реальной правке

        var problems = JournalIntegrity.Verify(records);
        Assert.Single(problems);
        Assert.Contains("2", problems[0]); // номер строки в сообщении
    }

    [Fact]
    public void Verify_detects_removed_record()
    {
        var store = new HistoryStore(_path);
        store.Append(Record());
        store.Append(Record());
        store.Append(Record());

        // Выброшена средняя запись: следующая ссылается на чужой prev_hash.
        var records = store.ReadLast(10).Where((_, i) => i != 1).ToList();

        Assert.Single(JournalIntegrity.Verify(records));
    }

    [Fact]
    public void Legacy_records_without_hashes_are_tolerated()
    {
        // Журнал, писанный старой версией (без хешей), затем новая запись.
        var legacy = System.Text.Json.JsonSerializer.Serialize(new
        {
            startedAt = "2026-01-01T00:00:00Z",
            preset = "light",
            totalMentions = 5,
            status = "Успешно",
        });
        File.WriteAllText(_path, legacy + "\n");

        var store = new HistoryStore(_path);
        store.Append(Record());

        var problems = JournalIntegrity.Verify(store.ReadLast(10));
        Assert.Empty(problems); // легаси пропускается, новая запись проверяется

        var appended = store.ReadLast(10).Last();
        Assert.NotNull(appended.Hash);
        Assert.Equal(JournalIntegrity.Genesis, appended.PrevHash); // цепь начата с генезиса
    }

    [Fact]
    public void Hash_changes_when_content_changes()
    {
        // Санити: разные данные — разные хеши (иначе вся затея бессмысленна).
        var h1 = JournalIntegrity.ComputeHash(Record(mentions: 1), JournalIntegrity.Genesis);
        var h2 = JournalIntegrity.ComputeHash(Record(mentions: 2), JournalIntegrity.Genesis);
        Assert.NotEqual(h1, h2);

        // И предыдущий хеш участвует: одинаковые записи в разных местах цепи различимы.
        var h3 = JournalIntegrity.ComputeHash(Record(mentions: 1), "abcdef");
        Assert.NotEqual(h1, h3);
    }
}
