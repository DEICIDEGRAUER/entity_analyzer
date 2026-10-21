using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// L2-кэш результатов: оперативный слой в памяти процесса поверх дискового
/// <see cref="IAnalysisCache"/>. В рамках одного процесса повторные обращения
/// (watch-режим GUI, перезапуск анализа после отмены, headless-сервер с
/// несколькими /analyze, повторные прогоны по неизменённому корпусу) отдаются
/// из RAM — без чтения файлов кэша и без десериализации JSON.
///
/// Устройство: хранилище в памяти на каждый каталог кэша (неймспейс) с
/// LRU-вытеснением по оценочному объёму. Ключ записи уже включает подпись
/// конфигурации (формируется движком), поэтому смешение настроек невозможно
/// по построению, а смена пресета не выбивает из памяти чужие результаты —
/// они просто не запрашиваются. Экземпляры слоя выдаёт <see cref="Wrap"/> —
/// один на пару «каталог кэша + подпись конфига»; счётчики попаданий/промахов
/// живут на экземпляре, движок читает их до и после прогона (дельта = метрика).
/// Все операции потокобезопасны: движок зовёт кэш из параллельного цикла.
/// </summary>
public sealed class MemoryCacheLayer : IAnalysisCache
{
    private sealed record Item(string Key, FileResult Result, long Size);

    /// <summary>LRU-хранилище одного каталога кэша со своим бюджетом.</summary>
    private sealed class NsStore
    {
        public readonly Dictionary<string, LinkedListNode<Item>> Map = new(StringComparer.Ordinal);
        public readonly LinkedList<Item> Lru = []; // первый — самый свежий
        public long UsedBytes;
        public long BudgetBytes = 256L * 1024 * 1024;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, NsStore> Stores = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MemoryCacheLayer> Layers = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _ns;
    private readonly IAnalysisCache _disk;
    private long _hits;
    private long _misses;

    /// <summary>Попадания уровня памяти (потокобезопасно).</summary>
    public long HitCount => Interlocked.Read(ref _hits);

    /// <summary>Промахи уровня памяти (запросы, ушедшие на диск).</summary>
    public long MissCount => Interlocked.Read(ref _misses);

    private MemoryCacheLayer(string ns, IAnalysisCache disk)
    {
        _ns = ns;
        _disk = disk;
    }

    /// <summary>
    /// Слой для пары «каталог кэша + подпись конфига»: повторные вызовы с теми
    /// же параметрами возвращают тот же экземпляр, поэтому L2 переживает
    /// отдельные прогоны. Бюджет задаётся на каталог (последний вызов — выигрывает).
    /// </summary>
    public static MemoryCacheLayer Wrap(string cacheDir, string? configHash, IAnalysisCache disk, long budgetBytes)
    {
        var ns = NormalizeDir(cacheDir);
        var registryKey = ns + "\u0001" + (configHash ?? "");
        lock (Gate)
        {
            var store = GetStore(ns);
            if (budgetBytes > 0) store.BudgetBytes = budgetBytes;

            if (Layers.TryGetValue(registryKey, out var existing)) return existing;

            var layer = new MemoryCacheLayer(ns, disk);
            Layers[registryKey] = layer;
            return layer;
        }
    }

    public bool TryGet(string key, out FileResult? result)
    {
        var store = GetStore(_ns);
        lock (Gate)
        {
            if (store.Map.TryGetValue(key, out var node))
            {
                store.Lru.Remove(node);
                store.Lru.AddFirst(node);
                Interlocked.Increment(ref _hits);
                result = node.Value.Result;
                return true;
            }
        }

        Interlocked.Increment(ref _misses);
        if (_disk.TryGet(key, out result))
        {
            if (result is not null) Promote(store, key, result);
            return true;
        }
        return false;
    }

    public void Set(string key, FileResult result)
    {
        Promote(GetStore(_ns), key, result);
        _disk.Set(key, result);
    }

    public void Flush() => _disk.Flush();

    /// <summary>Кладёт результат в память и вытесняет LRU-хвост за бюджет.</summary>
    private static void Promote(NsStore store, string key, FileResult result)
    {
        var item = new Item(key, result, EstimateSize(result));
        lock (Gate)
        {
            if (store.Map.TryGetValue(key, out var existing))
            {
                store.UsedBytes -= existing.Value.Size;
                store.Lru.Remove(existing);
            }
            var node = store.Lru.AddFirst(item);
            store.Map[key] = node;
            store.UsedBytes += item.Size;

            while (store.UsedBytes > store.BudgetBytes && store.Lru.Last is { } tail)
            {
                store.Lru.RemoveLast();
                store.Map.Remove(tail.Value.Key);
                store.UsedBytes -= tail.Value.Size;
            }
            if (store.UsedBytes < 0) store.UsedBytes = 0;
        }
    }

    private static NsStore GetStore(string ns)
    {
        lock (Gate)
        {
            if (!Stores.TryGetValue(ns, out var store))
                Stores[ns] = store = new NsStore();
            return store;
        }
    }

    private static string NormalizeDir(string dir)
    {
        try { return Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return dir; }
    }

    /// <summary>Состояние L2 в целом по процессу (для GUI и журнала).</summary>
    public static (int Entries, long Bytes) Stats
    {
        get
        {
            lock (Gate)
                return (Stores.Values.Sum(s => s.Map.Count), Stores.Values.Sum(s => s.UsedBytes));
        }
    }

    /// <summary>Очистить L2 для одного каталога кэша. Возвращает число удалённых записей.</summary>
    public static int Clear(string cacheDir)
    {
        var ns = NormalizeDir(cacheDir);
        lock (Gate)
        {
            if (!Stores.TryGetValue(ns, out var store)) return 0;
            var removed = store.Map.Count;
            Stores.Remove(ns);
            return removed;
        }
    }

    /// <summary>Полная очистка L2 (кнопка «Очистить кэш» в GUI). Счётчики слоёв сбрасываются.</summary>
    public static void ClearAll()
    {
        lock (Gate)
        {
            Stores.Clear();
            foreach (var layer in Layers.Values)
            {
                Interlocked.Exchange(ref layer._hits, 0);
                Interlocked.Exchange(ref layer._misses, 0);
            }
        }
    }

    /// <summary>
    /// Оценка объёма результата в памяти: каркас записи + строки контекстов и
    /// метаданных. Точность не нужна — важна пропорциональность (контекст
    /// доминирует), чтобы LRU честно вытеснял тяжёлые записи.
    /// </summary>
    internal static long EstimateSize(FileResult r)
    {
        long n = 256 + (r.FileName.Length + r.FullPath.Length) * 2L;
        foreach (var m in r.Mentions)
            n += 192 + m.Context.Length * 2L +
                 (m.Variant.Length + m.Entity.Length + m.Theme.Length) * 2L;
        foreach (var s in r.SemanticMatches)
            n += 160 + s.Snippet.Length * 2L +
                 (s.PatternId.Length + s.PatternName.Length + s.FilePath.Length) * 2L;
        return n;
    }
}
