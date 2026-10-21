using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>Вердикт эксперта о находке (R2-13).</summary>
public enum VerdictKind
{
    /// <summary>Подтверждено: находка корректна.</summary>
    Confirmed,
    /// <summary>Ложное срабатывание: фильтруемый шум.</summary>
    Rejected,
    /// <summary>Спорно: требуется дополнительная проверка.</summary>
    Uncertain,
}

/// <summary>
/// Вердикт пользователя об одной находке. Ключ идентичности —
/// (FileHash, Offset, Variant): контентно-адресуемый, переживает переименование
/// файла и пересборку кэша; Offset — позиция совпадения в исходном тексте.
/// </summary>
public sealed record VerdictRecord(
    DateTimeOffset Ts,
    string FileHash,
    long Offset,
    string Variant,
    string Entity,
    VerdictKind Verdict,
    string Note = "",
    string Tag = "");

/// <summary>
/// Хранилище вердиктов (R2-13): append-only JSONL в %APPDATA%, последний
/// вердикт по ключу — действующий. Накопленная разметка — актив проекта:
/// сразу отсекает шум, завтра служит выборкой для классификатора омонимии
/// (R1-25) и авточерновиков кодекса (R2-24). Повреждённые строки пропускаются.
/// </summary>
public sealed class VerdictStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Стабильный читаемый формат: "verdict":"rejected", а не число —
        // файл живёт годами и должен остаться парсимым вне приложения.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Канонический файл (%APPDATA%\EntityAnalyzer\verdicts.jsonl).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        ProfilePaths.DirectoryPath, "verdicts.jsonl");

    /// <summary>Хранилище по каноническому пути.</summary>
    public static VerdictStore Default { get; } = new(DefaultPath);

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<(string Hash, long Offset, string Variant), VerdictRecord> _index;
    private bool _loaded;

    public VerdictStore(string path)
    {
        _path = path;
        _index = [];
    }

    /// <summary>Добавить вердикт (дописать строку и обновить индекс «последний побеждает»).</summary>
    public void Append(VerdictRecord record)
    {
        lock (_gate)
        {
            EnsureLoaded();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, JsonSerializer.Serialize(record, JsonOpts) + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logging.Log.Warn("verdicts", $"Не удалось дописать вердикт: {ex.Message}");
            }
            _index[KeyOf(record)] = record;
        }
    }

    /// <summary>
    /// Добавить несколько вердиктов одной записью — для массовой проверки
    /// выборки. Порядок сохраняется, последняя запись по ключу побеждает.
    /// </summary>
    public void AppendRange(IEnumerable<VerdictRecord> records)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var batch = records as IList<VerdictRecord> ?? records.ToList();
            if (batch.Count == 0) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var lines = string.Join(Environment.NewLine,
                    batch.Select(r => JsonSerializer.Serialize(r, JsonOpts)));
                File.AppendAllText(_path, lines + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logging.Log.Warn("verdicts", $"Не удалось дописать вердикты: {ex.Message}");
            }
            foreach (var record in batch) _index[KeyOf(record)] = record;
        }
    }

    /// <summary>Действующий вердикт по ключу находки либо null.</summary>
    public VerdictRecord? Find(string fileHash, long offset, string variant)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _index.GetValueOrDefault((fileHash ?? "", offset, variant ?? ""));
        }
    }

    /// <summary>
    /// Удалить действующий вердикт (отмена последнего решения в интерфейсе).
    /// Файл компактируется атомарно: в нём остаются только действующие записи.
    /// Append-only журнал при этом сохраняет главный контракт — «последний
    /// вердикт по ключу побеждает»; удаление просто убирает ключ из индекса.
    /// Возвращает false, если записи не было.
    /// </summary>
    public bool Remove(string fileHash, long offset, string variant)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_index.Remove((fileHash ?? "", offset, variant ?? ""))) return false;
            RewriteLocked();
            return true;
        }
    }

    /// <summary>
    /// Удалить несколько действующих вердиктов одной атомарной перезаписью —
    /// для отмены массовой проверки. Возвращает число реально удалённых ключей.
    /// </summary>
    public int RemoveRange(IEnumerable<(string FileHash, long Offset, string Variant)> keys)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var removed = 0;
            foreach (var (hash, offset, variant) in keys)
                if (_index.Remove((hash ?? "", offset, variant ?? ""))) removed++;
            if (removed > 0) RewriteLocked();
            return removed;
        }
    }

    /// <summary>Все действующие вердикты (по одному на ключ).</summary>
    public IReadOnlyList<VerdictRecord> All
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return [.. _index.Values];
            }
        }
    }

    /// <summary>Счётчики действующих вердиктов по типам. Все три вида присутствуют всегда
    /// (нули включительно) — чтобы потребители не ловили KeyNotFoundException.</summary>
    public IReadOnlyDictionary<VerdictKind, int> Stats()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var stats = new Dictionary<VerdictKind, int>
            {
                [VerdictKind.Confirmed] = 0,
                [VerdictKind.Rejected] = 0,
                [VerdictKind.Uncertain] = 0,
            };
            foreach (var v in _index.Values)
                stats[v.Verdict] = stats.GetValueOrDefault(v.Verdict) + 1;
            return stats;
        }
    }

    private static (string, long, string) KeyOf(VerdictRecord r) =>
        (r.FileHash ?? "", r.Offset, r.Variant ?? "");

    /// <summary>Атомарно переписать журнал действующими записями (после удаления).</summary>
    private void RewriteLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllLines(
                tmp,
                _index.Values.Select(r => JsonSerializer.Serialize(r, JsonOpts)),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Индекс уже обновлён: в памяти отмена сработала, на диск попадёт
            // при следующей успешной записи. Не роняем интерфейс из-за I/O.
            Logging.Log.Warn("verdicts", $"Не удалось переписать вердикты: {ex.Message}");
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadLines(_path, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<VerdictRecord>(line, JsonOpts);
                    if (rec is not null) _index[KeyOf(rec)] = rec;
                }
                catch { /* битая строка — пропустить */ }
            }
        }
        catch
        {
            _index = []; // недоступный файл — начинаем с пустого индекса
        }
    }
}
