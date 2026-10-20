using System.IO;
using System.Text.Json.Nodes;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

/// <summary>
/// Жизненный цикл дискового кэша: TTL записей, лимит объёма с вытеснением
/// самых старых, инвалидация по подписи конфига (и отключение оной),
/// уборка осиротевших файлов. Каждый тест — собственный временный каталог.
/// </summary>
public class CacheLifecycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-cache-life-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static FileResult Sample(string name = "a.txt") => new(
        name, Path.Combine("corpus", name),
        [new Mention(1, "Наг и Йеб", "Наг и Йеб", 1, "контекст", Theme: "", IsFuzzy: false)],
        TotalFound: 3, ContextSize: 60, CharCount: 1200, DensityPer10k: 25.0);

    private string[] DataFiles() =>
        Directory.Exists(_dir)
            ? Directory.GetFiles(_dir)
                .Where(f => !Path.GetFileName(f).Equals("index.json", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];

    /// <summary>Состарить запись индекса (иначе TTL-тест ждал бы месяца).</summary>
    private void SetEntryTimestamp(string key, DateTimeOffset timestamp)
    {
        var path = Path.Combine(_dir, "index.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node[key]!.AsObject()["timestamp"] = timestamp.ToString("O");
        File.WriteAllText(path, node.ToJsonString());
    }

    [Fact]
    public void Ttl_removes_expired_entries_with_their_data_files()
    {
        var cache = new JsonFileCache(_dir);
        cache.Set("old", Sample("old.txt"));
        cache.Set("fresh", Sample("fresh.txt"));
        cache.Flush();
        Assert.Equal(2, cache.Count);
        Assert.Equal(2, DataFiles().Length);

        SetEntryTimestamp("old", DateTimeOffset.UtcNow.AddDays(-40));

        var reopened = new JsonFileCache(_dir, policy: new CachePolicy(MaxAgeDays: 30));
        Assert.Equal(1, reopened.Count);
        Assert.Equal(1, reopened.LastPruneRemoved);
        Assert.True(reopened.LastPruneFreedBytes > 0, "уборка обязана освобождать байты");

        Assert.False(reopened.TryGet("old", out _));
        Assert.True(reopened.TryGet("fresh", out _));
        Assert.Single(DataFiles()); // файл данных истёкшей записи удалён, а не только запись индекса
    }

    [Fact]
    public void Ttl_zero_keeps_entries_forever()
    {
        var policy = new CachePolicy(MaxAgeDays: 0);
        var cache = new JsonFileCache(_dir, policy: policy);
        cache.Set("k", Sample());
        cache.Flush();
        SetEntryTimestamp("k", DateTimeOffset.UtcNow.AddDays(-4000));

        var reopened = new JsonFileCache(_dir, policy: policy);
        Assert.Equal(0, reopened.LastPruneRemoved);
        Assert.True(reopened.TryGet("k", out _));
    }

    [Fact]
    public void Foreign_config_entries_are_pruned_together_with_files()
    {
        var a = new JsonFileCache(_dir, configHash: "cfg-A");
        a.Set("k", Sample());
        a.Flush();
        Assert.Single(DataFiles());

        var b = new JsonFileCache(_dir, configHash: "cfg-B");
        Assert.Equal(0, b.Count);
        Assert.Empty(DataFiles());
    }

    [Fact]
    public void Foreign_entries_survive_when_invalidation_is_disabled()
    {
        // InvalidateOnConfigChange=false: записи чужих настроек не удаляются —
        // возврат к прежнему пресету не требует повторного анализа.
        var policy = new CachePolicy(InvalidateOnConfigChange: false);
        var a = new JsonFileCache(_dir, configHash: "cfg-A", policy: policy);
        a.Set("k", Sample());
        a.Flush();

        var b = new JsonFileCache(_dir, configHash: "cfg-B", policy: policy);
        Assert.Equal(1, b.Count);
        Assert.True(b.TryGet("k", out _));

        var backToA = new JsonFileCache(_dir, configHash: "cfg-A", policy: policy);
        Assert.True(backToA.TryGet("k", out _));
    }

    [Fact]
    public void Size_cap_evicts_the_oldest_entry_first()
    {
        var cache = new JsonFileCache(_dir, policy: new CachePolicy(MaxSizeBytes: CachePolicy.Unlimited));
        cache.Set("old", Sample("old.txt"));
        cache.Set("new", Sample("new.txt"));
        cache.Flush();
        SetEntryTimestamp("old", DateTimeOffset.UtcNow.AddHours(-2));
        SetEntryTimestamp("new", DateTimeOffset.UtcNow.AddHours(-1));

        // Бюджет — меньше суммы двух записей: выживет только свежая.
        var cap = DataFiles().Min(f => new FileInfo(f).Length);
        var capped = new JsonFileCache(_dir, policy: new CachePolicy(MaxSizeBytes: cap));

        Assert.Equal(1, capped.Count);
        Assert.Equal(1, capped.LastPruneRemoved);
        Assert.False(capped.TryGet("old", out _), "самая старая запись вытесняется первой");
        Assert.True(capped.TryGet("new", out _));
        Assert.Single(DataFiles());
    }

    [Fact]
    public void Orphaned_data_files_are_removed_but_fresh_tmp_is_kept()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "deadbeef.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "pending.json.tmp"), "{}");

        _ = new JsonFileCache(_dir);

        Assert.False(File.Exists(Path.Combine(_dir, "deadbeef.json")), "мусорный файл без записи в индексе удаляется");
        Assert.True(File.Exists(Path.Combine(_dir, "pending.json.tmp")), "свежий tmp может писать параллельный процесс — не трогаем");
    }

    [Fact]
    public void Legacy_index_without_size_field_is_still_readable()
    {
        var cache = new JsonFileCache(_dir);
        cache.Set("legacy", Sample());
        cache.Flush();

        // Симулируем индекс старого формата: без поля size.
        var path = Path.Combine(_dir, "index.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["legacy"]!.AsObject().Remove("size");
        File.WriteAllText(path, node.ToJsonString());

        var reopened = new JsonFileCache(_dir);
        Assert.True(reopened.TryGet("legacy", out var loaded));
        Assert.Equal("a.txt", loaded!.FileName);
        Assert.Equal(0, reopened.LastPruneRemoved);
    }
}
