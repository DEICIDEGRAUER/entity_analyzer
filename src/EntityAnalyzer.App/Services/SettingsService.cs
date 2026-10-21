using System.IO;
using System.Text.Json;

namespace EntityAnalyzer.App.Services;

/// <summary>App-level persisted settings (%APPDATA%\EntityAnalyzer\settings.json).</summary>
public sealed class AppSettings
{
    public string CorpusDir { get; set; } = "";
    public string OutputDir { get; set; } = "output";
    public string ConfigPath { get; set; } = "";
    public string Preset { get; set; } = "extended";
    public List<string> Formats { get; set; } = ["txt", "json", "csv", "html", "md"];
    public bool Fuzzy { get; set; } = true;
    public bool DeepCorrupt { get; set; } = true;
    public int ContextWords { get; set; } = 75;
    public int MentionsPerFile { get; set; } = 12;

    // Фильтры (включаются по необходимости)
    public bool BoundaryFiltersEnabled { get; set; } = true;
    public bool ListingDensityEnabled { get; set; } = true;
    public bool BlacklistPhrasesEnabled { get; set; } = true;
    public bool MeaningfulnessEnabled { get; set; } = true;
    public bool CommonWordRuleEnabled { get; set; } = true;
    public bool TitleValidationEnabled { get; set; } = true;
    public bool CrossFileDeduplication { get; set; } = true;

    // Оформление
    /// <summary>Item 14: ключ темы — «light» (Академическая) или «dark» (Северный ветер).</summary>
    public string ThemeKey { get; set; } = "light";
    /// <summary>Масштаб интерфейса (0.8–1.6).</summary>
    public double UiZoom { get; set; } = 1.0;

    // Item 14: пользовательские цвета подсветки сущностей по ветвям
    // («#RRGGBB»; пусто — цвет из активной темы).
    public string Branch1Color { get; set; } = "";
    public string Branch2Color { get; set; } = "";
    public string Branch3Color { get; set; } = "";
    public string Branch4Color { get; set; } = "";
    public string Branch5Color { get; set; } = "";

    // Виртуальный корпус: дополнительные папки
    public List<string> ExtraCorpusDirs { get; set; } = [];

    // Форматы корпуса: какие расширения сканировать (по умолчанию — все поддерживаемые)
    public List<string> Extensions { get; set; } =
    [
        ".txt", ".md", ".html", ".htm", ".rtf", ".fb2", ".epub", ".docx",
        ".odt", ".pdf", ".doc", ".mobi", ".prc", ".azw", ".azw3", ".cbz",
        ".cbr", ".djvu", ".lit",
    ];

    // Оформление: свёрнутая навигационная рейка
    public bool RailCollapsed { get; set; }

    // Пользовательские regex-исключения
    public List<string> UserExcludePatterns { get; set; } = [];

    // R2-16: версия движка, для которой самопроверка уже пройдена
    // (null/отсутствие ключа в старом settings.json = проверить при старте).
    public string? LastSelfCheckVersion { get; set; }

    // R2-19: показанные контекстные подсказки (одноразовые туры).
    public List<string> ShownHints { get; set; } = [];

    // UX: недавние исследования .eaproj — продолжить работу одним кликом.
    public List<string> RecentProjects { get; set; } = [];

    // R1-7: недавно выполненные команды палитры (устойчивые ключи, новые сверху).
    public List<string> RecentPaletteCommands { get; set; } = [];

    // Кэш (страница «Обзор» → раздел «КЭШ»): жизненный цикл + L2.
    public bool CacheEnabled { get; set; } = true;
    public int CacheMaxAgeDays { get; set; } = 30;
    public long CacheMaxSizeMb { get; set; } = 2048;
    public bool MemoryCacheEnabled { get; set; } = true;
    public int MemoryCacheBudgetMb { get; set; } = 256;
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DirectoryPath => EntityAnalyzer.Core.Persistence.ProfilePaths.DirectoryPath;

    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Core.Logging.Audit.Read("settings", FilePath); // R2-23
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new();
                MigrateLegacyPaths(s);
                return s;
            }
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    /// <summary>
    /// Legacy configs carried Android paths like /storage/emulated/0/… which can
    /// never exist on Windows and silently killed the start button. Drop them.
    /// </summary>
    private static void MigrateLegacyPaths(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.CorpusDir) &&
            !Directory.Exists(s.CorpusDir) &&
            (s.CorpusDir.StartsWith('/') || s.CorpusDir.Contains("emulated", StringComparison.OrdinalIgnoreCase)))
        {
            s.CorpusDir = "";
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
            Core.Logging.Audit.Write("settings", FilePath); // R2-23
        }
        catch { /* non-fatal */ }
    }

    /// <summary>R2-11: настройки как JSON — для упаковки в .eaproj.</summary>
    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, JsonOpts);

    /// <summary>R2-11: записать готовый JSON настроек (например, из пакета .eaproj).
    /// Применение произойдёт при следующем запуске приложения.</summary>
    public static void WriteRaw(string json)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, json);
        }
        catch { /* non-fatal */ }
    }
}
