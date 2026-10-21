using System.Text;
using System.Text.Json;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>Запись об одном завершённом прогоне анализа.</summary>
public sealed class RunRecord
{
    public DateTimeOffset StartedAt { get; set; }
    public string Preset { get; set; } = "";
    public int TotalMentions { get; set; }
    public int FilesWithMentions { get; set; }
    public int UniqueEntities { get; set; }
    public double DurationSeconds { get; set; }
    public long RawMatches { get; set; }
    public int FinalMentions { get; set; }
    public List<string> CorpusDirs { get; set; } = [];
    public long BytesScanned { get; set; }
    public int FilesTotal { get; set; }
    public Dictionary<string, int>? EntityCounts { get; set; }
    public Dictionary<string, double>? StageMs { get; set; }
    public string? ConfigSignature { get; set; }
    /// <summary>Итог прогона: «Успешно» / «С ошибками (N)» / «Нет файлов» (QA-UX #13).</summary>
    public string Status { get; set; } = "Успешно";
    /// <summary>Сколько файлов взято из кэша без перечитывания — признак инкрементальности (QA-UX #16).</summary>
    public int FilesFromCache { get; set; }
    /// <summary>Прогон завершился не идеально — для подсветки статуса.</summary>
    public bool IsFailed => Status != "Успешно";

    /// <summary>R1-21: хеш предыдущей записи цепи (генезис — у первой новой записи).</summary>
    public string? PrevHash { get; set; }

    /// <summary>R1-21: SHA-256 канонического содержимого записи вместе с prev_hash.
    /// Тихая правка журнала прогонов становится обнаружимой.</summary>
    public string? Hash { get; set; }
}

/// <summary>
/// История прогонов: append-only JSONL (одна строка — один запуск).
/// Ноль зависимостей, переживает повреждение последней строки, читается
/// любым редактором. Для сотен прогонов объёмы копеечные.
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Канонический файл истории (%APPDATA%\EntityAnalyzer\history.jsonl).
    /// Единственный источник пути: и запись прогонов, и страница «История»
    /// берут его отсюда, поэтому модуль работает сам по себе без внешних знаний.
    /// </summary>
    public static string DefaultPath { get; } = Path.Combine(
        ProfilePaths.DirectoryPath, "history.jsonl");

    private readonly string _path;
    private string? _lastHash;
    private bool _lastHashLoaded;

    public HistoryStore(string path) => _path = path;

    /// <summary>Хранилище по каноническому пути.</summary>
    public static HistoryStore Default { get; } = new(DefaultPath);

    public void Append(RunRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            lock (this)
            {
                // R1-21: дописываем звено цепи целостности. Хеш считается по
                // содержимому записи и хешу предыдущей — правка или удаление
                // средней строки ломает всю последующую верификацию.
                if (!_lastHashLoaded)
                {
                    _lastHash = LoadLastHash();
                    _lastHashLoaded = true;
                }
                record.PrevHash = _lastHash ?? JournalIntegrity.Genesis;
                record.Hash = JournalIntegrity.ComputeHash(record, record.PrevHash);
                _lastHash = record.Hash;

                File.AppendAllText(_path, JsonSerializer.Serialize(record, JsonOpts) + Environment.NewLine, Encoding.UTF8);
                Logging.Audit.Write("history", _path); // R2-23: аудит доступа к данным
            }
        }
        catch (Exception ex)
        {
            Logging.Log.Warn("history", $"Не удалось дописать историю: {ex.Message}");
        }
    }

    /// <summary>Хеш последней записи файла (генезис — пустой/битый файл/легаси без хешей).</summary>
    private string? LoadLastHash()
    {
        try
        {
            if (!File.Exists(_path)) return JournalIntegrity.Genesis;
            string? last = null;
            foreach (var line in File.ReadLines(_path, Encoding.UTF8))
                if (line.Length > 0) last = line;
            if (last is null) return JournalIntegrity.Genesis;
            var rec = JsonSerializer.Deserialize<RunRecord>(last, JsonOpts);
            return string.IsNullOrEmpty(rec?.Hash) ? JournalIntegrity.Genesis : rec!.Hash;
        }
        catch
        {
            return JournalIntegrity.Genesis;
        }
    }

    /// <summary>Последние <paramref name="n"/> прогонов в хронологическом порядке.</summary>
    public IReadOnlyList<RunRecord> ReadLast(int n)
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var lines = File.ReadAllLines(_path, Encoding.UTF8);
            var records = new List<RunRecord>(lines.Length);
            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                try { records.Add(JsonSerializer.Deserialize<RunRecord>(line, JsonOpts)!); }
                catch { /* битая строка — пропустить */ }
            }
            return [.. records.TakeLast(n)];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>Запись журнала изменений каталога сущностей.</summary>
