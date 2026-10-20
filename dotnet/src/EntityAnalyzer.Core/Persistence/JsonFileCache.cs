using System.Globalization;
using System.Text.Json;
using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>Content-addressed per-file result cache.</summary>
public interface IAnalysisCache
{
    bool TryGet(string key, out FileResult? result);
    void Set(string key, FileResult result);
    /// <summary>Persists pending index changes (call once after a batch run).</summary>
    void Flush();
}

/// <summary>
/// File-backed cache: an index.json plus one JSON document per entry.
/// Writes are atomic (tmp file + move); a corrupted index degrades to empty.
/// Entries carrying a foreign config hash are treated as stale.
/// Жизненный цикл (см. <see cref="CachePolicy"/>): при открытии кэш убирает
/// записи старше TTL, перебирающие лимит объёма (вытесняются самые старые)
/// и осиротевшие data-файлы — иначе каталог кэша растёт бесконечно.
/// Сбои кэша никогда не всплывают в результат прогона.
/// </summary>
public sealed class JsonFileCache : IAnalysisCache
{
    internal sealed record Entry(string DataFile, string? ConfigHash, string Timestamp, long Size = 0);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _dir;
    private readonly string? _configHash;
    private readonly CachePolicy _policy;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _index;

    /// <summary>Сколько записей убрала последняя уборка при открытии (для журнала движка).</summary>
    public int LastPruneRemoved { get; }

    /// <summary>Сколько байт освободила последняя уборка при открытии.</summary>
    public long LastPruneFreedBytes { get; }

    /// <summary>Число записей индекса.</summary>
    public int Count
    {
        get { lock (_gate) return _index.Count; }
    }

    public JsonFileCache(string directory, string? configHash = null, CachePolicy? policy = null)
    {
        _dir = directory;
        _configHash = configHash;
        _policy = policy ?? new CachePolicy();
        _index = LoadIndex();
        (LastPruneRemoved, LastPruneFreedBytes) = Prune();
    }

