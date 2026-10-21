using System.Text;
using System.Text.Json;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// Вердикт пользователя по кандидату каталога (Группа 3, item 8): привязка
/// «сущность|написание». Append-only JSONL; действующим считается последний
/// вердикт по ключу, пустая строка — сброс вердикта.
/// </summary>
public sealed record CatalogIgnoreRecord(
    DateTimeOffset Ts,
    string Key,
    string Entity,
    string Spelling,
    string Verdict);

/// <summary>
/// Хранилище отклонённых кандидатов каталога:
/// %APPDATA%\EntityAnalyzer\catalog_ignored.jsonl. Отдельный файл
/// пользовательской разметки — данные анализа и каталог не меняются; сбои
/// не роняют приложение, при следующем входе кандидат не предлагается.
/// </summary>
public sealed class CatalogIgnoreStore
{
    public const string Ignored = "ignored";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Канонический файл (%APPDATA%\EntityAnalyzer\catalog_ignored.jsonl).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        ProfilePaths.DirectoryPath, "catalog_ignored.jsonl");

    /// <summary>Хранилище по каноническому пути.</summary>
    public static CatalogIgnoreStore Default { get; } = new(DefaultPath);

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, CatalogIgnoreRecord> _index = new(StringComparer.Ordinal);
    private bool _loaded;

    public CatalogIgnoreStore(string path) => _path = path;

    public static string MakeKey(string entity, string spelling) => entity + "|" + spelling;

    /// <summary>Записать вердикт («ignored» / "" — сброс).</summary>
    public void Set(string entity, string spelling, string verdict)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(spelling)) return;
            var key = MakeKey(entity, spelling);
            var record = new CatalogIgnoreRecord(DateTimeOffset.Now, key, entity, spelling, verdict ?? "");
            Append(record);
            _index[key] = record;
        }
    }

    /// <summary>Вернуть кандидата в список (сброс вердикта).</summary>
    public void Clear(string entity, string spelling) => Set(entity, spelling, "");

    /// <summary>Действующий вердикт по кандидату либо пустая строка.</summary>
    public string Find(string entity, string spelling)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _index.TryGetValue(MakeKey(entity, spelling), out var rec) ? rec.Verdict : "";
        }
    }

    /// <summary>Отклонён ли кандидат прямо сейчас (предлагать не нужно).</summary>
    public bool IsIgnored(string entity, string spelling) => Find(entity, spelling).Length > 0;

    /// <summary>Все действующие (непустые) вердикты: ключ → вердикт.</summary>
    public IReadOnlyDictionary<string, string> AllActive()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _index
                .Where(kv => kv.Value.Verdict.Length > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Verdict, StringComparer.Ordinal);
        }
    }

    private void Append(CatalogIgnoreRecord record)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(_path,
                JsonSerializer.Serialize(record, JsonOpts) + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logging.Log.Warn("catalog_ignore", $"Не удалось сохранить отклонение кандидата каталога: {ex.Message}");
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
                    var rec = JsonSerializer.Deserialize<CatalogIgnoreRecord>(line, JsonOpts);
                    if (rec is not null && !string.IsNullOrEmpty(rec.Key))
                        _index[rec.Key] = rec;
                }
                catch { /* битая строка — пропустить */ }
            }
        }
        catch
        {
            _index = []; // недоступный файл — начинаем с пустого словаря вердиктов
        }
    }
}
