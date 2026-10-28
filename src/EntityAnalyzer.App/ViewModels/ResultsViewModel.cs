using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Exporting;
using EntityAnalyzer.Core.Logging;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Themes;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>Results browser: files → mentions with highlighted contexts.</summary>
public sealed class ResultsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, string> _hashByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly VerdictStore _verdicts = VerdictStore.Default;
    private TrustViewClassifier _trust = TrustViewClassifier.Empty;
    private ReviewSummary _corpusReview = new(0, 0, 0);

    /// <summary>Explicit exploratory view; never the default and never a silent filter.</summary>
    public const string TrustedMode = "Trusted (exploratory)";

    /// <summary>Conservative additive view: exact canonical names only.</summary>
    public const string StrictCanonicalMode = "Canonical only (exploratory)";

    // Отмена последних вердиктов: храним предыдущее состояние, а не только
    // тип — иначе после «подтверждено → ложное → отмена» терялся бы первый вердикт.
    // Одна запись — одно действие пользователя: одиночный вердикт или массовый.
    private readonly List<VerdictUndo> _verdictUndo = [];
    private const int MaxVerdictUndo = 100;

    private sealed record VerdictUndo(IReadOnlyList<VerdictUndoItem> Items);
    private sealed record VerdictUndoItem(Mention Mention, FileResult File, string FileHash, VerdictKind? Previous);

    public ObservableCollection<FileResult> Files { get; } = [];
    public ObservableCollection<Mention> Mentions { get; } = [];

    /// <summary>R1-6: плитки heat map корпуса.</summary>
    public ObservableCollection<HeatTile> HeatTiles { get; } = [];
    public bool HasHeatTiles => HeatTiles.Count > 0;

    private FileResult? _selectedFile;
    public FileResult? SelectedFile
    {
        get => _selectedFile;
        set
        {
            // Смена файла применяет действующие фильтры (поиск, режим, тема+сущность).
            if (Set(ref _selectedFile, value)) ReloadSelectedFileMentions();
        }
    }

    private Mention? _selectedMention;
    public Mention? SelectedMention
    {
        get => _selectedMention;
        set
        {
            if (Set(ref _selectedMention, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Сколько цитат текущего файла уже получили вердикт (без учёта фильтра).</summary>
    public int ReviewedInFile => SelectedFile?.Mentions.Count(m => m.HasVerdict) ?? 0;

    /// <summary>Сколько всего цитат в текущем файле (без учёта фильтра).</summary>
    public int ReviewableInFile => SelectedFile?.Mentions.Count ?? 0;

    public string ReviewProgressText => ReviewableInFile == 0
        ? "Нет цитат для проверки"
        : $"Проверено {ReviewedInFile} из {ReviewableInFile}";

    public bool HasReviewWork => ReviewableInFile > 0;
    public bool CanUndoVerdict => _verdictUndo.Count > 0;

    /// <summary>Сколько цитат всего корпуса уже размечено.</summary>
    public int ReviewedInCorpus => _corpusReview.Reviewed;

    /// <summary>Сколько всего цитат в корпусе.</summary>
    public int ReviewableInCorpus => _corpusReview.Total;

    /// <summary>Сколько файлов ещё содержат непроверенные цитаты.</summary>
    public int FilesWithUnreviewedMentions => _corpusReview.FilesWithUnreviewed;

    public bool HasUnreviewedFiles => _corpusReview.FilesWithUnreviewed > 0;

    public string CorpusReviewProgressText => _corpusReview.Total == 0
        ? "В корпусе нет цитат"
        : _corpusReview.IsComplete
            ? $"Корпус проверен: {_corpusReview.Reviewed} из {_corpusReview.Total}"
            : $"По корпусу: {_corpusReview.Reviewed} из {_corpusReview.Total} · осталось файлов: {_corpusReview.FilesWithUnreviewed}";

    private void RefreshCorpusReview()
    {
        _corpusReview = ReviewNavigator.Summarize(Files);
        // Подсказка на «Выгрузке» зависит от тех же вердиктов; держим её
        // синхронной с прогрессом, не дожидаясь повторного входа на страницу.
        _main.Export.RefreshReviewInfo();
    }

    private void NotifyReviewProgress()
    {
        OnPropertyChanged(nameof(ReviewedInFile));
        OnPropertyChanged(nameof(ReviewableInFile));
        OnPropertyChanged(nameof(ReviewProgressText));
        OnPropertyChanged(nameof(HasReviewWork));
        OnPropertyChanged(nameof(CanUndoVerdict));
        OnPropertyChanged(nameof(ReviewedInCorpus));
        OnPropertyChanged(nameof(ReviewableInCorpus));
        OnPropertyChanged(nameof(FilesWithUnreviewedMentions));
        OnPropertyChanged(nameof(HasUnreviewedFiles));
        OnPropertyChanged(nameof(CorpusReviewProgressText));
        CommandManager.InvalidateRequerySuggested();
    }

    private string _search = "";
    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            RefreshFileFilter();
            ReloadSelectedFileMentions();
        }
    }

    private bool _onlyWithResults = true;
    public bool OnlyWithResults
    {
        get => _onlyWithResults;
        set { if (Set(ref _onlyWithResults, value)) RefreshFileFilter(); }
    }

    // ─── R2-21: изоляция кликом ───
    // Клик по имени файла → показывается только этот файл; хлебная крошка снимает.

    private FileResult? _isolatedFile;
    /// <summary>Изолированный файл (null — фильтр снят).</summary>
    public FileResult? IsolatedFile
    {
        get => _isolatedFile;
        private set
        {
            if (!Set(ref _isolatedFile, value)) return;
            OnPropertyChanged(nameof(IsFileIsolated));
            OnPropertyChanged(nameof(IsolationBreadcrumb));
            RefreshFileFilter();
        }
    }
    public bool IsFileIsolated => IsolatedFile is not null;
    public string IsolationBreadcrumb => IsolatedFile is null ? "" : $"Только файл: {IsolatedFile.FileName}";

    public RelayCommand IsolateFileCommand { get; }
    public RelayCommand ClearIsolationCommand { get; }

    // ─── R2-13: вердикты пользователя ───

    public RelayCommand ConfirmMentionCommand { get; }
    public RelayCommand RejectMentionCommand { get; }
    public RelayCommand UncertainMentionCommand { get; }

    /// <summary>Ctrl+Z / кнопка «Отменить»: вернуть предыдущий вердикт выделенной находки.</summary>
    public RelayCommand UndoVerdictCommand { get; }

    /// <summary>Массовые вердикты по видимой (отфильтрованной) выборке.</summary>
    public RelayCommand BulkConfirmCommand { get; }
    public RelayCommand BulkRejectCommand { get; }

    /// <summary>Перейти к следующему файлу, в котором ещё есть непроверенные цитаты.</summary>
    public RelayCommand NextUnreviewedFileCommand { get; }

    /// <summary>Открыть исходный файл на выбранной находке.</summary>
    public RelayCommand OpenMentionSourceCommand { get; }

    /// <summary>Режим фильтра упоминаний: вердикт, точность совпадения или тема.</summary>
    public IReadOnlyList<string> FilterModes { get; } =
    [
        VerdictFilter.All,
        VerdictFilter.Unreviewed,
        VerdictFilter.Confirmed,
        VerdictFilter.Rejected,
        VerdictFilter.Uncertain,
        "Точные",
        "Fuzzy",
        "Тема…",
        TrustedMode,
        StrictCanonicalMode,
    ];

    private string _selectedMode = VerdictFilter.All;
    public string SelectedMode
    {
        get => _selectedMode;
        set { if (Set(ref _selectedMode, value)) ReloadSelectedFileMentions(); }
    }

    // ─── Группа 3 (item 11): автотеги файлов по преобладающим темам ───

    private const string AllTagsKey = "Все теги";

    private Dictionary<string, HashSet<string>> _tagsByPath = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<string> TagFilters { get; } = [AllTagsKey];

    private string _tagFilter = AllTagsKey;
    public string TagFilter
    {
        get => _tagFilter;
        set { if (Set(ref _tagFilter, string.IsNullOrEmpty(value) ? AllTagsKey : value)) RefreshFileFilter(); }
    }

    // ─── Группа 3 (R3-1, item 52): фильтр по теме и сущности ───
    // Тема — словарь (встроенный или из user_themes.json); слова ищутся в
    // контекстах упоминаний с морфологией. Сущность — отбор по имени.

    private const string AllThemesKey = "Все темы";
    private const string AllEntitiesKey = "Все сущности";

    public ObservableCollection<string> ThemeFilters { get; } = [AllThemesKey];
    public ObservableCollection<string> EntityFilters { get; } = [AllEntitiesKey];

    private IReadOnlyList<string> _themeFilterWords = [];

    /// <summary>Слова выбранной темы (пусто — фильтр снят); используются подсветкой предпросмотра.</summary>
    public IReadOnlyList<string> ThemeFilterWords => _themeFilterWords;

    private string _themeFilter = AllThemesKey;
    public string ThemeFilter
    {
        get => _themeFilter;
        set
        {
            if (!Set(ref _themeFilter, string.IsNullOrEmpty(value) ? AllThemesKey : value)) return;
            _themeFilterWords = LookupThemeWords(_themeFilter);
            OnPropertyChanged(nameof(ThemeFilterWords));
            RefreshFileFilter();
            ReloadSelectedFileMentions();
        }
    }

    private string _entityFilter = AllEntitiesKey;
    public string EntityFilter
    {
        get => _entityFilter;
        set
        {
            if (!Set(ref _entityFilter, string.IsNullOrEmpty(value) ? AllEntitiesKey : value)) return;
            RefreshFileFilter();
            ReloadSelectedFileMentions();
        }
    }

    private static IReadOnlyList<string> LookupThemeWords(string theme)
    {
        if (theme == AllThemesKey) return [];
        return ThemeCatalog.Merge(ThemeStore.Default.Load())
            .FirstOrDefault(t => string.Equals(t.Name, theme, StringComparison.OrdinalIgnoreCase))?.Words
            ?? (IReadOnlyList<string>)[];
    }

    public ICollectionView FilesView { get; }

    /// <summary>Empty State: анализ не запускался или не дал файлов.</summary>
    public bool IsDataEmpty => Files.Count == 0;

    public RelayCommand CopyContext { get; }
    public RelayCommand OpenFullText { get; }
    public event Action<string, IReadOnlyList<Mention>>? FullTextRequested;

    // ─── Quote mode (Issue 26 / Группа 2) ───

    /// <summary>
    /// Цитатник: отметки живут в постоянном файле quotes.jsonl (QuoteStore);
    /// состояние на самих цитатах — лишь отражение книги для текущей сессии.
    /// </summary>
    private readonly QuoteStore _quotes = QuoteStore.Default;

    /// <summary>Проверяет, отмечена ли цитата звёздочкой (состояние живёт в самом Mention).</summary>
    public static bool IsStarred(Mention m) => m.Starred;

    /// <summary>Число цитат в книге (quotes.jsonl) — для панели цитатника.</summary>
    public int StarredCount => _quotes.StarredCount;

    /// <summary>Переключить звёздочку для цитаты.</summary>
    public RelayCommand ToggleStarCommand { get; }

    /// <summary>Item 39: Ctrl+S — отметить/снять звёздочку у выбранной цитаты.</summary>
    public RelayCommand ToggleSelectedStarCommand { get; }

    /// <summary>Экспорт книги (все отмеченные цитаты) в Markdown.</summary>
    public RelayCommand ExportStarredQuotesCommand { get; }

    /// <summary>Очистить все звёздочки (с подтверждением — книга постоянная).</summary>
    public RelayCommand ClearAllStarsCommand { get; }

    /// <summary>Группа 2: открыть раздел «Цитатник».</summary>
    public RelayCommand OpenQuoteBookCommand { get; }

    /// <summary>Группа 3 (item 24): открыть «Паспорт файла» для строки списка.</summary>
    public RelayCommand OpenPassportCommand { get; }

    /// <summary>Группа 2 (item 30): заметка дневника с привязкой к цитате.</summary>
    public RelayCommand AddDiaryNoteCommand { get; }

    public ResultsViewModel(MainViewModel main)
    {
        _main = main;
        FilesView = System.Windows.Data.CollectionViewSource.GetDefaultView(Files);
        CopyContext = new RelayCommand(_ =>
        {
            if (SelectedMention is { } m)
                Clipboard.SetText($"{m.Entity} ({m.Variant}) L{m.Line}\n{m.Context}");
        }, _ => SelectedMention is not null);
        OpenFullText = new RelayCommand(_ =>
        {
            if (SelectedFile is { } f)
                FullTextRequested?.Invoke(f.FullPath, f.Mentions);
        }, _ => SelectedFile is not null);

        IsolateFileCommand = new RelayCommand(p =>
        {
            if (p is FileResult f) IsolatedFile = f;
        }, p => p is FileResult);
        ClearIsolationCommand = new RelayCommand(_ => IsolatedFile = null);

        ConfirmMentionCommand = new RelayCommand(
            p => SaveVerdict(p, VerdictKind.Confirmed), CanSaveVerdict);
        RejectMentionCommand = new RelayCommand(
            p => SaveVerdict(p, VerdictKind.Rejected), CanSaveVerdict);
        UncertainMentionCommand = new RelayCommand(
            p => SaveVerdict(p, VerdictKind.Uncertain), CanSaveVerdict);
        UndoVerdictCommand = new RelayCommand(_ => UndoLastVerdict(),
            _ => _verdictUndo.Count > 0);
        BulkConfirmCommand = new RelayCommand(_ => ApplyBulkVerdict(VerdictKind.Confirmed),
            _ => Mentions.Count > 0 && SelectedFile is not null);
        BulkRejectCommand = new RelayCommand(_ => ApplyBulkVerdict(VerdictKind.Rejected),
            _ => Mentions.Count > 0 && SelectedFile is not null);
        NextUnreviewedFileCommand = new RelayCommand(_ => GoToNextUnreviewedFile(),
            _ => HasUnreviewedFiles);
        OpenMentionSourceCommand = new RelayCommand(
            p => OpenMentionSource(p as Mention), p => p is Mention && SelectedFile is not null);

        // Quote mode commands
        ToggleStarCommand = new RelayCommand(p =>
        {
            if (p is Mention m && SelectedFile is { } file)
                ToggleStar(m, file);
        }, p => p is Mention && SelectedFile is not null);

        // Item 39: горячая клавиша Ctrl+S для выбранной цитаты (фокус клавиатуры
        // может быть в списке — команда работает через SelectedMention).
        ToggleSelectedStarCommand = new RelayCommand(_ =>
        {
            if (SelectedMention is { } m && SelectedFile is { } file)
                ToggleStar(m, file);
        }, _ => SelectedMention is not null && SelectedFile is not null);

        ExportStarredQuotesCommand = new RelayCommand(_ => ExportStarredQuotes(),
            _ => StarredCount > 0);

        ClearAllStarsCommand = new RelayCommand(_ => ClearAllStars(),
            _ => StarredCount > 0);

        OpenQuoteBookCommand = new RelayCommand(_ => _main.Navigate(_main.QuoteBook));

        // Группа 3 (item 24): кнопка «Паспорт» в строке файла.
        OpenPassportCommand = new RelayCommand(p => OpenPassport(p as FileResult), p => p is FileResult);

        // Группа 2 (item 30): кнопка «✎» у цитаты — заметка с привязкой.
        AddDiaryNoteCommand = new RelayCommand(p =>
        {
            if (p is Mention m && SelectedFile is { } file)
                AddDiaryNote(m, file);
        }, p => p is Mention && SelectedFile is not null);
    }

    internal void LoadSession(AnalysisSession session)
    {
        // Группа 3 (item 11): автотеги файлов — до наполнения списка, чтобы
        // строки файлов сразу получили теги из общего реестра.
        ApplyTags(session);

        // R3-1 (item 52): списки тем (словари) и сущностей (новая сессия).
        RefreshThemeAndEntityFilters(session);

        // Exploratory trusted view: catalog-derived, rebuilt per session.
        _trust = TrustViewClassifier.FromCodex(_main.Codex);

        Files.Clear();
        Mentions.Clear();
        _verdictUndo.Clear();
        _hashByPath.Clear();
        IsolatedFile = null; // новая сессия — старые фильтры не имеют смысла
        foreach (var f in session.Files) Files.Add(f);

        // Восстанавливаем всю разметку сразу: без этого прогресс по корпусу
        // и переход к следующему непроверенному файлу видели бы только
        // текущий файл. Хэши берутся из инкрементального индекса, если он есть.
        foreach (var m in session.Files.SelectMany(f => f.Mentions)) m.Verdict = null;
        VerdictResolver.ApplyTo(session.Files, _verdicts, GetFileHash);

        SelectedFile = session.Files.FirstOrDefault(f => f.Mentions.Count > 0) ?? session.Files.FirstOrDefault();
        RefreshFileFilter();
        RefreshCorpusReview();
        NotifyReviewProgress();
        OnPropertyChanged(nameof(IsDataEmpty));

        // Группа 2 (item 26): вернуть звёздочки книги (quotes.jsonl) на цитаты сессии.
        RestoreStars();

        // R1-6: heat map корпуса из той же сессии.
        HeatTiles.Clear();
        foreach (var t in HeatmapModel.Build(session, Services.BranchResolver.ResolveIndex))
            HeatTiles.Add(t);
        OnPropertyChanged(nameof(HasHeatTiles));
    }

    private void RefreshFileFilter()
    {
        if (FilesView is null) return;
        var q = Search?.Trim() ?? "";

        FilesView.Filter = o =>
        {
            if (o is not FileResult f) return false;
            // R2-21: изоляция кликом главнее остальных условий.
            if (IsolatedFile is not null) return ReferenceEquals(f, IsolatedFile);
            // Группа 3 (item 11): фильтр по автотегу файла.
            if (_tagFilter != AllTagsKey &&
                (!_tagsByPath.TryGetValue(f.FullPath, out var tags) || !tags.Contains(_tagFilter)))
                return false;
            // R3-1 (item 52): тема + сущность — файл проходит, если есть
            // упоминание, подходящее под оба условия (слова ищутся в контексте).
            if ((_themeFilterWords.Count > 0 || _entityFilter != AllEntitiesKey) &&
                !f.Mentions.Any(m =>
                    (_entityFilter == AllEntitiesKey ||
                     string.Equals(m.Entity, _entityFilter, StringComparison.OrdinalIgnoreCase)) &&
                    (_themeFilterWords.Count == 0 ||
                     ThemeMatcher.MatchWords(m.Context, _themeFilterWords).Count > 0)))
                return false;
            if (OnlyWithResults && f.Mentions.Count == 0 && string.IsNullOrEmpty(q)) return false;
            if (q.Length == 0) return true;
            return f.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || f.Mentions.Any(m =>
                    m.Context.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.Variant.Contains(q, StringComparison.OrdinalIgnoreCase));
        };
    }

    /// <summary>
    /// Группа 3 (R3-1, item 52): пересобрать списки тем (встроенные словари +
    /// user_themes.json) и сущностей (из новой сессии), сохранив выбор.
    /// </summary>
    private void RefreshThemeAndEntityFilters(AnalysisSession session)
    {
        var currentTheme = _themeFilter;
        ThemeFilters.Clear();
        ThemeFilters.Add(AllThemesKey);
        foreach (var theme in ThemeCatalog.Merge(ThemeStore.Default.Load()))
            ThemeFilters.Add(theme.Name);
        _themeFilter = ThemeFilters.Contains(currentTheme) ? currentTheme : AllThemesKey;
        _themeFilterWords = LookupThemeWords(_themeFilter);
        OnPropertyChanged(nameof(ThemeFilter));
        OnPropertyChanged(nameof(ThemeFilterWords));

        var currentEntity = _entityFilter;
        EntityFilters.Clear();
        EntityFilters.Add(AllEntitiesKey);
        foreach (var name in session.Files
                     .SelectMany(f => f.Mentions)
                     .Select(m => m.Entity)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.Ordinal))
            EntityFilters.Add(name);
        _entityFilter = EntityFilters.Contains(currentEntity) ? currentEntity : AllEntitiesKey;
        OnPropertyChanged(nameof(EntityFilter));
    }

    /// <summary>
    /// Группа 3 (item 11): пересобрать автотеги файлов и список тегов для фильтра.
    /// </summary>
    private void ApplyTags(AnalysisSession session)
    {
        var tags = FileTagBuilder.Build(session);
        _tagsByPath = tags.ToDictionary(
            t => t.FullPath,
            t => t.Tags.ToHashSet(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
        FileTagsRegistry.Set(tags.ToDictionary(
            t => t.FullPath,
            t => t.Tags.Count == 0 ? "" : "Теги: " + string.Join(" · ", t.Tags),
            StringComparer.OrdinalIgnoreCase));

        var current = _tagFilter;
        TagFilters.Clear();
        TagFilters.Add(AllTagsKey);
        foreach (var tag in FileTagBuilder.AllTags(tags)) TagFilters.Add(tag);
        _tagFilter = TagFilters.Contains(current) ? current : AllTagsKey;
        OnPropertyChanged(nameof(TagFilter));
    }

    private void ReloadSelectedFileMentions()
    {
        var file = _selectedFile;
        if (LoadStoredVerdicts(file)) RefreshCorpusReview();
        Mentions.Clear();
        var q = Search?.Trim() ?? "";
        foreach (var m in file?.Mentions ?? [])
            if (MentionPasses(m, file, q))
                Mentions.Add(m);
        NotifyReviewProgress();
    }

    /// <summary>
    /// Вернуть вердикты из verdicts.jsonl на цитаты выбранного файла.
    /// Хеш считается один раз на файл и кэшируется; если журнал пуст,
    /// диск вообще не читается. Возвращает true, если состояние изменилось.
    /// </summary>
    private bool LoadStoredVerdicts(FileResult? file)
    {
        if (file is null) return false;

        var changed = false;
        if (_verdicts.All.Count == 0)
        {
            foreach (var m in file.Mentions)
            {
                if (m.Verdict is null) continue;
                m.Verdict = null;
                changed = true;
            }
            return changed;
        }

        var hash = GetFileHash(file.FullPath);
        foreach (var m in file.Mentions)
        {
            var offset = m.Offset > 0 ? m.Offset : m.Line;
            var verdict = _verdicts.Find(hash, offset, m.Variant)?.Verdict;
            if (m.Verdict == verdict) continue;
            m.Verdict = verdict;
            changed = true;
        }
        return changed;
    }

    /// <summary>Проходит ли упоминание сквозь фильтры поиска, режима, темы и сущности (R3-1).</summary>
    private bool MentionPasses(Mention m, FileResult? file, string q)
    {
        if (!VerdictFilter.Matches(m.Verdict, SelectedMode)) return false;
        if (SelectedMode == "Точные" && m.IsFuzzy) return false;
        if (SelectedMode == "Fuzzy" && !m.IsFuzzy) return false;
        if (SelectedMode == "Тема…" && string.IsNullOrEmpty(m.Theme)) return false;
        if (SelectedMode is TrustedMode or StrictCanonicalMode)
        {
            if (!SourceQuality.IsUsable(file?.SourceQuality)) return false;
            var assessment = _trust.Assess(m);
            if (SelectedMode == TrustedMode && !assessment.IsTrusted) return false;
            if (SelectedMode == StrictCanonicalMode && assessment.Level != TrustViewLevel.Canonical) return false;
        }
        if (_entityFilter != AllEntitiesKey &&
            !string.Equals(m.Entity, _entityFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (_themeFilterWords.Count > 0 &&
            ThemeMatcher.MatchWords(m.Context, _themeFilterWords).Count == 0) return false;
        if (q.Length > 0 &&
            !m.Context.Contains(q, StringComparison.OrdinalIgnoreCase) &&
            !m.Variant.Contains(q, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    // ─── Quote mode helpers (Группа 2, item 26: книга в quotes.jsonl) ───

    private static string MakeStarKey(Mention m, FileResult file, string fileHash)
    {
        var offset = m.Offset > 0 ? m.Offset : m.Line;
        return $"{fileHash}|{offset}|{m.Variant}";
    }

    /// <summary>Позиция цитаты внутри файла — совпадает с полем Offset записей книги.</summary>
    private static string OffsetKey(Mention m) => (m.Offset > 0 ? m.Offset : m.Line) + "|" + m.Variant;

    private void ToggleStar(Mention m, FileResult file)
    {
        // Item 39: кэш хешей — файл читается один раз, а не при каждом клике.
        var hash = GetFileHash(file.FullPath);
        var key = MakeStarKey(m, file, hash);
        if (m.Starred)
        {
            m.Starred = false;
            // Снимаем именно действующую запись книги: хеш файла мог измениться.
            _quotes.Unstar(FindStoredStarKey(file, m) ?? key);
        }
        else
        {
            // Запись того же места от прежней версии файла (другой хеш) — убрать,
            // чтобы в книге не было дублей одной цитаты.
            var offsetKey = OffsetKey(m);
            foreach (var q in _quotes.AllStarred())
                if (q.Key != key && SameLocation(q, file.FullPath, offsetKey))
                    _quotes.Unstar(q.Key);

            m.Starred = true;
            _quotes.SetStar(new QuoteRecord(
                DateTimeOffset.Now, key, true,
                FileHash: hash,
                Offset: m.Offset > 0 ? m.Offset : m.Line,
                Variant: m.Variant,
                Entity: m.Entity,
                FileName: file.FileName,
                FilePath: file.FullPath,
                Line: m.Line,
                Context: m.Context,
                Theme: m.Theme,
                IsFuzzy: m.IsFuzzy));
        }
        StarCountChanged();
    }

    /// <summary>Ключ действующей записи книги для этого места файла (может отличаться хешем).</summary>
    private string? FindStoredStarKey(FileResult file, Mention m)
    {
        var offsetKey = OffsetKey(m);
        return _quotes.AllStarred()
            .FirstOrDefault(q => SameLocation(q, file.FullPath, offsetKey))?.Key;
    }

    private static bool SameLocation(QuoteRecord q, string fullPath, string offsetKey) =>
        string.Equals(q.FilePath, fullPath, StringComparison.OrdinalIgnoreCase) &&
        q.Offset + "|" + q.Variant == offsetKey;

    /// <summary>Индекс книги: путь файла → множество «Offset|Variant».</summary>
    private static Dictionary<string, HashSet<string>> BuildStarIndex(IReadOnlyList<QuoteRecord> starred)
    {
        var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in starred)
        {
            if (string.IsNullOrEmpty(q.FilePath)) continue;
            if (!index.TryGetValue(q.FilePath, out var set))
                index[q.FilePath] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(q.Offset + "|" + q.Variant);
        }
        return index;
    }

    /// <summary>Отметить звёздочками цитаты текущей сессии, отмеченные в прошлых сеансах.</summary>
    private void RestoreStars()
    {
        var index = BuildStarIndex(_quotes.AllStarred());
        if (index.Count > 0)
        {
            foreach (var file in Files)
            {
                if (!index.TryGetValue(file.FullPath, out var set)) continue;
                foreach (var m in file.Mentions)
                    if (set.Contains(OffsetKey(m)))
                        m.Starred = true;
            }
        }
        StarCountChanged();
    }

    /// <summary>
    /// Синхронизировать звёздочки на странице с книгой: вызывается после снятия
    /// отметок в разделе «Цитатник» или полной очистки книги.
    /// </summary>
    public void SyncStarsWithStore()
    {
        var index = BuildStarIndex(_quotes.AllStarred());
        foreach (var file in Files)
        {
            var hasSet = index.TryGetValue(file.FullPath, out var set);
            foreach (var m in file.Mentions)
                m.Starred = hasSet && set!.Contains(OffsetKey(m));
        }
        StarCountChanged();
    }

    /// <summary>
    /// Перейти к конкретному упоминанию (из глобального поиска Ctrl+K).
    /// При необходимости снимает фильтры, которые могли бы спрятать карточку.
    /// </summary>
    public void FocusMention(FileResult file, Mention mention)
    {
        if (!ReferenceEquals(IsolatedFile, file)) IsolatedFile = null;
        if (!Mentions.Contains(mention))
        {
            Search = "";
            SelectedMode = "Все";
            EntityFilter = AllEntitiesKey;
            ThemeFilter = AllThemesKey;
            TagFilter = AllTagsKey;
        }
        SelectedFile = file;
        SelectedMention = mention;
    }

    /// <summary>
    /// Изолировать файл по полному пути (переход из «Цитатника»).
    /// false — файла нет в текущей сессии.
    /// </summary>
    public bool IsolateFileByPath(string path)
    {
        var file = Files.FirstOrDefault(f =>
            string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (file is null) return false;
        IsolatedFile = file;
        return true;
    }

    /// <summary>
    /// Группа 3 (item 24): открыть «Паспорт файла» для строки списка.
    /// </summary>
    private void OpenPassport(FileResult? file)
    {
        if (file is null) return;
        _main.OpenFilePassport(file.FullPath);
    }

    private void StarCountChanged()
    {
        OnPropertyChanged(nameof(StarredCount));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ClearAllStars()
    {
        var n = _quotes.StarredCount;
        if (n == 0) return;
        var answer = System.Windows.MessageBox.Show(
            $"Снять все звёздочки? Цитатник будет очищен: {n} цитат.\n" +
            "История отметок сохранится в файле quotes.jsonl.",
            "Очистка цитатника",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        _quotes.ClearAll();
        SyncStarsWithStore();
        _main.StatusText = "Цитатник очищен";
        _main.Dashboard.Log($"⭐ Цитатник очищен ({n} цитат снято)");
    }

    private void ExportStarredQuotes()
    {
        var quotes = _quotes.AllStarred();
        if (quotes.Count == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт цитатника в Markdown",
            Filter = "Markdown|*.md",
            FileName = $"quotes_{DateTime.Now:yyyyMMdd_HHmm}.md"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, QuoteBookMarkdown.Build(quotes, DateTimeOffset.Now), Encoding.UTF8);
            _main.StatusText = $"Цитатник экспортирован: {dlg.FileName}";
            _main.Dashboard.Log($"⭐ Цитатник экспортирован в {dlg.FileName} ({quotes.Count} цитат)");
        }
        catch (Exception ex)
        {
            _main.StatusText = $"Ошибка экспорта цитатника: {ex.Message}";
        }
    }

    /// <summary>Группа 2 (item 30): заметка дневника, привязанная к цитате.</summary>
    private void AddDiaryNote(Mention m, FileResult file)
    {
        var key = MakeStarKey(m, file, GetFileHash(file.FullPath));
        var text = (m.Context ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length > 90) text = text[..89] + "…";
        var label = $"{text} — {m.Entity}, {file.FileName}";
        _main.Diary.StartNewNote(NoteBindingKind.Quote, key, label);
        _main.Navigate(_main.Diary);
        _main.StatusText = "Дневник: новая заметка с привязкой к цитате";
    }

    // ─── R2-13 helpers ───

    private bool CanSaveVerdict(object? p) => p is Mention && SelectedFile is not null;

    private void SaveVerdict(object? p, VerdictKind kind)
    {
        if (p is not Mention { } m || SelectedFile is not { } file) return;
        SaveVerdictCore(m, file, kind, advance: true);
    }

    /// <summary>Сохранить вердикт, запомнить предыдущее состояние и перейти дальше.</summary>
    private void SaveVerdictCore(Mention m, FileResult file, VerdictKind kind, bool advance)
    {
        if (m.Verdict == kind)
        {
            _main.StatusText = $"Уже отмечено: {m.Entity} — {m.VerdictLabel}";
            if (advance) AdvanceToNextUnreviewed(m);
            return;
        }

        var hash = GetFileHash(file.FullPath);
        var offset = VerdictOffset(m);
        PushUndo(new VerdictUndoItem(m, file, hash, m.Verdict));

        try
        {
            _verdicts.Append(new VerdictRecord(
                DateTimeOffset.Now, hash, offset, m.Variant, m.Entity, kind));
            m.Verdict = kind;
            Log.Info("verdicts", "{0}: {1} ({2}) строка {3}", kind, m.Entity, m.Variant, m.Line);
            RefreshCorpusReview();
            _main.StatusText = $"{m.Entity}: {m.VerdictLabel} · {ReviewProgressText}";
            NotifyReviewProgress();
            if (advance) AdvanceToNextUnreviewed(m);
            // В режиме вердикта карточка должна исчезнуть сразу, если новый
            // вердикт больше не проходит фильтр (например, подтверждённая
            // цитата в выборке «Ложные»), а не после ручного переключения.
            if (VerdictFilter.IsVerdictMode(SelectedMode)
                && !VerdictFilter.Matches(m.Verdict, SelectedMode)
                && Mentions.Remove(m))
            {
                if (ReferenceEquals(SelectedMention, m))
                    SelectedMention = Mentions.FirstOrDefault();
                NotifyReviewProgress();
            }
        }
        catch (Exception ex)
        {
            _verdictUndo.RemoveAt(_verdictUndo.Count - 1);
            Log.Error("verdicts", $"Не удалось сохранить вердикт: {ex.Message}");
            _main.StatusText = "Не удалось сохранить вердикт";
        }
    }

    private static long VerdictOffset(Mention m) => m.Offset > 0 ? m.Offset : m.Line;

    /// <summary>Запомнить одно действие пользователя (одиночное или массовое) для Ctrl+Z.</summary>
    private void PushUndo(params VerdictUndoItem[] items)
    {
        if (items.Length == 0) return;
        _verdictUndo.Add(new VerdictUndo(items));
        while (_verdictUndo.Count > MaxVerdictUndo) _verdictUndo.RemoveAt(0);
    }

    /// <summary>Следующая непроверенная цитата в текущей выборке; в конце — по кругу.</summary>
    private void AdvanceToNextUnreviewed(Mention current)
    {
        var index = Mentions.IndexOf(current);
        if (index < 0) return;

        for (var step = 1; step <= Mentions.Count; step++)
        {
            var candidate = Mentions[(index + step) % Mentions.Count];
            if (candidate.HasVerdict) continue;
            SelectedMention = candidate;
            return;
        }
        _main.StatusText = $"Нет непроверенных цитат в выборке · {ReviewProgressText}";
    }

    /// <summary>
    /// Перейти к следующему файлу с непроверенными цитатами. Сначала ищем
    /// в текущей файловой выборке; если там работы нет, сбрасываем фильтры
    /// и продолжаем по всему корпусу. Режим проверки переключается на
    /// «Непроверенные», чтобы карточки были сразу видны.
    /// </summary>
    private void GoToNextUnreviewedFile()
    {
        if (!HasUnreviewedFiles)
        {
            _main.StatusText = _corpusReview.Total == 0
                ? "В корпусе нет цитат для проверки"
                : $"Все цитаты проверены: {ReviewedInCorpus} из {ReviewableInCorpus}";
            return;
        }

        var visible = FilesView.Cast<FileResult>().ToList();
        var next = ReviewNavigator.NextFileWithUnreviewed(visible, SelectedFile);
        var scopeReset = false;
        if (next is null && visible.Count < Files.Count)
        {
            next = ReviewNavigator.NextFileWithUnreviewed(Files, SelectedFile);
            scopeReset = next is not null;
        }
        if (next is null)
        {
            _main.StatusText = "В текущей выборке нет файлов с непроверенными цитатами";
            return;
        }

        if (scopeReset) ResetReviewScopeFilters();
        if (SelectedMode != VerdictFilter.Unreviewed)
            SelectedMode = VerdictFilter.Unreviewed;

        SelectedFile = next;

        // Файл мог пройти по имени, но все его карточки скрыты поиском,
        // темой или сущностью — тогда снимаем и эти фильтры.
        if (Mentions.Count == 0 && next.Mentions.Any(m => !m.HasVerdict))
        {
            ResetReviewScopeFilters();
            if (SelectedMode != VerdictFilter.Unreviewed)
                SelectedMode = VerdictFilter.Unreviewed;
            ReloadSelectedFileMentions();
        }

        SelectedMention = Mentions.FirstOrDefault(m => !m.HasVerdict) ?? Mentions.FirstOrDefault();
        _main.StatusText = scopeReset
            ? $"Фильтры сброшены · {next.FileName}: {ReviewProgressText}"
            : $"{next.FileName}: {ReviewProgressText}";
    }

    private void ResetReviewScopeFilters()
    {
        IsolatedFile = null;
        if (!string.IsNullOrEmpty(Search)) Search = "";
        if (_tagFilter != AllTagsKey) TagFilter = AllTagsKey;
        if (_themeFilter != AllThemesKey) ThemeFilter = AllThemesKey;
        if (_entityFilter != AllEntitiesKey) EntityFilter = AllEntitiesKey;
    }

    private void UndoLastVerdict()
    {
        if (_verdictUndo.Count == 0)
        {
            _main.StatusText = "Нечего отменять: история вердиктов в этом сеансе пуста";
            return;
        }

        var undo = _verdictUndo[^1];
        _verdictUndo.RemoveAt(_verdictUndo.Count - 1);
        var focus = undo.Items[^1];
        try
        {
            var keys = undo.Items
                .Select(i => (FileHash: i.FileHash, Offset: VerdictOffset(i.Mention), Variant: i.Mention.Variant))
                .ToList();
            if (keys.Count == 1)
                _verdicts.Remove(keys[0].FileHash, keys[0].Offset, keys[0].Variant);
            else
                _verdicts.RemoveRange(keys);

            var previousRecords = new List<VerdictRecord>(undo.Items.Count);
            foreach (var item in undo.Items)
            {
                item.Mention.Verdict = item.Previous;
                if (item.Previous is { } previous)
                {
                    previousRecords.Add(new VerdictRecord(
                        DateTimeOffset.Now, item.FileHash, VerdictOffset(item.Mention),
                        item.Mention.Variant, item.Mention.Entity, previous, Note: "undo"));
                }
            }
            if (previousRecords.Count > 0) _verdicts.AppendRange(previousRecords);
            RefreshCorpusReview();

            // Возвращаемся к файлу последней отменённой цитаты и пересобираем
            // видимую выборку: в режиме «Непроверенные» карточка должна вернуться.
            if (!ReferenceEquals(SelectedFile, focus.File))
                SelectedFile = focus.File;
            else
                ReloadSelectedFileMentions();
            // После отмены карточка может не проходить активный фильтр
            // (например, снят вердикт в выборке «Подтверждённые»).
            SelectedMention = Mentions.Contains(focus.Mention)
                ? focus.Mention
                : Mentions.FirstOrDefault();
            if (undo.Items.Count == 1)
            {
                _main.StatusText = focus.Previous is null
                    ? $"Вердикт отменён: {focus.Mention.Entity} снова не проверено"
                    : $"Вердикт восстановлен: {focus.Mention.Entity} — {focus.Mention.VerdictLabel}";
                Log.Info("verdicts", "undo: {0} ({1}) строка {2}",
                    focus.Mention.Entity, focus.Mention.Variant, focus.Mention.Line);
            }
            else
            {
                _main.StatusText = $"Массовый вердикт отменён: {undo.Items.Count} цитат";
                Log.Info("verdicts", "undo bulk: {0} цитат в {1}",
                    undo.Items.Count, focus.File.FileName);
            }
            NotifyReviewProgress();
        }
        catch (Exception ex)
        {
            Log.Error("verdicts", $"Не удалось отменить вердикт: {ex.Message}");
            _main.StatusText = "Не удалось отменить вердикт";
        }
    }

    private void OpenMentionSource(Mention? mention)
    {
        if (mention is null || SelectedFile is not { } file) return;
        SelectedMention = mention;
        FullTextRequested?.Invoke(file.FullPath, file.Mentions);
    }

    private void ApplyBulkVerdict(VerdictKind kind)
    {
        if (SelectedFile is not { } file) return;
        var targets = Mentions.Where(m => m.Verdict != kind).ToList();
        if (targets.Count == 0)
        {
            _main.StatusText = "В видимой выборке уже нет цитат для этого вердикта";
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            $"Отметить «{VerdictName(kind)}» {targets.Count} цитат(ы) в файле «{file.FileName}»?\n" +
            "Действие можно отменить кнопкой «↶ Отменить вердикт» или Ctrl+Z.",
            "Массовый вердикт", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        var hash = GetFileHash(file.FullPath);
        var records = new List<VerdictRecord>(targets.Count);
        var undoItems = new List<VerdictUndoItem>(targets.Count);
        foreach (var m in targets)
        {
            undoItems.Add(new VerdictUndoItem(m, file, hash, m.Verdict));
            records.Add(new VerdictRecord(
                DateTimeOffset.Now, hash, VerdictOffset(m), m.Variant, m.Entity, kind));
        }
        PushUndo(undoItems.ToArray());

        _verdicts.AppendRange(records);
        foreach (var m in targets) m.Verdict = kind;
        RefreshCorpusReview();
        _main.StatusText = $"Массовый вердикт «{VerdictName(kind)}»: {targets.Count} цитат";
        Log.Info("verdicts", "bulk {0}: {1} цитат в {2}", kind, targets.Count, file.FileName);
        if (VerdictFilter.IsVerdictMode(SelectedMode))
        {
            ReloadSelectedFileMentions();
            SelectedMention = Mentions.FirstOrDefault();
        }
        else
        {
            NotifyReviewProgress();
        }
    }

    private static string VerdictName(VerdictKind kind) => kind switch
    {
        VerdictKind.Confirmed => "подтверждено",
        VerdictKind.Rejected => "ложное срабатывание",
        _ => "спорно",
    };

    /// <summary>SHA-256 содержимого файла с кэшем на время жизни страницы.</summary>
    private string GetFileHash(string fullPath)
    {
        if (_hashByPath.TryGetValue(fullPath, out var hash)) return hash;

        // После анализа хэш уже лежит в инкрементальном индексе: не читаем
        // корпус повторно только ради восстановления вердиктов и звёздочек.
        try
        {
            if (_main.TryGetIndexedHash(fullPath, out var indexed) &&
                !string.IsNullOrEmpty(indexed))
            {
                _hashByPath[fullPath] = indexed;
                return indexed;
            }
        }
        catch
        {
            // Индекс недоступен — ниже обычное чтение файла.
        }

        try
        {
            using var fs = File.OpenRead(fullPath);
            hash = Convert.ToHexString(SHA256.HashData(fs));
        }
        catch
        {
            hash = "unreadable:" + fullPath; // файл исчез — ключ всё равно стабильный
        }
        _hashByPath[fullPath] = hash;
        return hash;
    }
}
