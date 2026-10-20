using System.IO;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

/// <summary>
/// Отклонения кандидатов каталога (item 8): append-only JSONL, действует
/// последний вердикт по ключу «сущность|написание», сброс возвращает
/// кандидата в список. Каждый тест — собственный временный каталог.
/// </summary>
public class CatalogIgnoreStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-catalog-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "catalog_ignored.jsonl");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Ignore_survives_reload_and_lists_active()
    {
        var s1 = new CatalogIgnoreStore(StorePath);
        s1.Set("Азатот", "Azatot", CatalogIgnoreStore.Ignored);

        var s2 = new CatalogIgnoreStore(StorePath);
        Assert.True(s2.IsIgnored("Азатот", "Azatot"));
        Assert.False(s2.IsIgnored("Азатот", "Азатотъ")); // другой кандидат — не задет
        Assert.Single(s2.AllActive());
        Assert.Equal(CatalogIgnoreStore.Ignored, s2.AllActive().Values.Single());
    }

    [Fact]
    public void Clear_returns_candidate_to_the_list()
    {
        var store = new CatalogIgnoreStore(StorePath);
        store.Set("Наг", "Нааг", CatalogIgnoreStore.Ignored);
        store.Clear("Наг", "Нааг");

        Assert.False(store.IsIgnored("Наг", "Нааг"));
        Assert.Empty(store.AllActive());
    }

    [Fact]
    public void Broken_lines_are_skipped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath,
            "не json вовсе\n" +
            "{\"ts\":\"2026-10-19T00:00:00+03:00\",\"key\":\"Наг|Нааг\",\"entity\":\"Наг\",\"spelling\":\"Нааг\",\"verdict\":\"ignored\"}\n" +
            "{битая строка}\n");

        var store = new CatalogIgnoreStore(StorePath);
        Assert.True(store.IsIgnored("Наг", "Нааг"));
        Assert.Single(store.AllActive());
    }

    [Fact]
    public void Empty_inputs_are_ignored()
    {
        var store = new CatalogIgnoreStore(StorePath);
        store.Set("", "Нааг", CatalogIgnoreStore.Ignored);
        store.Set("Наг", "", CatalogIgnoreStore.Ignored);

        Assert.Empty(store.AllActive());
    }
}
