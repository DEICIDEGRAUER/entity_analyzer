using System.Collections.Concurrent;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

/// <summary>
/// L2-кэш результатов в памяти: попадание без обращения к диску, чтение с диска
/// с прогревом, LRU-вытеснение по бюджету, изоляция и очистка неймспейсов
/// (каталог кэша), потокобезопасность. Тесты — в коллекции LogSequential:
/// статическое состояние L2 общее на процесс, параллельный запуск исказил бы
/// счётчики и вытеснение.
/// </summary>
[Collection("LogSequential")]
public class MemoryCacheLayerTests : IDisposable
{
    private const long Budget = 32L * 1024 * 1024;

    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { MemoryCacheLayer.Clear(dir); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ea-l2-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        return dir;
    }

    private static FileResult Sample(string name = "a.txt") => new(
        name, Path.Combine("corpus", name),
        [new Mention(1, "Наг и Йеб", "Наг и Йеб", 1, "контекст упоминания для оценки объёма", Theme: "", IsFuzzy: false)],
        TotalFound: 3, ContextSize: 60, CharCount: 1200, DensityPer10k: 25.0);

    /// <summary>Дисковый слой-заглушка: считает обращения, хранит записи в словаре.</summary>
    private sealed class StubCache : IAnalysisCache
    {
        private readonly ConcurrentDictionary<string, FileResult> _store = new();
        private long _gets;
        private long _sets;

        public long GetCalls => Interlocked.Read(ref _gets);
        public long SetCalls => Interlocked.Read(ref _sets);

        public void Put(string key, FileResult result) => _store[key] = result;

        public bool TryGet(string key, out FileResult? result)
        {
            Interlocked.Increment(ref _gets);
            var hit = _store.TryGetValue(key, out var stored);
            result = stored;
            return hit;
        }

        public void Set(string key, FileResult result)
        {
            Interlocked.Increment(ref _sets);
            _store[key] = result;
        }

        public void Flush() { }
    }

    [Fact]
    public void Memory_hit_does_not_touch_disk()
    {
        var disk = new StubCache();
        var layer = MemoryCacheLayer.Wrap(NewDir(), "cfg", disk, Budget);
        var sample = Sample();

        layer.Set("k", sample);
        Assert.True(layer.TryGet("k", out var got));

        Assert.Same(sample, got);
        Assert.Equal(1, layer.HitCount);
        Assert.Equal(0, layer.MissCount);
        Assert.Equal(0, disk.GetCalls);  // при попадании в память диск не читается вовсе
        Assert.Equal(1, disk.SetCalls);  // запись идёт в оба слоя
    }

    [Fact]
    public void Disk_hit_promotes_result_into_memory()
    {
        var disk = new StubCache();
        var sample = Sample();
        disk.Put("k", sample);
        var layer = MemoryCacheLayer.Wrap(NewDir(), "cfg", disk, Budget);

        Assert.True(layer.TryGet("k", out var first)); // промах памяти → диск
        Assert.Same(sample, first);
        Assert.Equal(0, layer.HitCount);
        Assert.Equal(1, layer.MissCount);
        Assert.Equal(1, disk.GetCalls);

        Assert.True(layer.TryGet("k", out var second)); // повтор — уже из памяти
        Assert.Same(sample, second);
        Assert.Equal(1, layer.HitCount);
        Assert.Equal(1, layer.MissCount); // счётчик промахов не вырос
        Assert.Equal(1, disk.GetCalls);
    }

    [Fact]
    public void Missing_everywhere_reports_miss()
    {
        var layer = MemoryCacheLayer.Wrap(NewDir(), "cfg", new StubCache(), Budget);

        Assert.False(layer.TryGet("nope", out var result));
        Assert.Null(result);
        Assert.Equal(0, layer.HitCount);
        Assert.Equal(1, layer.MissCount);
    }

