using System.ComponentModel;
using System.Text.Json.Serialization;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>A validated mention of an entity inside a file.</summary>
public sealed record Mention(
    int Num,
    string Entity,
    string Variant,
    int Line,
    string Context,
    string Theme = "",
    bool IsFuzzy = false,
    int Repeats = 1,
    long Offset = 0) : INotifyPropertyChanged   // R2-13: позиция совпадения в нормализованном тексте — ключ вердикта
{
    private bool _starred;

    /// <summary>
    /// Issue 39: цитата отмечена звёздочкой (цитатник). Состояние интерфейса:
    /// в JSON-кэш файлов не пишется, при смене уведомляет привязки WPF.
    /// </summary>
    [JsonIgnore]
    public bool Starred
    {
        get => _starred;
        set
        {
            if (_starred == value) return;
            _starred = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Starred)));
        }
    }

    private VerdictKind? _verdict;

    /// <summary>
    /// Действующий экспертный вердикт по этой находке. UI-состояние:
    /// источник истины — VerdictStore, значение восстанавливается при выборе
    /// файла и обновляется сразу после нажатия кнопки.
    /// </summary>
    [JsonIgnore]
    public VerdictKind? Verdict
    {
        get => _verdict;
        set
        {
            if (_verdict == value) return;
            _verdict = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Verdict)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasVerdict)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VerdictLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VerdictGlyph)));
        }
    }

    [JsonIgnore]
    public bool HasVerdict => _verdict.HasValue;

    [JsonIgnore]
    public string VerdictLabel => _verdict switch
    {
        VerdictKind.Confirmed => "подтверждено",
        VerdictKind.Rejected => "ложное срабатывание",
        VerdictKind.Uncertain => "спорно",
        _ => "",
    };

    [JsonIgnore]
    public string VerdictGlyph => _verdict switch
    {
        VerdictKind.Confirmed => "✓",
        VerdictKind.Rejected => "✗",
        VerdictKind.Uncertain => "?",
        _ => "",
    };

    /// <summary>Человеческое объяснение правила, по которому найдено упоминание.</summary>
    [JsonIgnore]
    public string MatchRule => IsFuzzy
        ? "нечёткое совпадение по словарю вариантов"
        : "точное совпадение по словарю вариантов";

    /// <summary>Все причины оценки достоверности — для карточки «Почему найдено?».</summary>
    [JsonIgnore]
    public string ConfidenceDetails => string.Join(" · ", ConfidenceBadge.From(this).Reasons);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Per-file analysis outcome.</summary>
public sealed record FileResult(
    string FileName,
    string FullPath,
    IReadOnlyList<Mention> Mentions,
    int TotalFound,
    int ContextSize,
    long CharCount,
    double DensityPer10k,
    string? Error = null,
    IReadOnlyList<SemanticMatch>? SemanticMatches = null)
{
    /// <summary>Находки семантических паттернов в этом файле (кэшируются вместе с файлом).</summary>
    public IReadOnlyList<SemanticMatch> SemanticMatches { get; init; } = SemanticMatches ?? [];
}

public sealed record AnalysisSummary(
    int TotalMentions,
    int FilesWithMentions,
    int UniqueEntities,
    long TotalChars,
    IReadOnlyDictionary<string, int> EntityCounts,
    IReadOnlyDictionary<string, int> GroupCounts) : IGroupCountsProvider
{
    public AnalysisSummary(int TotalMentions, int FilesWithMentions, int UniqueEntities,
        long TotalChars, IReadOnlyDictionary<string, int> EntityCounts)
        : this(TotalMentions, FilesWithMentions, UniqueEntities, TotalChars,
               EntityCounts, new Dictionary<string, int>()) { }
}
/// <summary>Позволяет читать группы без breaking-изменений в существующем коде.</summary>
public interface IGroupCountsProvider
{
    IReadOnlyDictionary<string, int> GroupCounts { get; }
}

