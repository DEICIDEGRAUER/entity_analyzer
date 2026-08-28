using System.IO;
using System.Text.Json;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// Инкрементальный индекс: путь → (размер, mtime, SHA-256). Неизменённый файл
/// (тот же размер и время записи) отдаёт хэш из индекса — чтение с диска
/// не выполняется, повторные прогоны проходят за секунды.
/// </summary>
public sealed class IncrementalIndexStore
{
    private sealed record Entry(long Size, DateTime MtimeUtc, string Hash);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _index = [];
    private bool _dirty;

    public IncrementalIndexStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>
    /// Хэш файла: если размер и mtime совпадают с индексом — берём сохранённый,
    /// иначе вызываем compute() и обновляем запись.
    /// </summary>
    public string GetOrComputeHash(string file, Func<string> compute)
    {
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) return compute();

                if (_index.TryGetValue(file, out var e) &&
                    e.Size == info.Length &&
                    Math.Abs((e.MtimeUtc - info.LastWriteTimeUtc).TotalMilliseconds) < 2)
                {
                    return e.Hash;
                }
            }
            catch (IOException)
            {
                return compute();
            }

            var hash = compute();
            try
            {
                var info = new FileInfo(file);
                _index[file] = new Entry(info.Length, info.LastWriteTimeUtc, hash);
                _dirty = true;
            }
            catch { /* файл мог исчезнуть между чтением и записью */ }
            return hash;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_index, JsonOpts));
                File.Move(tmp, _path, overwrite: true);
                _dirty = false;
            }
            catch { /* некритично */ }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            _index = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path), JsonOpts) ?? [];
        }
        catch
        {
            _index = []; // битый индекс — перестраиваем
        }
    }
}
