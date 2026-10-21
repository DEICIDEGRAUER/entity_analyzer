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
        PruneVanished();
    }

    /// <summary>Число записей индекса.</summary>
    public int Count
    {
        get { lock (_gate) return _index.Count; }
    }

    /// <summary>
    /// Убирает записи о файлах, которых больше нет на диске (удалены,
    /// переименованы, корпус отмонтирован). Без этого индекс путей копил бы
    /// мёртвые записи годами. Проверка существования дешёвая (stat) и одна
    /// на сессию — на корпусе в десятки тысяч файлов это доли секунды.
    /// </summary>
    private void PruneVanished()
    {
        lock (_gate)
        {
            var stale = _index.Keys.Where(p =>
            {
                try { return !File.Exists(p); }
                catch { return false; }
            }).ToList();
            if (stale.Count == 0) return;

            foreach (var p in stale) _index.Remove(p);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_index, JsonOpts));
                File.Move(tmp, _path, overwrite: true);
                _dirty = false;
            }
            catch { /* некритично: уборка повторится при следующем открытии */ }
        }
    }

    /// <summary>
    /// Пробует получить хэш из индекса, не читая файл. Проверка размера и mtime
    /// выполняется вне глобальной блокировки: чтение/хеширование больше не
    /// сериализуют параллельные файлы.
    /// </summary>
    public bool TryGetHash(string file, out string hash)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists)
            {
                hash = "";
                return false;
            }
        }
        catch (IOException)
        {
            hash = "";
            return false;
        }

        lock (_gate)
        {
            if (_index.TryGetValue(file, out var e) &&
                e.Size == info.Length &&
                Math.Abs((e.MtimeUtc - info.LastWriteTimeUtc).TotalMilliseconds) < 2)
            {
                hash = e.Hash;
                return true;
            }
        }

        hash = "";
        return false;
    }

    /// <summary>Запоминает хэш прочитанного файла; stat берётся после чтения.</summary>
    public void StoreHash(string file, string hash)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return;
            lock (_gate)
            {
                _index[file] = new Entry(info.Length, info.LastWriteTimeUtc, hash);
                _dirty = true;
            }
        }
        catch { /* файл мог исчезнуть между чтением и записью */ }
    }

    /// <summary>
    /// Хэш файла: если размер и mtime совпадают с индексом — берём сохранённый,
    /// иначе вызываем compute() и обновляем запись.
    /// </summary>
    public string GetOrComputeHash(string file, Func<string> compute)
    {
        if (TryGetHash(file, out var hash)) return hash;

        var computed = compute();
        StoreHash(file, computed);
        return computed;
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
