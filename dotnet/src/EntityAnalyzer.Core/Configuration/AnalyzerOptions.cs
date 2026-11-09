namespace EntityAnalyzer.Core.Configuration;

public sealed class AnalyzerOptions
{
    public CorpusOptions Corpus { get; set; } = new();
    public OutputOptions Output { get; set; } = new();
    public CacheOptions Cache { get; set; } = new();
    public ProcessingOptions Processing { get; set; } = new();
    public SourceQualityOptions SourceQuality { get; set; } = new();

    /// <summary>Theme name → description, imported from legacy config or edited in UI.</summary>
    public Dictionary<string, string> Themes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>User-defined presets overriding built-ins.</summary>
    public Dictionary<string, PresetDefinition> CustomPresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ActivePreset { get; set; } = "extended";
}

public sealed class CorpusOptions
{
    public string Dir { get; set; } = "";
    /// <summary>Виртуальный корпус: дополнительные папки, сканируемые вместе с основной.</summary>
    public List<string> ExtraDirs { get; set; } = [];
    /// <summary>Поддерживаемые форматы: полный список для UI и discovery.</summary>
    public static readonly string[] AllExtensions =
    [
        ".txt", ".md", ".html", ".htm", ".rtf", ".fb2", ".epub", ".docx",
        ".odt", ".pdf", ".doc", ".mobi", ".prc", ".azw", ".azw3", ".cbz",
        ".cbr", ".djvu", ".lit",
    ];

    public List<string> Extensions { get; set; } = [.. AllExtensions];
    public bool Recursive { get; set; } = true;
    public int MaxFileSizeMb { get; set; } = 100;
    public bool SkipDuplicates { get; set; } = true;
    /// <summary>
    /// R1-5: включить потоковую обработку файлов, превышающих MaxFileSizeMb.
    /// По умолчанию false — воспроизводимость существующих прогонов.
    /// При true: большие файлы читаются чанками предложений (StreamedSentenceReader),
    /// анализируются независимо и результаты мерджатся в один FileResult.
    /// Препроцессоры для чанков не применяются (ограничение).
    /// </summary>
    public bool StreamLargeFiles { get; set; } = false;
}

public sealed class OutputOptions
{
    public string Dir { get; set; } = "output";
    public HashSet<string> Formats { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "txt", "json" };
    public bool SaveQualityReport { get; set; } = true;
    public bool PerEntityFolders { get; set; }
}

/// <summary>
/// File-level source-quality overrides. When no explicit path is given, the
/// engine looks for <c>entities_source_quality.csv</c> in Output.Dir; an absent
/// optional file preserves detector-only behaviour.
/// </summary>
public sealed class SourceQualityOptions
{
    /// <summary>Explicit sidecar path. Empty = auto-discover in Output.Dir.</summary>
    public string SidecarPath { get; set; } = "";

    /// <summary>Fail instead of silently falling back to detector-only mode.</summary>
    public bool RequireSidecar { get; set; }
}

public sealed class CacheOptions
{
    public bool Enabled { get; set; } = true;
    public string Dir { get; set; } = ".cache";
    public int MaxAgeDays { get; set; } = 30;
    public bool InvalidateOnConfigChange { get; set; } = true;

    /// <summary>Лимит объёма дискового кэша, МБ (0 = без лимита).
    /// При переполнении вытесняются самые старые записи.</summary>
    public long MaxSizeMb { get; set; } = 2048;

    /// <summary>L2: оперативный кэш результатов в памяти процесса — повторные
    /// прогоны в рамках одной сессии не касаются диска.</summary>
    public bool MemoryEnabled { get; set; } = true;

    /// <summary>Бюджет L2-кэша, МБ (0 = L2 выключен, см. MemoryEnabled).</summary>
    public int MemoryBudgetMb { get; set; } = 256;
}

public sealed class ProcessingOptions
{
    /// <summary>0 = use all cores.</summary>
    public int MaxWorkers { get; set; }
    /// <summary>0 = unlimited.</summary>
    public int MaxMentionsPerFile { get; set; } = 12;
    public int FileReadTimeoutSeconds { get; set; } = 120;

    public bool FuzzyEnabled { get; set; }
    public int FuzzyThreshold { get; set; } = 85;
    public bool DeepCorrupt { get; set; } = true;

    public int ContextMaxWords { get; set; } = 75;
    public int ContextMinWords { get; set; } = 25;
    public int ContextThreshold { get; set; } = 50;

    public List<string> MythSignalWords { get; set; } =
    [
        "бог", "древний", "великий", "внешний", "ужас", "культ", "лавкрафт",
        "ктулху", "некрономикон", "миф", "хаос", "бездна", "врата", "демон",
        "god", "ancient", "great", "old", "elder", "cthulhu", "mythos", "demon"
    ];

