using System.Text.Json;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// R3-1 (item 52): пользовательские слова тематических словарей —
/// %APPDATA%\EntityAnalyzer\user_themes.json. Формат: объект
/// «тема → массив слов»; битый или отсутствующий файл означает пусто.
/// Запись атомарная (tmp + move); сбои не роняют приложение.
/// </summary>
public sealed class ThemeStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Канонический файл (%APPDATA%\EntityAnalyzer\user_themes.json).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        ProfilePaths.DirectoryPath, "user_themes.json");

    /// <summary>Хранилище по каноническому пути.</summary>
    public static ThemeStore Default { get; } = new(DefaultPath);

    private readonly string _path;

    public ThemeStore(string path) => _path = path;

    /// <summary>Пользовательские слова по темам (тема → слова).</summary>
    public Dictionary<string, List<string>> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_path));
            return data is null
                ? new Dictionary<string, List<string>>(StringComparer.Ordinal)
                : new Dictionary<string, List<string>>(data, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
    }

    /// <summary>Атомарная запись всех тем.</summary>
    public void Save(IReadOnlyDictionary<string, List<string>> words)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(
            words.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()), JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Добавить слово к теме (дедупликация без учёта регистра).</summary>
    public bool AddWord(string theme, string word)
    {
        var w = (word ?? "").Trim();
        if (string.IsNullOrWhiteSpace(theme) || w.Length == 0) return false;
        var all = Load();
        if (!all.TryGetValue(theme, out var list))
        {
            list = [];
            all[theme] = list;
        }
        if (list.Any(x => string.Equals(x, w, StringComparison.OrdinalIgnoreCase))) return false;
        list.Add(w);
        Save(all);
        return true;
    }

    /// <summary>Убрать пользовательское слово темы.</summary>
    public bool RemoveWord(string theme, string word)
    {
        var all = Load();
        if (!all.TryGetValue(theme, out var list)) return false;
        var removed = list.RemoveAll(x => string.Equals(x, word, StringComparison.OrdinalIgnoreCase)) > 0;
        if (list.Count == 0) all.Remove(theme);
        if (removed) Save(all);
        return removed;
    }

    /// <summary>Пользовательские слова одной темы.</summary>
    public List<string> Words(string theme) =>
        Load().TryGetValue(theme, out var list) ? [.. list] : [];
}
