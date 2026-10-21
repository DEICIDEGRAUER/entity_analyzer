using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

/// <summary>
/// Обслуживание каталога кэша для GUI: сводка (записи, объём, осиротевшие
/// файлы, диапазон дат) и полная очистка. Сбои обязаны деградировать в нулевую
/// статистику, а не в исключение — кнопка «Очистить» работает всегда.
/// Каждый тест — собственный временный каталог.
/// </summary>
public class CacheMaintenanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-cache-maint-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static FileResult Sample(string name = "a.txt") => new(
        name, Path.Combine("corpus", name),
        [new Mention(1, "Наг", "Наг", 1, "контекст")],
        TotalFound: 1, ContextSize: 60, CharCount: 100, DensityPer10k: 10.0);

    [Fact]
    public void Missing_directory_reports_empty_stats()
    {
        var stats = CacheMaintenance.Inspect(_dir);

        Assert.Equal(0, stats.Entries);
        Assert.Equal(0, stats.FilesCount);
        Assert.Equal(0, stats.FilesBytes);
        Assert.Equal(0, stats.OrphanFiles);
        Assert.Equal(0, stats.OrphanBytes);
        Assert.Null(stats.Oldest);
        Assert.Null(stats.Newest);
    }

    [Fact]
    public void Inspect_counts_entries_files_and_orphans()
    {
        var cache = new JsonFileCache(_dir);
        cache.Set("one", Sample("one.txt"));
        cache.Set("two", Sample("two.txt"));
        cache.Flush();
        File.WriteAllText(Path.Combine(_dir, "orphan.json"), new string('x', 128));

        var stats = CacheMaintenance.Inspect(_dir);

        Assert.Equal(2, stats.Entries);
        Assert.Equal(4, stats.FilesCount); // index.json + 2 data-файла + осиротевший
        Assert.Equal(1, stats.OrphanFiles);
        Assert.Equal(128, stats.OrphanBytes);
        Assert.True(stats.FilesBytes > stats.OrphanBytes);
        Assert.NotNull(stats.Oldest);
        Assert.NotNull(stats.Newest);
        Assert.True(stats.Newest >= stats.Oldest);
    }

    [Fact]
    public void Corrupt_index_degrades_to_orphan_counting()
    {
        var cache = new JsonFileCache(_dir);
        cache.Set("k", Sample());
        cache.Flush();
        File.WriteAllText(Path.Combine(_dir, "index.json"), "{ это не json");

        var stats = CacheMaintenance.Inspect(_dir);

        Assert.Equal(0, stats.Entries);     // индекс нечитаем — записей для UI нет
        Assert.Equal(1, stats.OrphanFiles); // его data-файл показывается осиротевшим
        Assert.Null(stats.Oldest);
    }

    [Fact]
    public void Clear_removes_all_files_and_reports_freed_bytes()
    {
        var cache = new JsonFileCache(_dir);
        cache.Set("k", Sample());
        cache.Flush();
        File.WriteAllText(Path.Combine(_dir, "stale.json"), new string('y', 64));
        var before = Directory.GetFiles(_dir).Sum(f => new FileInfo(f).Length);

        var freed = CacheMaintenance.Clear(_dir);

        Assert.Equal(before, freed);
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.True(Directory.Exists(_dir));          // каталог остаётся: движок пишет в него как раньше
        Assert.Equal(0, CacheMaintenance.Clear(_dir)); // повторная очистка — чистить нечего
        Assert.Equal(0, CacheMaintenance.Clear(_dir + "-missing")); // несуществующий каталог — не исключение
    }
}