    [Fact]
    public void Lru_tail_is_evicted_when_budget_is_exceeded()
    {
        var disk = new StubCache();
        var a = Sample("a.txt");
        var b = Sample("b.txt");
        // Бюджет тянет одну запись: при добавлении свежей старая вытесняется.
        var layer = MemoryCacheLayer.Wrap(NewDir(), "cfg", disk,
            MemoryCacheLayer.EstimateSize(a) + 1);
        disk.Put("a", a);
        disk.Put("b", b);

        layer.Set("a", a);
        layer.Set("b", b);

        Assert.True(layer.TryGet("b", out _)); // свежая осталась в памяти
        Assert.Equal(1, layer.HitCount);
        Assert.Equal(0, disk.GetCalls);

        Assert.True(layer.TryGet("a", out var fromDisk)); // вытесненная идёт на диск
        Assert.Same(a, fromDisk);
        Assert.Equal(1, layer.MissCount);
        Assert.Equal(1, disk.GetCalls);
    }

    [Fact]
    public void Clear_empties_only_the_targeted_namespace()
    {
        var dirA = NewDir();
        var dirB = NewDir();
        var disk = new StubCache();
        var layerA = MemoryCacheLayer.Wrap(dirA, "cfg", disk, Budget);
        var layerB = MemoryCacheLayer.Wrap(dirB, "cfg", disk, Budget);
        layerA.Set("k", Sample("a.txt"));
        layerB.Set("k", Sample("b.txt"));

        Assert.Equal(1, MemoryCacheLayer.Clear(dirA));
        Assert.Equal(0, MemoryCacheLayer.Clear(dirA)); // повторно чистить нечего

        // Память каталога A пуста — запрос честно уходит на диск (Set пишет в оба слоя).
        Assert.True(layerA.TryGet("k", out _));
        Assert.Equal(1, layerA.MissCount);
        Assert.Equal(0, layerA.HitCount);

        Assert.True(layerB.TryGet("k", out _)); // соседний неймспейс не тронут
        Assert.Equal(1, layerB.HitCount);
        Assert.Equal(0, layerB.MissCount);
    }

    [Fact]
    public void ClearAll_wipes_entries_and_resets_counters()
    {
        var layer = MemoryCacheLayer.Wrap(NewDir(), "cfg", new StubCache(), Budget);
        layer.Set("k", Sample());
        Assert.True(layer.TryGet("k", out _));
        Assert.True(MemoryCacheLayer.Stats.Entries >= 1);

        MemoryCacheLayer.ClearAll();

        Assert.Equal(0, MemoryCacheLayer.Stats.Entries);
        Assert.Equal(0, MemoryCacheLayer.Stats.Bytes);
        Assert.Equal(0, layer.HitCount);
        Assert.Equal(0, layer.MissCount);

        // Слой остаётся рабочим: следующий запрос уходит на диск (заглушка хранит запись).
        Assert.True(layer.TryGet("k", out _));
        Assert.Equal(1, layer.MissCount);
        Assert.Equal(0, layer.HitCount);
    }

    [Fact]
    public void Wrap_returns_same_layer_for_same_namespace_and_config()
    {
        var dir = NewDir();

        var first = MemoryCacheLayer.Wrap(dir, "cfg-A", new StubCache(), Budget);
        var again = MemoryCacheLayer.Wrap(dir, "cfg-A", new StubCache(), Budget);
        Assert.Same(first, again); // повторные прогоны видят один и тот же L2

        // Смена подписи конфига или каталога — отдельные слои.
        Assert.NotSame(first, MemoryCacheLayer.Wrap(dir, "cfg-B", new StubCache(), Budget));
        Assert.NotSame(first, MemoryCacheLayer.Wrap(NewDir(), "cfg-A", new StubCache(), Budget));
    }

    [Fact]
    public void Concurrent_access_is_thread_safe()
    {
        var layer = MemoryCacheLayer.Wrap(NewDir(), "cfg", new StubCache(), Budget);

        // 200 итераций по 20 ключам: каждый Set проверяется тем же потоком,
        // бюджет заведомо не ограничивает — все обращения обязаны быть попаданиями.
        Parallel.For(0, 200, i =>
        {
            var key = "k" + (i % 20);
            layer.Set(key, Sample($"f{i % 20}.txt"));
            Assert.True(layer.TryGet(key, out _));
        });

        Assert.Equal(200, layer.HitCount);
        Assert.Equal(0, layer.MissCount);
    }
}