public sealed record CatalogChangeEntry(DateTimeOffset Ts, string Action, string Entity, string Details);

/// <summary>
/// Журнал изменений каталога: добавление/правка/удаление/включение/импорт/сброс.
/// Append-only JSONL в %APPDATA%.
/// </summary>
public sealed class CatalogChangeLog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public CatalogChangeLog(string path) => _path = path;

    public void Record(string action, string entity, string details)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var entry = new CatalogChangeEntry(DateTimeOffset.Now, action, entity, details);
            lock (this)
            {
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, JsonOpts) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* журнал не должен ломать работу */ }
    }

    public static IReadOnlyList<CatalogChangeEntry> Read(string path, int n)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var entries = new List<CatalogChangeEntry>(Math.Min(n, lines.Length));
            foreach (var line in lines.TakeLast(Math.Max(1, n)))
            {
                if (line.Length == 0) continue;
                try { entries.Add(JsonSerializer.Deserialize<CatalogChangeEntry>(line, JsonOpts)!); }
                catch { /* битая строка */ }
            }
            return entries;
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>
/// R1-21: цепочка целостности журнала прогонов. Каждая запись содержит
/// prev_hash и SHA-256 от канонического содержимого + prev_hash. Верификация
/// обнаруживает тихую правку содержимого, удаление и вставку записей.
/// Записи старого формата (без хешей) пропускаются без замечаний —
/// цепь начинается с первой записи нового формата.
/// </summary>
public static class JournalIntegrity
{
    /// <summary>Хеш «до начала времён» для первой записи цепи.</summary>
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions CanonicalOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Хеш записи: канонический JSON содержимого (без полей хешей) + prev_hash.
    /// Клон через JSON-раундтрип устойчив к будущим полям RunRecord — новое поле
    /// автоматически начинает участвовать в хеше без правок этого класса.
    /// </summary>
    public static string ComputeHash(RunRecord record, string prevHash)
    {
        var clone = JsonSerializer.Deserialize<RunRecord>(
            JsonSerializer.Serialize(record, CanonicalOpts), CanonicalOpts)!;
        clone.PrevHash = null;
        clone.Hash = null;

        var canonical = JsonSerializer.Serialize(clone, CanonicalOpts) + "|" + prevHash;
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Проверить цепочку. Возвращает список проблем («строка N: …»); пустой
    /// список — журнал не менялся мимо приложения.
    /// </summary>
    public static IReadOnlyList<string> Verify(IEnumerable<RunRecord> records)
    {
        var problems = new List<string>();
        var expectedPrev = Genesis;
        var line = 0;

        foreach (var r in records)
        {
            line++;
            if (string.IsNullOrEmpty(r.Hash)) continue; // легаси-запись

            if (!string.Equals(r.PrevHash, expectedPrev, StringComparison.Ordinal))
                problems.Add($"Строка {line}: нарушена связь с предыдущей записью (возможно удаление/вставка)");
            else if (!string.Equals(ComputeHash(r, r.PrevHash!), r.Hash, StringComparison.Ordinal))
                problems.Add($"Строка {line}: содержимое не соответствует хешу (запись изменена)");

            expectedPrev = r.Hash!;
        }

        return problems;
    }
}
