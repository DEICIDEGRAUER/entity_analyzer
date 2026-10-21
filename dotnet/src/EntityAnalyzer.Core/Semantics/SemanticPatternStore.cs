using System.IO;
using System.Reflection;
using System.Threading;

using EntityAnalyzer.Core.Persistence;

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
    /// Переопределение каталога пользовательских данных для тестов.
    /// Статическое поле с блокировкой для изоляции тестов в коллекции LogSequential.
    /// </summary>
    private static string? _testUserDir;
    private static readonly object _testUserDirLock = new();

    /// <summary>
    /// Каталог пользовательских файлов. Переопределяется переменной
    /// EA_SEMANTIC_USER_DIR — изоляция тестов от реальных данных машины.
    /// В тестах коллекции LogSequential переопределяется через статическое поле.
    /// </summary>
    public static string UserDir
    {
        get
        {
            // Приоритет: статическое поле (тестовая изоляция LogSequential) > переменная окружения > дефолт
            string? testDir;
            lock (_testUserDirLock)
            {
                testDir = _testUserDir;
            }
            if (!string.IsNullOrWhiteSpace(testDir))
                return testDir;

            var override_ = Environment.GetEnvironmentVariable("EA_SEMANTIC_USER_DIR");
            if (!string.IsNullOrWhiteSpace(override_))
                return override_;

            return ProfilePaths.DirectoryPath;
        }
    }

    /// <summary>
    /// Установить каталог пользовательских данных для тестов коллекции LogSequential.
    /// Используется фикстурой LogSequentialFixture для изоляции тестов.
    /// </summary>
    public static void SetTestUserDir(string? path)
    {
        lock (_testUserDirLock)
        {
            _testUserDir = string.IsNullOrWhiteSpace(path) ? null : path;
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
        //    Если пользовательский паттерн имеет Name = "[DELETED] ...", считаем это
        //    могильником — исключаем встроенный паттерн с таким же ID.
        var embedded = SemanticPatternJson.Parse(ReadEmbedded(PatternsResource));
        var byId = embedded.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var deletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(UserPatternsPath))
        {
            try
            {
                foreach (var user in SemanticPatternJson.Parse(File.ReadAllText(UserPatternsPath)))
                {
                    if (user.Name.StartsWith("[DELETED] ", StringComparison.Ordinal))
                    {
                        deletedIds.Add(user.Id);
                    }
                    else
                    {
                        byId[user.Id] = user;
                    }
                }
            }
            catch { /* битый файл — игнорируем */ }
        }

        // Исключаем могильники
        foreach (var id in deletedIds)
            byId.Remove(id);

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

    /// <summary>Создать стартовый пользовательский файл (для редактирования), если его нет.
    /// </summary>
    public void EnsureUserFile()
    {
        if (File.Exists(UserPatternsPath)) return;
        SaveUserPatterns(Patterns);
    }

    /// <summary>
    /// Item 46/48: пользовательские правки словарей для редактора —
    /// имя словаря → упорядоченный список слов. Слово с префиксом «!» означает
    /// удаление одноимённого слова из встроенного словаря при слиянии.
    /// </summary>
    public static Dictionary<string, List<string>> LoadUserLexiconEdits()
    {
        if (!File.Exists(UserLexiconsPath)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return LexiconFileDto.ParseRaw(File.ReadAllText(UserLexiconsPath));
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase); // битый файл — пустые правки
        }
    }

    /// <summary>Item 48: сохранить правки словарей в пользовательский файл (атомарно).</summary>
    public static void SaveUserLexiconEdits(Dictionary<string, List<string>> edits)
    {
        var dir = Path.GetDirectoryName(UserLexiconsPath)!;
        Directory.CreateDirectory(dir);
        var tmp = UserLexiconsPath + ".tmp";
        File.WriteAllText(tmp, LexiconFileDto.Serialize(edits));
        File.Move(tmp, UserLexiconsPath, overwrite: true);
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