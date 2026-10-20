using System.Text;
using System.Text.Json;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// Вердикт пользователя по эпитету (Группа 3, item 23): привязка
/// «сущность|эпитет». Append-only JSONL; действующим считается последний
/// вердикт по ключу, пустая строка — сброс вердикта.
/// </summary>
public sealed record EpithetVerdictRecord(
    DateTimeOffset Ts,
    string Key,
    string Entity,
    string Epithet,
    string Verdict);

/// <summary>
/// Хранилище вердиктов словаря эпитетов:
/// %APPDATA%\EntityAnalyzer\epithets.jsonl. Отдельный файл пользовательской
/// разметки — данные анализа и кэш не меняются; сбои не роняют приложение.
/// </summary>
public sealed class EpithetStore
{
    public const string Confirmed = "confirmed";
    public const string Rejected = "rejected";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Канонический файл (%APPDATA%\EntityAnalyzer\epithets.jsonl).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        ProfilePaths.DirectoryPath, "epithets.jsonl");

    /// <summary>Хранилище по каноническому пути.</summary>
    public static EpithetStore Default { get; } = new(DefaultPath);

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, EpithetVerdictRecord> _index = new(StringComparer.Ordinal);
    private bool _loaded;

    public EpithetStore(string path) => _path = path;

    public static string MakeKey(string entity, string epithet) => entity + "|" + epithet;

    /// <summary>Записать вердикт («confirmed» / «rejected» / "" — сброс).</summary>
    public void Set(string entity, string epithet, string verdict)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(epithet)) return;
            var key = MakeKey(entity, epithet);
            var record = new EpithetVerdictRecord(DateTimeOffset.Now, key, entity, epithet, verdict ?? "");
            Append(record);
            _index[key] = record;
        }
    }

    /// <summary>Сбросить вердикт (привязка снова «нейтральная»).</summary>
    public void Clear(string entity, string epithet) => Set(entity, epithet, "");

    /// <summary>Действующий вердикт по привязке либо пустая строка.</summary>
    public string Find(string entity, string epithet)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _index.TryGetValue(MakeKey(entity, epithet), out var rec) ? rec.Verdict : "";
        }
    }

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

    private void Append(EpithetVerdictRecord record)
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
            Logging.Log.Warn("epithets", $"Не удалось сохранить вердикт эпитета: {ex.Message}");
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
                    var rec = JsonSerializer.Deserialize<EpithetVerdictRecord>(line, JsonOpts);
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
