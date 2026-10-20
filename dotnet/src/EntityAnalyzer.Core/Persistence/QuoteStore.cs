using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// Отметка цитаты в цитатнике (Группа 2, item 26). Ключ идентичности —
/// (FileHash, Offset, Variant): контентно-адресуемый, как у вердиктов
/// (<see cref="VerdictStore"/>); переживает переименование файла и пересборку
/// кэша. Запись со Starred=false — «снятие звёздочки»; действующей считается
/// последняя запись по ключу, поэтому история отметок не теряется.
/// </summary>
public sealed record QuoteRecord(
    DateTimeOffset Ts,
    string Key,
    bool Starred,
    string FileHash = "",
    long Offset = 0,
    string Variant = "",
    string Entity = "",
    string FileName = "",
    string FilePath = "",
    int Line = 0,
    string Context = "",
    string Theme = "",
    bool IsFuzzy = false);

/// <summary>
/// Хранилище цитатника: append-only JSONL в %APPDATA%\EntityAnalyzer\quotes.jsonl.
/// Отдельный файл пользовательской разметки — данные анализа и кэш не меняются.
/// Повреждённые строки пропускаются; сбои записи не роняют приложение.
/// </summary>
public sealed class QuoteStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Канонический файл (%APPDATA%\EntityAnalyzer\quotes.jsonl).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        ProfilePaths.DirectoryPath, "quotes.jsonl");

    /// <summary>Хранилище по каноническому пути.</summary>
    public static QuoteStore Default { get; } = new(DefaultPath);

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, QuoteRecord> _index = new(StringComparer.Ordinal);
    private bool _loaded;

    public QuoteStore(string path) => _path = path;

    /// <summary>Поставить звёздочку: дописать запись и обновить индекс.</summary>
    public void SetStar(QuoteRecord record)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(record.Key)) return;
            var starred = record with { Starred = true };
            Append(starred);
            _index[starred.Key] = starred;
        }
    }

    /// <summary>Снять звёздочку (минимальная запись-маркер, поля остаются в истории).</summary>
    public void Unstar(string key)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(key)) return;
            var tomb = new QuoteRecord(DateTimeOffset.Now, key, false);
            Append(tomb);
            _index[key] = tomb;
        }
    }

    /// <summary>Действующая запись по ключу либо null.</summary>
    public QuoteRecord? Find(string key)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return key is null ? null : _index.GetValueOrDefault(key);
        }
    }

    /// <summary>Отмечена ли цитата звёздочкой сейчас.</summary>
    public bool IsStarred(string key) => Find(key)?.Starred == true;

    /// <summary>Количество отмеченных цитат в книге.</summary>
    public int StarredCount
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _index.Values.Count(v => v.Starred);
            }
        }
    }

    /// <summary>Все отмеченные цитаты, недавно отмеченные — первыми.</summary>
    public IReadOnlyList<QuoteRecord> AllStarred()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return [.. _index.Values.Where(v => v.Starred).OrderByDescending(v => v.Ts)];
        }
    }

    /// <summary>Снять все звёздочки; возвращает, сколько их было.</summary>
    public int ClearAll()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var keys = _index.Values.Where(v => v.Starred).Select(v => v.Key).ToList();
            foreach (var key in keys)
            {
                var tomb = new QuoteRecord(DateTimeOffset.Now, key, false);
                Append(tomb);
                _index[key] = tomb;
            }
            return keys.Count;
        }
    }

    private void Append(QuoteRecord record)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(_path, JsonSerializer.Serialize(record, JsonOpts) + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logging.Log.Warn("quotes", $"Не удалось сохранить отметку цитатника: {ex.Message}");
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
                    var rec = JsonSerializer.Deserialize<QuoteRecord>(line, JsonOpts);
                    if (rec is not null && !string.IsNullOrEmpty(rec.Key))
                        _index[rec.Key] = rec;
                }
                catch { /* битая строка — пропустить */ }
            }
        }
        catch
        {
            _index = []; // недоступный файл — начинаем с пустой книги
        }
    }
}
