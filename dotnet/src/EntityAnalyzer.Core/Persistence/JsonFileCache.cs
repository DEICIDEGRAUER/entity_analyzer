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
/// </summary>
public sealed class JsonFileCache : IAnalysisCache
{
    private sealed record Entry(string DataFile, string? ConfigHash, string Timestamp);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _dir;
    private readonly string? _configHash;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _index;

    public JsonFileCache(string directory, string? configHash = null)
    {
        _dir = directory;
        _configHash = configHash;
        _index = [];
        LoadIndex();
        PruneForeign();
    }

    public bool TryGet(string key, out FileResult? result)
    {
        result = null;
        string path;
        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var entry)) return false;
            if (_configHash is not null && !string.Equals(entry.ConfigHash, _configHash, StringComparison.Ordinal))
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
            lock (_gate)
            {
                Directory.CreateDirectory(_dir);
                var tmp = Path.Combine(_dir, dataFile + ".tmp");
                File.WriteAllText(tmp, JsonSerializer.Serialize(result, JsonOpts));
                var finalPath = Path.Combine(_dir, dataFile);
                File.Move(tmp, finalPath, overwrite: true);

                _index[key] = new Entry(dataFile, _configHash, DateTimeOffset.UtcNow.ToString("O"));
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

    private void LoadIndex()
    {
        try
        {
            var path = Path.Combine(_dir, "index.json");
            if (!File.Exists(path)) return;
            _index = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOpts) ?? [];
        }
        catch
        {
            // Corrupt index → start fresh (original engine backed it up; we simply rebuild).
            _index = [];
        }
    }

    private void PruneForeign()
    {
        if (_configHash is null) return;
        lock (_gate)
        {
            var stale = _index.Where(kv => kv.Value.ConfigHash != _configHash).Select(kv => kv.Key).ToList();
            foreach (var k in stale) _index.Remove(k);
            if (stale.Count > 0) SaveIndexLocked();
        }
    }

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