public sealed record QualityReport(
    long RawMatches,
    int BoundaryRejected,
    int BrokenWordRejected,
    int ListingRejected,
    int CommonWordRejected,
    int TitleRejected,
    int FinalMentions,
    int Deduplicated,
    int CacheHits,
    int CacheMisses,
    IReadOnlyList<(string File, string Error)> Errors,
    string Duration,
    int BlacklistRejected = 0,
    int MeaningfulnessRejected = 0,
    int DuplicatesGrouped = 0,
    IReadOnlyDictionary<string, double>? StageMs = null,
    int FilesUnchangedSkipped = 0,
    int UserPatternRejected = 0,
    long BytesScanned = 0,
    int Utf8Files = 0,
    int Cp1251Files = 0,
    IReadOnlyList<SemanticPatternStat>? SemanticStats = null,
    int DisplayCapped = 0)
{
    /// <summary>Тайминги стадий в миллисекундах (read/match/filter/context/…).</summary>
    public IReadOnlyDictionary<string, double> StageMs { get; init; } = StageMs ?? new Dictionary<string, double>();

    /// <summary>Сводка сработавших семантических паттернов (пусто — если не было).</summary>
    public IReadOnlyList<SemanticPatternStat> SemanticStats { get; init; } = SemanticStats ?? [];

    /// <summary>Упоминания, скрытые лимитом цитат/файл (не отклонены фильтрами).</summary>
    public int DisplayCapped { get; init; } = DisplayCapped;

    /// <summary>Совпадения, схлопнутые резолвером перекрытий вариантов (QA N04).</summary>
    public int OverlapsResolved { get; init; }

    /// <summary>L2-кэш (память): попадания за прогон (0 — слой не использовался).</summary>
    public int L2Hits { get; init; }

    /// <summary>L2-кэш (память): промахи (запросы, ушедшие на диск), за прогон.</summary>
    public int L2Misses { get; init; }

    /// <summary>Тихие пропуски файлов — сводка в отчёте и консоли (QA N06).</summary>
    public int SkippedTooLarge { get; init; }
    public int SkippedEmpty { get; init; }
    public int SkippedDuplicate { get; init; }

    /// <summary>Суммарно отклонено всеми контекстными фильтрами.</summary>
    public int TotalFilterRejected =>
        BoundaryRejected + BrokenWordRejected + ListingRejected + CommonWordRejected +
        TitleRejected + BlacklistRejected + MeaningfulnessRejected + UserPatternRejected;

    /// <summary>
    /// Сверка воронки: сколько совпадений не объясняется учтёнными компонентами.
    /// После добавления OverlapsResolved всегда 0 — поле оставлено как страховка.
    /// </summary>
    public int FunnelLeftover =>
        (int)Math.Max(0, RawMatches - TotalFilterRejected - OverlapsResolved -
            DisplayCapped - Deduplicated - DuplicatesGrouped - FinalMentions);
    /// <summary>Share of raw automaton hits killed by the two boundary filters.</summary>
    public string FilterEfficiency =>
        RawMatches == 0
            ? "0%"
            : $"{Math.Round((BoundaryRejected + BrokenWordRejected) * 100.0 / RawMatches, 1):0.#}%";
}

public sealed record AnalysisProgress(
    int ProcessedFiles,
    int TotalFiles,
    string CurrentFile,
    string? LastEvent = null,
    double ElapsedSec = 0)
{
    /// <summary>Скорость обработки, файлов/сек.</summary>
    public double FilesPerSec => ElapsedSec > 0.5 ? ProcessedFiles / ElapsedSec : 0;
    /// <summary>Оценка оставшегося времени, сек.</summary>
    public double EtaSec => FilesPerSec > 0 ? Math.Max(0, (TotalFiles - ProcessedFiles) / FilesPerSec) : 0;
}

/// <summary>Группа файлов-дубликатов: Files[0] — оригинал, остальные — копии (SHA-256 контента).</summary>
public sealed record DuplicateGroup(string Hash, IReadOnlyList<string> Files)
{
    /// <summary>Число избыточных копий в группе (без оригинала).</summary>
    public int RedundantCount => Math.Max(0, Files.Count - 1);
}

/// <summary>Совпадение семантического паттерна (см. Core/Semantics).</summary>
public sealed record SemanticMatch(
    string PatternId,
    string PatternName,
    string FilePath,
    int Line,
    string Snippet,
    int Offset = 0);   // R1-8: позиция начала находки в нормализованном тексте — для live-превью

/// <summary>
/// Результат обособленного скана семантических паттернов — без основного
/// поиска сущностей: только конструкции, заданные паттернами.
/// </summary>
public sealed record PatternScanResult(
    IReadOnlyList<SemanticMatch> Matches,
    int FilesScanned,
    int FilesEmpty,
    TimeSpan Duration);

/// <summary>Сводка по одному паттерну за прогон.</summary>
public sealed record SemanticPatternStat(
    string PatternId,
    string PatternName,
    int Matches,
    int UniqueFiles);

public sealed record AnalysisSession(
    IReadOnlyList<FileResult> Files,
    AnalysisSummary Summary,
    QualityReport Quality,
    string PresetName,
    DateTimeOffset StartedAt = default,
    TimeSpan Duration = default,
    IReadOnlyList<DuplicateGroup>? DuplicateGroups = null,
    IReadOnlyList<SemanticMatch>? SemanticMatches = null)
{
    /// <summary>Группы текстовых дубликатов корпуса (пусто, если дубликатов нет).</summary>
    public IReadOnlyList<DuplicateGroup> DuplicateGroups { get; init; } = DuplicateGroups ?? [];
    /// <summary>Находки семантических паттернов за прогон.</summary>
    public IReadOnlyList<SemanticMatch> SemanticMatches { get; init; } = SemanticMatches ?? [];
}
