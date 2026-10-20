using System.IO;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

/// <summary>
/// Глоссарий (item 25): append-only JSONL, действует последняя запись
/// по слову; определение обновляется, сброс возвращает нейтральный статус.
/// Каждый тест — собственный временный каталог.
/// </summary>
public class GlossaryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-glossary-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "glossary.jsonl");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Term_and_definition_survive_reload()
    {
        var s1 = new GlossaryStore(StorePath);
        s1.Set("Некрономикон", "Легендарная книга", GlossaryStore.Confirmed);

        var s2 = new GlossaryStore(StorePath);
        var rec = s2.Get("Некрономикон");

        Assert.NotNull(rec);
        Assert.Equal("Легендарная книга", rec!.Definition);
        Assert.Equal(GlossaryStore.Confirmed, rec.Status);
        Assert.Single(s2.AllActive());
    }

    [Fact]
    public void Definition_update_keeps_latest()
    {
        var store = new GlossaryStore(StorePath);
        store.Set("Некрономикон", "Черновик", GlossaryStore.Confirmed);
        store.Set("Некрономикон", "Уточнённое определение", GlossaryStore.Confirmed);

        Assert.Equal("Уточнённое определение", store.Get("Некрономикон")!.Definition);
    }

    [Fact]
    public void Clear_returns_neutral_status()
    {
        var store = new GlossaryStore(StorePath);
        store.Set("Некрономикон", "", GlossaryStore.Rejected);
        store.Set("Некрономикон", "", "");

        var rec = store.Get("Некрономикон");
        Assert.NotNull(rec);
        Assert.Equal("", rec!.Status);
        Assert.Empty(store.AllActive());
    }

    [Fact]
    public void Broken_lines_are_skipped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath,
            "не json вовсе\n" +
            "{\"ts\":\"2026-10-19T00:00:00+03:00\",\"word\":\"Некрономикон\",\"definition\":\"Книга\",\"status\":\"confirmed\"}\n" +
            "{битая строка}\n");

        var store = new GlossaryStore(StorePath);

        Assert.Equal("Книга", store.Get("Некрономикон")!.Definition);
        Assert.Single(store.AllActive());
    }

    [Fact]
    public void Empty_word_is_ignored()
    {
        var store = new GlossaryStore(StorePath);
        store.Set("   ", "мусор", GlossaryStore.Confirmed);

        Assert.Empty(store.AllActive());
    }
}