    public bool TryGet(string key, out FileResult? result)
    {
        result = null;
        string path;
        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var entry)) return false;
            if (_policy.InvalidateOnConfigChange && _configHash is not null &&
                !string.Equals(entry.ConfigHash, _configHash, StringComparison.Ordinal))
                return false;
            path = Path.Combine(_dir, entry.DataFile);
        }

        try
        {
            result = JsonSerializer.Deserialize<FileResult>(File.ReadAllText(path), JsonOpts);
            return result is not null;
        }
        catch
        {
            return false;
        }
    }

    public void Set(string key, FileResult result)
    {
        try
        {
            var dataFile = DataFileName(key);
            var json = JsonSerializer.Serialize(result, JsonOpts);
            var size = System.Text.Encoding.UTF8.GetByteCount(json);

            // Сериализация и запись — вне глобальной блокировки: иначе все
            // воркеры ждут диск друг друга. Уникальный tmp исключает гонку
            // при одинаковом контентном ключе.
            Directory.CreateDirectory(_dir);
            var tmp = Path.Combine(_dir, dataFile + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tmp, json);
            var finalPath = Path.Combine(_dir, dataFile);
            File.Move(tmp, finalPath, overwrite: true);

            lock (_gate)
            {
                _index[key] = new Entry(dataFile, _configHash,
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    size);
                _dirty = true; // index is flushed once per run, not per file
            }
        }
        catch
        {
            // Cache failures must never break the analysis run.
        }
    }

    /// <summary>
    /// Имя файла данных по ключу. BUG-012: ключ движка — "хеш|подпись", а '|'
    /// запрещён в именах файлов Windows: каждая запись кэша молча падала в catch,
    /// кэш результатов не наполнялся вовсе, «тёплые» прогоны перечитывали всё.
    /// Имя файла теперь — SHA-256 отпечаток ключа: безопасно на всех ФС.
    /// </summary>
    private static string DataFileName(string key) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key)))[..32] + ".json";

    private bool _dirty;

    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            SaveIndexLocked();
            _dirty = false;
        }
    }

    // ---------- жизненный цикл ----------

    /// <summary>
    /// Уборка при открытии кэша (одна на сессию). Порядок: TTL → чужие подписи
    /// конфига (если включена инвалидация) → лимит объёма → осиротевшие файлы.
    /// Удаление записи всегда удаляет и её data-файл — иначе диск утекает.
    /// </summary>
    private (int Removed, long FreedBytes) Prune()
    {
        var removed = 0;
        long freed = 0;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var stale = new List<string>();
            foreach (var (key, entry) in _index)
            {
                if (IsExpired(entry, now) ||
                    (_policy.InvalidateOnConfigChange && _configHash is not null &&
                     !string.Equals(entry.ConfigHash, _configHash, StringComparison.Ordinal)))
                {
                    stale.Add(key);
                }
            }
            foreach (var key in stale)
            {
                TryDeleteDataFile(key, ref freed);
                removed++;
            }

            // Лимит объёма: вытесняем самые старые, пока не уложимся в бюджет.
            if (_policy.MaxSizeBytes > 0 && _index.Count > 0)
            {
                long total = _index.Values.Sum(SizeOf);
                if (total > _policy.MaxSizeBytes)
                {
                    var oldestFirst = _index
                        .OrderBy(kv => kv.Value.Timestamp, StringComparer.Ordinal)
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var key in oldestFirst)
                    {
                        if (total <= _policy.MaxSizeBytes) break;
                        if (!_index.TryGetValue(key, out var entry)) continue;
                        total -= SizeOf(entry);
                        TryDeleteDataFile(key, ref freed);
                        removed++;
                    }
                }
            }

            PruneOrphans(ref freed);

            if (removed > 0) SaveIndexLocked();
        }
        return (removed, freed);
    }

    private bool IsExpired(Entry entry, DateTimeOffset now)
    {
        if (_policy.MaxAgeDays <= 0) return false;
        // Нечитаемая отметка времени — не повод терять данные (легаси/ручная правка).
        return DateTimeOffset.TryParse(entry.Timestamp, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts) &&
               now - ts > TimeSpan.FromDays(_policy.MaxAgeDays);
    }

    /// <summary>Убирает запись индекса и её data-файл; освобождённые байты — в freed.</summary>
    private void TryDeleteDataFile(string key, ref long freed)
    {
        if (!_index.TryGetValue(key, out var entry)) return;
        _index.Remove(key);
        try
        {
            var path = Path.Combine(_dir, entry.DataFile);
            if (!File.Exists(path)) return;
            freed += new FileInfo(path).Length;
            File.Delete(path);
        }
        catch { /* файл занят — запись из индекса уже убрана, диск уберётся позже */ }
    }

    private long SizeOf(Entry entry)
    {
        if (entry.Size > 0) return entry.Size;
        try
        {
            var info = new FileInfo(Path.Combine(_dir, entry.DataFile));
            return info.Exists ? info.Length : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Осиротевшие файлы: *.json без записи в индексе (битый/сброшенный индекс,
    /// смена подписи конфига до появления уборки) и подвисшие *.tmp старше суток.
    /// Свежие tmp не трогаем — их может писать параллельный процесс.
    /// </summary>
    private void PruneOrphans(ref long freed)
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var known = _index.Values.Select(e => e.DataFile)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_dir))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("index.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (known.Contains(name)) continue;
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(1))
                            continue;
                    }
                    catch { continue; }
                }
                try
                {
                    freed += new FileInfo(file).Length;
                    File.Delete(file);
                }
                catch { /* занятый файл — не повод ломать прогон */ }
            }
        }
        catch { /* перечисление каталога не должно ронять кэш */ }
    }

    /// <summary>Чтение индекса с диска (общее для конструктора и обслуживания).</summary>
    internal static Dictionary<string, Entry> ReadIndex(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "index.json");
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOpts) ?? [];
        }
        catch
        {
            // Corrupt index → start fresh (original engine backed it up; we simply rebuild).
            return [];
        }
    }

    private Dictionary<string, Entry> LoadIndex() => ReadIndex(_dir);

    private void SaveIndexLocked()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = Path.Combine(_dir, "index.json.tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(_index, JsonOpts));
            File.Move(tmp, Path.Combine(_dir, "index.json"), overwrite: true);
        }
        catch { /* non-fatal */ }
    }
}
