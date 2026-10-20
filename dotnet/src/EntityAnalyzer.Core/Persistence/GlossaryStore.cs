using System.Text;
using System.Text.Json;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// Запись глоссария (Группа 3, item 25): термин, определение и статус.
/// Append-only JSONL; действующей считается последняя запись по слову,
/// пустой статус — сброс вердикта (определение при этом сохраняется).
/// </summary>
public sealed record GlossaryRecord(
    DateTimeOffset Ts,
    string Word,
    string Definition,
    string Status);

/// <summary>
/// Хранилище глоссария: %APPDATA%\EntityAnalyzer\glossary.jsonl.
/// Хранится отдельно от сущностей и каталога — пользовательские определения
/// не смешиваются с данными анализа.
/// </summary>
public sealed class GlossaryStore
{
    public const string Confirmed = "confirmed";
    public const string Rejected = "rejected";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Канонический файл (%APPDATA%\EntityAnalyzer\glossary.jsonl).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        ProfilePaths.DirectoryPath, "glossary.jsonl");

    /// <summary>Хранилище по каноническому пути.</summary>
    public static GlossaryStore Default { get; } = new(DefaultPath);

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, GlossaryRecord> _index = new(StringComparer.Ordinal);
    private bool _loaded;

    public GlossaryStore(string path) => _path = path;

    /// <summary>Записать термин («confirmed» / «rejected» / "" — сброс вердикта).</summary>
    public void Set(string word, string definition, string status)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(word)) return;
            var record = new GlossaryRecord(
                DateTimeOffset.Now, word, definition ?? "", status ?? "");
            Append(record);
            _index[word] = record;
        }
    }

    /// <summary>Действующая запись по слову (null — термин ещё не трогали).</summary>
    public GlossaryRecord? Get(string word)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _index.GetValueOrDefault(word);
        }
    }

    /// <summary>Все записи с непустым статусом: слово → запись.</summary>
    public IReadOnlyDictionary<string, GlossaryRecord> AllActive()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _index
                .Where(kv => kv.Value.Status.Length > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }
    }

    private void Append(GlossaryRecord record)
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
            Logging.Log.Warn("glossary", $"Не удалось сохранить термин глоссария: {ex.Message}");
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
                    var rec = JsonSerializer.Deserialize<GlossaryRecord>(line, JsonOpts);
                    if (rec is not null && !string.IsNullOrEmpty(rec.Word))
                        _index[rec.Word] = rec;
                }
                catch { /* битая строка — пропустить */ }
            }
        }
        catch
        {
            _index = []; // недоступный файл — начинаем с пустого глоссария
        }
    }
}
