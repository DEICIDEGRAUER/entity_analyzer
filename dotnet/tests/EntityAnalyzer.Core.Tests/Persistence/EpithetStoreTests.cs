using System.IO;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

/// <summary>
/// Вердикты словаря эпитетов (item 23): append-only JSONL, действует
/// последний вердикт по ключу «сущность|эпитет», сброс возвращает нейтраль.
/// Каждый тест — собственный временный каталог.
/// </summary>
public class EpithetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-epithets-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "epithets.jsonl");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Verdict_survives_reload()
    {
        var s1 = new EpithetStore(StorePath);
        s1.Set("Азатот", "зловещий", EpithetStore.Confirmed);

        var s2 = new EpithetStore(StorePath);
        Assert.Equal(EpithetStore.Confirmed, s2.Find("Азатот", "зловещий"));
        Assert.Single(s2.AllActive());
    }

    [Fact]
    public void Last_verdict_wins()
    {
        var store = new EpithetStore(StorePath);
        store.Set("Азатот", "зловещий", EpithetStore.Confirmed);
        store.Set("Азатот", "зловещий", EpithetStore.Rejected);

        Assert.Equal(EpithetStore.Rejected, store.Find("Азатот", "зловещий"));
    }

    [Fact]
    public void Clear_returns_neutral_and_removes_from_active()
    {
        var store = new EpithetStore(StorePath);
        store.Set("Азатот", "зловещий", EpithetStore.Rejected);
        store.Clear("Азатот", "зловещий");

        Assert.Equal("", store.Find("Азатот", "зловещий"));
        Assert.Empty(store.AllActive());
    }

    [Fact]
    public void Broken_lines_are_skipped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath,
            "не json вовсе\n" +
            "{\"ts\":\"2026-10-19T00:00:00+03:00\",\"key\":\"Наг|древний\",\"entity\":\"Наг\",\"epithet\":\"древний\",\"verdict\":\"confirmed\"}\n" +
            "{битая строка}\n");

        var store = new EpithetStore(StorePath);
        Assert.Equal(EpithetStore.Confirmed, store.Find("Наг", "древний"));
        Assert.Single(store.AllActive());
    }

    [Fact]
    public void Empty_inputs_are_ignored()
    {
        var store = new EpithetStore(StorePath);
        store.Set("", "древний", EpithetStore.Confirmed);
        store.Set("Наг", "", EpithetStore.Confirmed);

        Assert.Empty(store.AllActive());
    }
}
