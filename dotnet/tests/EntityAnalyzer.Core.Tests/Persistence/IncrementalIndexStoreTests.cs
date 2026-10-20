using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

/// <summary>
/// Инкрементальный индекс «путь → (размер, mtime, SHA-256)»: неизменённые файлы
/// не перечитываются, изменённые хэшируются заново, записи исчезнувших файлов
/// вычищаются при открытии (иначе индекс путей копил бы мёртвые записи годами).
/// </summary>
public class IncrementalIndexStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-inc-" + Guid.NewGuid().ToString("N"));
    private readonly string _indexPath;

    public IncrementalIndexStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _indexPath = Path.Combine(_dir, "incremental.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Unchanged_file_reuses_hash_across_instances()
    {
        var file = Path.Combine(_dir, "f.txt");
        File.WriteAllText(file, "первая версия текста");

        var calls = 0;
        string Compute() { calls++; return "hash-" + calls; }

        var store = new IncrementalIndexStore(_indexPath);
        Assert.Equal("hash-1", store.GetOrComputeHash(file, Compute));
        Assert.Equal("hash-1", store.GetOrComputeHash(file, Compute));
        Assert.Equal(1, calls);
        store.Flush();

        // Новый экземпляр читает индекс с диска: файл снова не читается.
        var reopened = new IncrementalIndexStore(_indexPath);
        Assert.Equal("hash-1", reopened.GetOrComputeHash(file, Compute));
        Assert.Equal(1, calls);
        Assert.Equal(1, reopened.Count);
    }

    [Fact]
    public void Changed_timestamp_forces_recompute()
    {
        var file = Path.Combine(_dir, "f.txt");
        File.WriteAllText(file, "текст");
        var store = new IncrementalIndexStore(_indexPath);
        var calls = 0;
        string Compute() { calls++; return "hash-" + calls; }

        Assert.Equal("hash-1", store.GetOrComputeHash(file, Compute));

        // Размер тот же, время записи другое → запись индекса недействительна.
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(10));
        Assert.Equal("hash-2", store.GetOrComputeHash(file, Compute));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Changed_size_forces_recompute()
    {
        var file = Path.Combine(_dir, "f.txt");
        File.WriteAllText(file, "коротко");
        var store = new IncrementalIndexStore(_indexPath);
        var calls = 0;
        string Compute() { calls++; return "hash-" + calls; }

        Assert.Equal("hash-1", store.GetOrComputeHash(file, Compute));
        File.WriteAllText(file, "текст стал заметно длиннее прежнего");
        Assert.Equal("hash-2", store.GetOrComputeHash(file, Compute));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Vanished_files_are_pruned_on_open()
    {
        var file1 = Path.Combine(_dir, "keep.txt");
        var file2 = Path.Combine(_dir, "gone.txt");
        File.WriteAllText(file1, "остаётся");
        File.WriteAllText(file2, "исчезнет");

        var store = new IncrementalIndexStore(_indexPath);
        Assert.Equal("h1", store.GetOrComputeHash(file1, () => "h1"));
        Assert.Equal("h2", store.GetOrComputeHash(file2, () => "h2"));
        store.Flush();
        Assert.Equal(2, store.Count);

        File.Delete(file2);

        var reopened = new IncrementalIndexStore(_indexPath);
        Assert.Equal(1, reopened.Count); // мёртвая запись вычищена сразу при открытии

        var calls = 0;
        Assert.Equal("h1", reopened.GetOrComputeHash(file1, () => { calls++; return "заново"; }));
        Assert.Equal(0, calls); // живая запись уборкой не затронута
    }

    [Fact]
    public void Missing_file_delegates_to_compute_without_indexing()
    {
        var store = new IncrementalIndexStore(_indexPath);
        var missing = Path.Combine(_dir, "нет-такого.txt");

        Assert.Equal("h", store.GetOrComputeHash(missing, () => "h"));
        Assert.Equal(0, store.Count); // в индексе появляются только реально прочитанные файлы

        File.WriteAllText(missing, "теперь есть");
        Assert.Equal("h2", store.GetOrComputeHash(missing, () => "h2"));
        Assert.Equal(1, store.Count);
    }
}
