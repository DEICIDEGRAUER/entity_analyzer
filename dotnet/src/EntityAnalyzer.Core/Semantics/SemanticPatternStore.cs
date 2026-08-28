using System.IO;
using System.Reflection;

namespace EntityAnalyzer.Core.Semantics;

/// <summary>
/// Хранилище семантических паттернов и словарей.
/// Встроенные определения (Resources/*.json) сливаются с пользовательскими
/// файлами в %APPDATA%\EntityAnalyzer: пользовательские записи по id
/// перекрывают встроенные, новые категории добавляются без изменения кода.
/// </summary>
public sealed class SemanticPatternStore
{
    private const string PatternsResource = "EntityAnalyzer.Core.Resources.default_semantic_patterns.json";
    private const string LexiconsResource = "EntityAnalyzer.Core.Resources.default_semantic_lexicons.json";

    /// <summary>
    /// Каталог пользовательских файлов. Переопределяется переменной
    /// EA_SEMANTIC_USER_DIR — изоляция тестов от реальных данных машины.
    /// </summary>
    public static string UserDir
    {
        get
        {
            var override_ = Environment.GetEnvironmentVariable("EA_SEMANTIC_USER_DIR");
            return string.IsNullOrWhiteSpace(override_)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EntityAnalyzer")
                : override_;
        }
    }

    public static string UserPatternsPath => Path.Combine(UserDir, "semantic_patterns.json");
    public static string UserLexiconsPath => Path.Combine(UserDir, "semantic_lexicons.json");

    public IReadOnlyList<SemanticPattern> Patterns { get; private set; } = [];
    public LexiconSet Lexicons { get; private set; } = new();

    public static SemanticPatternStore Load()
    {
        var store = new SemanticPatternStore();

        // 1) Встроенные словари + пользовательские дополнения.
        var lexicons = LexiconFileDto.Parse(ReadEmbedded(LexiconsResource));
        if (File.Exists(UserLexiconsPath))
        {
            try
            {
                lexicons = LexiconSet.Merge(lexicons, LexiconFileDto.Parse(File.ReadAllText(UserLexiconsPath)));
            }
            catch { /* битый пользовательский файл — работаем на встроенных */ }
        }
        store.Lexicons = lexicons;

        // 2) Встроенные паттерны; пользовательский файл перекрывает по id
        //    (так выключение встроенного паттерна сохраняется навсегда).
        var embedded = SemanticPatternJson.Parse(ReadEmbedded(PatternsResource));
        var byId = embedded.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        if (File.Exists(UserPatternsPath))
        {
            try
            {
                foreach (var user in SemanticPatternJson.Parse(File.ReadAllText(UserPatternsPath)))
                    byId[user.Id] = user;
            }
            catch { /* битый файл — игнорируем */ }
        }

        store.Patterns = [.. byId.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
        return store;
    }

    /// <summary>Сохранить пользовательские паттерны (вкл/выкл, правки, новые).</summary>
    public void SaveUserPatterns(IEnumerable<SemanticPattern> patterns)
    {
        var dir = Path.GetDirectoryName(UserPatternsPath)!;
        Directory.CreateDirectory(dir);
        var tmp = UserPatternsPath + ".tmp";
        File.WriteAllText(tmp, SemanticPatternJson.Serialize(patterns));
        File.Move(tmp, UserPatternsPath, overwrite: true);
    }

    /// <summary>Создать стартовый пользовательский файл (для редактирования), если его нет.</summary>
    public void EnsureUserFile()
    {
        if (File.Exists(UserPatternsPath)) return;
        SaveUserPatterns(Patterns);
    }

    private static string ReadEmbedded(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Ресурс не найден: " + resourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