    // ─── Переключатели фильтров (всё включается по необходимости) ───

    public bool BoundaryFiltersEnabled { get; set; } = true;
    public bool ListingDensityEnabled { get; set; } = true;
    public bool BlacklistPhrasesEnabled { get; set; } = true;
    public bool MeaningfulnessEnabled { get; set; } = true;
    public bool CommonWordRuleEnabled { get; set; } = true;
    public bool TitleValidationEnabled { get; set; } = true;
    public bool CrossFileDeduplication { get; set; } = true;

    /// <summary>Семантические паттерны: параллельный поиск структурных конструкций.</summary>
    public bool SemanticPatternsEnabled { get; set; } = true;
    /// <summary>Максимум находок одного паттерна в одном файле.</summary>
    public int SemanticMaxPerPatternPerFile { get; set; } = 50;

    /// <summary>
    /// R1-13: имена препроцессоров текста (см. TextPreprocessorRegistry).
    /// Пусто = выкл — поведение по умолчанию не меняется, эталон самопроверки стабилен.
    /// </summary>
    public List<string> Preprocessors { get; set; } = [];

    /// <summary>Фразы-маркеры списков и оглавлений (проверяются в голове контекста).</summary>
    public List<string> ListingBlacklistPhrases { get; set; } =
    [
        "великие ужасы и тайны древнего мира",
        "великие древние и древние",
        "оглавление",
        "содержание:",
        "полный список сущностей",
        "пантеон:",
        "great old ones:",
        "table of contents",
        "list of great old ones",
    ];

    /// <summary>Доля коротких сегментов, при которой контекст считается перечислением.</summary>
    public double EnumerationMaxSegmentRatio { get; set; } = 0.55;
    /// <summary>Слов в сегменте, чтобы сегмент считался «именной строкой».</summary>
    public int EnumerationMaxSegmentWords { get; set; } = 4;
    /// <summary>Минимальная доля служебных слов для признания контекста прозой.</summary>
    public double MinFunctionWordRatio { get; set; } = 0.06;

    // ─── Пользовательские regex-исключения ───

    public bool UserExclusionsEnabled { get; set; } = true;
    public List<string> UserExcludePatterns { get; set; } = [];

    // ─── Инкрементальный режим ───

    public bool IncrementalEnabled { get; set; } = true;

    // ─── Морфология ───

    /// <summary>Генерировать русские падежные формы для кириллических вариантов.</summary>
    public bool RussianMorphologyEnabled { get; set; } = true;

    public HashSet<string> ToSignalWordSet() =>
        new(MythSignalWords.Where(w => !string.IsNullOrWhiteSpace(w)), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// R3: выбор сущностей для селективного анализа. Пусто = анализировать все.
    /// Содержит ID карточек кодекса (EntityCard.Id).
    /// </summary>
    public HashSet<string> SelectedEntityIds { get; set; } = [];
}

/// <summary>A named analysis intensity profile.</summary>
public sealed record PresetDefinition(
    string Name,
    string Description,
    int ContextWords,
    int MentionsPerFile,
    bool Fuzzy,
    bool DeepCorrupt);

public static class Presets
{
    /// <summary>Built-in profiles mirroring the original Python menu (SBERT dropped — offline engine).</summary>
    public static readonly IReadOnlyDictionary<string, PresetDefinition> All =
        new Dictionary<string, PresetDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["minimal"] = new("minimal", "Только канонические имена, минимальный контекст", 25, 3, false, false),
            ["light"]   = new("light", "Быстрый поиск с базовой генерацией", 40, 5, false, false),
            ["medium"]  = new("medium", "Стандартный анализ + fuzzy-поиск", 60, 8, true, true),
            ["extended"]= new("extended", "Расширенный поиск [по умолчанию]", 75, 12, true, true),
            ["heavy"]   = new("heavy", "Глубокий анализ", 100, 20, true, true),
            ["maximum"] = new("maximum", "Максимальный охват", 150, 50, true, true),
            ["titanic"] = new("titanic", "Полный анализ без ограничений", 200, 0, true, true),
        };

    public static PresetDefinition Resolve(string name, AnalyzerOptions? options = null)
    {
        if (options is not null && options.CustomPresets.TryGetValue(name, out var custom)) return custom;
        return All.TryGetValue(name, out var p) ? p : All["extended"];
    }

    public static void Apply(PresetDefinition preset, ProcessingOptions target)
    {
        target.ContextMaxWords = preset.ContextWords;
        target.MaxMentionsPerFile = preset.MentionsPerFile;
        target.FuzzyEnabled = preset.Fuzzy;
        target.DeepCorrupt = preset.DeepCorrupt;
    }
}
