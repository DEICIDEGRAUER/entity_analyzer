using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.App.Services;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Text;
using EntityAnalyzer.Core.Variants;
using Microsoft.Win32;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>Root shell: navigation, shared session/catalog state.</summary>
public sealed class MainViewModel : ObservableObject
{
    public DashboardViewModel Dashboard { get; }
    public ResultsViewModel Results { get; }
    public CodexViewModel CodexVm { get; }
    public SchemaViewModel SchemaVm { get; }
    public ReportsViewModel Reports { get; }
    public HistoryViewModel History { get; }
    public ExportViewModel Export { get; }
    public AdvancedSearchViewModel AdvancedSearch { get; }

    /// <summary>Группа 2 (item 26): цитатник — постоянная книга отмеченных цитат.</summary>
    public QuoteBookViewModel QuoteBook { get; }

    /// <summary>Группа 2 (item 22/30): дневник исследователя (заметки в SQLite).</summary>
    public DiaryViewModel Diary { get; }

    /// <summary>Группа 3 (item 24): паспорт файла — исследование одного файла.</summary>
    public FilePassportViewModel Passport { get; }

    /// <summary>Группа 3 (items 23/25/8): словари — эпитеты, глоссарий, каталог.</summary>
    public DictionariesViewModel Dictionaries { get; }

    /// <summary>Группа 3 (items 31/28/29/53): корпус — поиск по фрагменту и аналитика текстов.</summary>
    public CorpusViewModel Corpus { get; }

    /// <summary>Группа 3 (R3-1, item 52): сборщик тематических отрывков на «Обзоре».</summary>
    public ThemeExcerptsViewModel Excerpts { get; }

    /// <summary>Группа 4 (items 5/5.1/6/19): мир — древо, иерархия, хронология, география.</summary>
    public WorldViewModel World { get; }

    /// <summary>Страница «Справка»: обучение и FAQ (контент статический).</summary>
    public HelpViewModel Help { get; } = new();

    /// <summary>The codex document shared by all pages.</summary>
    public Core.Codex.CodexDocument Codex => _codex;
    private readonly Core.Codex.CodexDocument _codex;

    // ─── Масштаб интерфейса ───

    private double _zoom = 1.0;
    public double Zoom
    {
        get => _zoom;
        set
        {
            var v = Math.Clamp(value, 0.8, 1.6);
            if (Set(ref _zoom, v))
            {
                _settings.UiZoom = v;
                SettingsService.Save(_settings);
            }
        }
    }

    public void ZoomStep(int direction)
    {
        if (direction > 0) Zoom += 0.1;
        else if (direction < 0) Zoom -= 0.1;
    }

    /// <summary>R1-7: ключи недавно выполненных команд — новые сверху.</summary>
    public IReadOnlyList<string> RecentPaletteCommands =>
        _settings.RecentPaletteCommands ?? [];

    /// <summary>Запомнить выполненную команду палитры и сохранить настройки.</summary>
    public void RememberPaletteCommand(string key)
    {
        _settings.RecentPaletteCommands = RecentCommandHistory.Touch(
            _settings.RecentPaletteCommands, key);
        SettingsService.Save(_settings);
    }

    /// <summary>Хэш файла из инкрементального индекса текущего прогона.</summary>
    internal bool TryGetIndexedHash(string fullPath, out string hash) =>
        Dashboard.TryGetIndexedHash(fullPath, out hash);

    private void RestoreZoom(AppSettings settings) => _zoom = Math.Clamp(settings.UiZoom, 0.8, 1.6);

    private object _currentPage;
    public object CurrentPage { get => _currentPage; set => Set(ref _currentPage, value); }

    private string _statusText = "Готов к работе";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public MainViewModel()
    {
        _settings = SettingsService.Load();
        var settings = _settings;
        RestoreZoom(settings);
        DismissHintCommand = new RelayCommand(_ => DismissHint()); // R2-19

        // Codex bootstrap: user edits (AppData) win over the migrated legacy
        // catalog, which in turn wins over the embedded default.
        var (doc, source) = UserCodexStore.Load();
        _codex = doc;

        CodexVm = new CodexViewModel(this, settings);
        Dashboard = new DashboardViewModel(this, settings);
        Results = new ResultsViewModel(this);
        SchemaVm = new SchemaViewModel(this);
        Reports = new ReportsViewModel(settings);
        History = new HistoryViewModel();
        Export = new ExportViewModel(this, settings);
        AdvancedSearch = new AdvancedSearchViewModel(this);
        QuoteBook = new QuoteBookViewModel(this);
        Diary = new DiaryViewModel(this);
        Passport = new FilePassportViewModel(this);
        Dictionaries = new DictionariesViewModel(this);
        Corpus = new CorpusViewModel(this);
        Excerpts = new ThemeExcerptsViewModel(this, settings);
        World = new WorldViewModel(this, settings);

        _currentPage = Dashboard;

        // Цветовое кодирование ветвей строится по загруженному кодексу.
        BranchResolver.Initialize(_codex);

        _ = CodexVm.RebuildCatalogAsync().ContinueWith(_ =>
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                BranchResolver.Initialize(_codex);
                // UX: человеческая формулировка вместо технической — новичок
                // должен понять, ЧТО это за справочник и зачем он уже загружен.
                Dashboard.Log($"Справочник сущностей готов ({source}): {_codex.Cards.Count} карточек, " +
                              $"{_codex.Links.Count} связей, " +
                              $"{Catalog.VariantsByEntity.Values.Sum(v => v.Count)} вариантов поиска. " +
                              "Свои сущности добавляются на странице «Кодекс».");
                Dashboard.OnCodexChanged();
            }),
            TaskScheduler.Default);
    }

    /// <summary>R2-17: локальный счётчик событий (stats.jsonl в %APPDATA%).
    /// Никогда не покидает машину; выгрузка — вручную. Тихо игнорирует сбои.</summary>
    internal void Track(string ev, string detail) =>
        Core.Logging.UsageStats.Append(
            System.IO.Path.Combine(SettingsService.DirectoryPath, "stats"), ev, detail);

    // ─── R2-19: контекстные туры ───

    private string? _activeHintKey;

    /// <summary>Текст активной подсказки (null = скрыта).</summary>
    public string? ActiveHint { get; private set; }

    public bool HintVisible => ActiveHint is not null;

    public RelayCommand DismissHintCommand { get; }

    /// <summary>Показать подсказку один раз на ключ (словарь показанных — в settings.json).</summary>
    private void ShowContextHint(object page)
    {
        var (key, text) = page switch
        {
            AdvancedSearchViewModel => ("tour-advanced",
                "Подсказка: ① включите паттерны галочками → ② проверьте их в «Песочнице» на своём предложении → " +
                "③ запустите «Скан паттернов» для поиска по всему корпусу."),
            DashboardViewModel => ("tour-dashboard",
                "Подсказка: плитка «Скрыто лимитом» показывает, сколько цитат не попало в отчёт из-за лимита «цитат/файл» — " +
                "они не потеряны, увеличьте лимит, чтобы увидеть их."),
            DiaryViewModel => ("tour-diary",
                "Подсказка: заметки можно привязывать к файлам, сущностям, паттернам и цитатам — " +
                "кнопки «✎» на других страницах создают заметку сразу с готовой привязкой."),
            FilePassportViewModel => ("tour-passport",
                "Подсказка: паспорт файла открывается и из «Результатов» — кнопкой «Паспорт» " +
                "в строке файла; он собирает сущности, паттерны, темы, главы и связи этого файла."),
            DictionariesViewModel => ("tour-dictionaries",
                "Подсказка: эпитеты, термины глоссария и обнаруженные варианты имён собираются " +
                "автоматически по контекстам — подтверждайте и отклоняйте, всё сохраняется между запусками."),
            CorpusViewModel => ("tour-corpus",
                "Подсказка: поиск по фрагменту идёт по всем файлам в три уровня — точная подстрока, " +
                "падежные формы слов и приблизительное написание (fuzzy/парафраз); Enter запускает поиск. " +
                "«Годы» строят указатель по кнопке, «Интертекст» ищет общие фрагменты между файлами."),
            _ => ("", ""),
        };
        if (key == "" || _settings.ShownHints.Contains(key)) return;
        _activeHintKey = key;
        ActiveHint = text;
        OnPropertyChanged(nameof(ActiveHint));
        OnPropertyChanged(nameof(HintVisible));
    }

    private void DismissHint()
    {
        if (_activeHintKey is null) return;
        if (!_settings.ShownHints.Contains(_activeHintKey))
        {
            _settings.ShownHints.Add(_activeHintKey);
            SettingsService.Save(_settings);
        }
        _activeHintKey = null;
        ActiveHint = null;
        OnPropertyChanged(nameof(ActiveHint));
        OnPropertyChanged(nameof(HintVisible));
    }

    internal void Navigate(object page)
    {
        CurrentPage = page;
        Track("page", page.GetType().Name); // R2-17: офлайн-статистика навигации
        ShowContextHint(page); // R2-19: одноразовые подсказки
        // Keep the nav rail in sync with programmatic transitions too.
        OnPropertyChanged(nameof(NavDashboard));
        OnPropertyChanged(nameof(NavResults));
        OnPropertyChanged(nameof(NavCodex));
        OnPropertyChanged(nameof(NavSchema));
        OnPropertyChanged(nameof(NavExport));
        OnPropertyChanged(nameof(NavQuoteBook));
        OnPropertyChanged(nameof(NavDiary));
        OnPropertyChanged(nameof(NavPassport));
        OnPropertyChanged(nameof(NavDictionaries));
        OnPropertyChanged(nameof(NavCorpus));
        OnPropertyChanged(nameof(NavWorld));
        StatusText = page switch
        {
            DashboardViewModel => "Настройте параметры и запустите анализ",
            ResultsViewModel => Session is null ? "Сначала запустите анализ" : $"Найдено упоминаний: {Session.Summary.TotalMentions}",
            CodexViewModel => "Кодекс мира: карточки, категории и связи",
            SchemaViewModel => "Конструктор категорий и типов связей",
            ReportsViewModel => "Просмотр отчётов по сущностям",
            HistoryViewModel => "История прогонов и сравнение результатов",
            ExportViewModel => Session is null ? "Нет данных для экспорта — сначала выполните анализ" : "Экспорт результатов",
            HelpViewModel => "Обучение работе с приложением и частые вопросы",
            QuoteBookViewModel => "Цитатник: отмеченные цитаты, фильтры и экспорт",
            DiaryViewModel => "Дневник исследователя: заметки, привязанные к объектам",
            FilePassportViewModel => "Паспорт файла: сущности, паттерны, темы, главы и связи",
            DictionariesViewModel => "Словари: эпитеты сущностей и вердикты пользователя",
            CorpusViewModel => "Корпус: поиск по фрагменту, годы и интертекст",
            WorldViewModel => "Мир: древо, иерархия, хронология и география",
            _ => ""
        };
        if (page is ReportsViewModel rv)
            rv.Refresh();
        if (page is DashboardViewModel dv)
        {
            dv.RefreshCacheStats(); // сводка кэша всегда свежая при входе на «Обзор»
            AdvancedSearch.Refresh(); // вкладка «Поиск и паттерны» живёт здесь же
        }
        if (page is HistoryViewModel hv)
            hv.Refresh();
        if (page is ExportViewModel ev)
            ev.RefreshAutoExportInfo();
        if (page is QuoteBookViewModel qbv)
        {
            qbv.Refresh();
            StatusText = qbv.Count > 0
                ? $"Цитатник: {qbv.Count} отмеченных цитат"
                : "Цитатник пуст — отмечайте цитаты звёздочкой в «Результатах»";
        }
        if (page is DiaryViewModel diaryV)
        {
            diaryV.Refresh();
            StatusText = diaryV.TotalCount > 0
                ? $"Дневник: заметок — {diaryV.TotalCount}"
                : "Дневник пуст — создайте первую заметку";
        }
        if (page is FilePassportViewModel fpp)
        {
            fpp.Refresh();
            StatusText = fpp.HasPassport
                ? $"Паспорт файла: {fpp.Title}"
                : "Паспорт файла: выберите файл из списка";
        }
        if (page is DictionariesViewModel dictV)
        {
            dictV.Refresh();
            StatusText = dictV.Epithets.Entities.Count > 0 || dictV.Glossary.Items.Count > 0 || dictV.Catalog.TotalCount > 0
                ? $"Словари: эпитеты — {dictV.Epithets.Entities.Count}, термины — {dictV.Glossary.Items.Count}, варианты — {dictV.Catalog.TotalCount}"
                : "Словари: эпитеты, глоссарий и каталог пока пусты";
        }
        if (page is CorpusViewModel corpusV)
        {
            corpusV.Refresh();
            StatusText = corpusV.Fragment.HasResults
                ? $"Корпус: найдено {corpusV.Fragment.Results.Count} совпадений"
                : "Корпус: поиск по фрагменту, годы и интертекст";
        }
        if (page is WorldViewModel worldV)
        {
            worldV.Refresh();
            StatusText = "Мир: древо, иерархия, хронология и география";
        }
    }

    /// <summary>Открыть паспорт конкретного файла (переход из «Результатов», Группа 3 / item 24).</summary>
    internal void OpenFilePassport(string fullPath)
    {
        Passport.RequestFile(fullPath);
        Navigate(Passport);
    }

    // Two-way nav bindings: radio click → Navigate; Navigate → radio highlight.
    public bool NavDashboard { get => CurrentPage == Dashboard; set { if (value) Navigate(Dashboard); } }
    public bool NavResults { get => CurrentPage == Results; set { if (value) Navigate(Results); } }
    public bool NavCodex { get => CurrentPage == CodexVm; set { if (value) Navigate(CodexVm); } }
    public bool NavSchema { get => CurrentPage == SchemaVm; set { if (value) Navigate(SchemaVm); } }
    public bool NavReports { get => CurrentPage == Reports; set { if (value) Navigate(Reports); } }
    public bool NavHistory { get => CurrentPage == History; set { if (value) Navigate(History); } }
    public bool NavExport { get => CurrentPage == Export; set { if (value) Navigate(Export); } }
    public bool NavHelp { get => CurrentPage == Help; set { if (value) Navigate(Help); } }
    public bool NavQuoteBook { get => CurrentPage == QuoteBook; set { if (value) Navigate(QuoteBook); } }
    public bool NavDiary { get => CurrentPage == Diary; set { if (value) Navigate(Diary); } }
    public bool NavPassport { get => CurrentPage == Passport; set { if (value) Navigate(Passport); } }
    public bool NavDictionaries { get => CurrentPage == Dictionaries; set { if (value) Navigate(Dictionaries); } }
    public bool NavCorpus { get => CurrentPage == Corpus; set { if (value) Navigate(Corpus); } }
    public bool NavWorld { get => CurrentPage == World; set { if (value) Navigate(World); } }

    /// <summary>
    /// QA-UX #3/#40: «Поиск и паттерны» — вкладка на «Обзоре»; отдельного
    /// пункта в рейке нет, команда лишь открывает вкладку.
    /// </summary>
    public RelayCommand NavSearchTab { get; private set; } = null!;

    /// <summary>Индекс выбранной вкладки правой колонки Обзора (0 = ОБЗОР, 1 = ПОИСК И ПАТТЕРНЫ).</summary>
    private int _dashboardTabIndex;
    public int DashboardTabIndex
    {
        get => _dashboardTabIndex;
        set => Set(ref _dashboardTabIndex, value);
    }

    public void OpenSearchTab()
    {
        DashboardTabIndex = 1;
        AdvancedSearch.Refresh();
        ShowContextHint(AdvancedSearch); // одноразовая подсказка ① паттерны → ② песочница → ③ скан
        Navigate(Dashboard);
    }

    /// <summary>Свёрнутая навигационная рейка (сохраняется между сеансами).</summary>
    public bool RailCollapsed
    {
        get => _settings.RailCollapsed;
        set
        {
            if (_settings.RailCollapsed != value)
            {
                _settings.RailCollapsed = value;
                Services.SettingsService.Save(_settings);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Last completed analysis (null until first run).</summary>
    private AnalysisSession? _session;
    public AnalysisSession? Session
    {
        get => _session;
        internal set
        {
            if (Set(ref _session, value))
            {
                // Notify dependent commands (e.g., AdvancedSearchViewModel.FindCommand)
                OnPropertyChanged(nameof(Session));
                CommandManager.InvalidateRequerySuggested();
                AdvancedSearch?.Refresh();
            }
        }
    }

    private readonly AppSettings _settings;

    private EntityCatalog _catalog = new([], generateVariants: false);

    /// <summary>Catalog used by the next run. Change raises readiness refresh.</summary>
    public EntityCatalog Catalog
    {
        get => _catalog;
        internal set
        {
            if (Set(ref _catalog, value))
                Dashboard.RefreshReadiness();
        }
    }
}

public sealed class NavItem
{
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required object Page { get; init; }
}

// ============================ DASHBOARD ============================

public sealed class DashboardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// QA-UX #1: профили с человеческими русскими названиями; в скобках —
    /// технический id (совместим со settings.json и CLI).
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> PresetItems { get; } =
        Presets.All.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(id => new KeyValuePair<string, string>(id, id switch
            {
                "minimal" => "Минимальный (minimal)",
                "light" => "Быстрый (light)",
                "medium" => "Стандартный (medium)",
                "extended" => "Расширенный (extended) — по умолчанию",
                "heavy" => "Глубокий (heavy)",
                "maximum" => "Максимальный (maximum)",
                "titanic" => "Полный без ограничений (titanic)",
                _ => id,
            }))
            .ToList();

    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>Недавние исследования (.eaproj): путь + человеческая подпись.</summary>
    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = [];

    public bool HasRecentProjects => RecentProjects.Count > 0;

    private string _corpusDir = "";
    public string CorpusDir
    {
        get => _corpusDir;
        set
        {
            var normalized = NormalizePath(value);
            if (Set(ref _corpusDir, normalized))
            {
                RefreshReadiness();
                Persist();
            }
        }
    }

    private string _preset = "extended";
    public string Preset
    {
        get => _preset;
        set
        {
            var changed = Set(ref _preset, value);
            // BUG-N09: поля следуют за пресетом только при смене пользователем,
            // а не при восстановлении сохранённых значений на старте.
            if (changed && !_initializing) ApplyPresetToFields();
            Persist();
            OnPropertyChanged(nameof(PresetSummary));
        }
    }

    /// <summary>UX: человекочитаемая сводка выбранного профиля вместо эпитета-загадки.</summary>
    public string PresetSummary
    {
        get
        {
            if (!Presets.All.TryGetValue(_preset, out var p)) return "";
            return $"Контекст ≤{p.ContextWords} слов · цитат на файл: {(p.MentionsPerFile == 0 ? "∞" : p.MentionsPerFile.ToString())} · " +
                   $"поиск опечаток: {(p.Fuzzy ? "вкл" : "выкл")} · порча имён: {(p.DeepCorrupt ? "глубокая" : "лёгкая")}";
        }
    }

    private int _contextWords = 75;
    public int ContextWords { get => _contextWords; set { Set(ref _contextWords, Math.Clamp(value, 5, 400)); Persist(); } }

    private int _mentionsPerFile = 12;
    public int MentionsPerFile { get => _mentionsPerFile; set { Set(ref _mentionsPerFile, Math.Clamp(value, 0, 500)); Persist(); } }

    private bool _fuzzy = true;
    public bool Fuzzy { get => _fuzzy; set { Set(ref _fuzzy, value); Persist(); } }

    private bool _deepCorrupt = true;
    public bool DeepCorrupt { get => _deepCorrupt; set { Set(ref _deepCorrupt, value); Persist(); } }

    // ─── Переключатели фильтров (включаются по необходимости) ───

    public bool BoundaryFilters { get => _settings.BoundaryFiltersEnabled; set { _settings.BoundaryFiltersEnabled = value; Persist(); } }
    public bool ListingDensity { get => _settings.ListingDensityEnabled; set { _settings.ListingDensityEnabled = value; Persist(); } }
    public bool BlacklistPhrases { get => _settings.BlacklistPhrasesEnabled; set { _settings.BlacklistPhrasesEnabled = value; Persist(); } }
    public bool Meaningfulness { get => _settings.MeaningfulnessEnabled; set { _settings.MeaningfulnessEnabled = value; Persist(); } }
    public bool CommonWordRule { get => _settings.CommonWordRuleEnabled; set { _settings.CommonWordRuleEnabled = value; Persist(); } }
    public bool TitleValidation { get => _settings.TitleValidationEnabled; set { _settings.TitleValidationEnabled = value; Persist(); } }
    public bool CrossFileDedup { get => _settings.CrossFileDeduplication; set { _settings.CrossFileDeduplication = value; Persist(); } }

    // ─── Кэш: настройки жизненного цикла и L2 (применяются при следующем прогоне) ───

    public bool CacheEnabled
    {
        get => _settings.CacheEnabled;
        set { _settings.CacheEnabled = value; Persist(); OnPropertyChanged(); }
    }

    public bool MemoryCacheEnabled
    {
        get => _settings.MemoryCacheEnabled;
        set { _settings.MemoryCacheEnabled = value; Persist(); OnPropertyChanged(); }
    }

    /// <summary>Срок хранения записей кэша, дней (0 = вечно).</summary>
    public int CacheMaxAgeDays
    {
        get => _settings.CacheMaxAgeDays;
        set { _settings.CacheMaxAgeDays = Math.Clamp(value, 0, 3650); Persist(); OnPropertyChanged(); }
    }

    /// <summary>Лимит объёма дискового кэша, МБ (0 = без лимита).</summary>
    public long CacheMaxSizeMb
    {
        get => _settings.CacheMaxSizeMb;
        set { _settings.CacheMaxSizeMb = Math.Clamp(value, 0, 10_000_000); Persist(); OnPropertyChanged(); }
    }

    /// <summary>Бюджет L2-кэша (память), МБ (0 = L2 выключен).</summary>
    public int MemoryCacheBudgetMb
    {
        get => _settings.MemoryCacheBudgetMb;
        set { _settings.MemoryCacheBudgetMb = Math.Clamp(value, 0, 32_768); Persist(); OnPropertyChanged(); }
    }

    private string _cacheStatsText = "";
    /// <summary>Сводка кэша (диск + память); обновляется кнопкой, после прогонов и при возврате на «Обзор».</summary>
    public string CacheStatsText { get => _cacheStatsText; private set => Set(ref _cacheStatsText, value); }

    public RelayCommand RefreshCacheStatsCommand { get; private set; } = null!;
    public RelayCommand ClearCacheCommand { get; private set; } = null!;

    // ─── Item 14: оформление — тема и цвета подсветки сущностей ───

    /// <summary>Темы для выпадающего списка («Светлая», «Тёмная»).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ThemeItems => ThemeManager.ThemeItems;

    private string _themeKey = ThemeManager.LightKey;
    public string ThemeKey
    {
        get => _themeKey;
        set
        {
            var normalized = ThemeManager.Normalize(value);
            if (!Set(ref _themeKey, normalized)) return;
            _settings.ThemeKey = normalized;
            Persist();
            ThemeManager.Apply(_settings);
            ThemeMessage = normalized == ThemeManager.DarkKey
                ? "Тёмная тема включена."
                : "Светлая тема включена.";
        }
    }

    public string Branch1Color
    {
        get => _settings.Branch1Color;
        set { _settings.Branch1Color = value; Persist(); ApplyBranchColors(); }
    }

    public string Branch2Color
    {
        get => _settings.Branch2Color;
        set { _settings.Branch2Color = value; Persist(); ApplyBranchColors(); }
    }

    public string Branch3Color
    {
        get => _settings.Branch3Color;
        set { _settings.Branch3Color = value; Persist(); ApplyBranchColors(); }
    }

    public string Branch4Color
    {
        get => _settings.Branch4Color;
        set { _settings.Branch4Color = value; Persist(); ApplyBranchColors(); }
    }

    public string Branch5Color
    {
        get => _settings.Branch5Color;
        set { _settings.Branch5Color = value; Persist(); ApplyBranchColors(); }
    }

    private string _themeMessage = "";
    /// <summary>Подтверждение/ошибка последней правки оформления.</summary>
    public string ThemeMessage { get => _themeMessage; private set => Set(ref _themeMessage, value); }

    /// <summary>Сбросить цвета ветвей к палитре темы.</summary>
    public RelayCommand ResetBranchColorsCommand { get; private set; } = null!;

    private void ApplyBranchColors()
    {
        ThemeManager.Apply(_settings);
        var invalid = new[] { Branch1Color, Branch2Color, Branch3Color, Branch4Color, Branch5Color }
            .Count(c => !string.IsNullOrWhiteSpace(c) && ThemeManager.ParseColor(c) is null);
        ThemeMessage = invalid > 0
            ? $"Некорректных цветов: {invalid} — ожидается «#RRGGBB»; для них действует цвет темы."
            : "Цвета ветвей применены.";
    }

    // ─── Выбор сущностей для селективного анализа (Issue 3) ───

    /// <summary>Доступные сущности из кодекса для выбора.</summary>
    public ObservableCollection<EntitySelectorItem> AvailableEntities { get; } = [];

    /// <summary>ID выбранных сущностей для селективного анализа. Пусто = все.</summary>
    public ObservableCollection<string> SelectedEntityIds { get; } = [];

    /// <summary>Текст поиска для фильтра списка сущностей.</summary>
    private string _entitySearch = "";
    public string EntitySearch
    {
        get => _entitySearch;
        set { if (Set(ref _entitySearch, value)) RefreshEntityList(); }
    }

    /// <summary>Флаг: включить селективный анализ только по выбранным сущностям.</summary>
    private bool _useSelectiveAnalysis;
    public bool UseSelectiveAnalysis
    {
        get => _useSelectiveAnalysis;
        set { if (Set(ref _useSelectiveAnalysis, value)) Persist(); }
    }

    /// <summary>Обновить список доступных сущностей из кодекса.</summary>
    public void RefreshEntityList()
    {
        var query = EntitySearch?.Trim() ?? "";
        var cards = _main.Codex.Cards
            .Where(c => string.IsNullOrWhiteSpace(query) ||
                        c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        c.Canonical.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        c.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AvailableEntities.Clear();
        foreach (var c in cards)
        {
            var item = new EntitySelectorItem(c.Id, c.Name, c.Canonical, c.Aliases.Count, SelectedEntityIds.Contains(c.Id))
            {
                SelectionChanged = (id, isSelected) =>
                {
                    if (isSelected) SelectedEntityIds.Add(id);
                    else SelectedEntityIds.Remove(id);
                }
            };
            AvailableEntities.Add(item);
        }
    }

    /// <summary>Элемент списка выбора сущности.</summary>
    public sealed class EntitySelectorItem : ObservableObject
    {
        public string Id { get; }
        public string Name { get; }
        public string Canonical { get; }
        public int AliasCount { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (Set(ref _isSelected, value))
                    SelectionChanged?.Invoke(Id, value);
            }
        }

        /// <summary>Callback при изменении выбора: (entityId, isSelected).</summary>
        public Action<string, bool>? SelectionChanged { get; set; }

        public EntitySelectorItem(string id, string name, string canonical, int aliasCount, bool isSelected)
        {
            Id = id;
            Name = name;
            Canonical = canonical;
            AliasCount = aliasCount;
            _isSelected = isSelected;
        }
    }

    // ─── Виртуальный корпус: дополнительные папки ───

    public ObservableCollection<string> ExtraDirs { get; } = [];

    // ─── Форматы корпуса: чекбоксы расширений ───

    public ObservableCollection<ExtensionFlagVM> ExtensionFlags { get; } = [];

    private void InitExtensionFlags()
    {
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = "Обычный текст (UTF-8 / CP1251 / UTF-16)",
            [".md"] = "Markdown — текст с разметкой",
            [".html"] = "HTML-страница: теги вычищаются",
            [".htm"] = "HTML-страница: теги вычищаются",
            [".rtf"] = "Rich Text Format: управление группами, \\uN, \\'hh",
            [".fb2"] = "FictionBook XML: тело книги без заметок",
            [".epub"] = "EPUB-книга: spine → xhtml по порядку",
            [".docx"] = "Word 2007+: word/document.xml",
            [".odt"] = "OpenDocument: content.xml",
            [".pdf"] = "PDF best-effort: FlateDecode + Tj/TJ; сканы и CID-шрифты не извлекаются",
            [".doc"] = "Word 97–2003 best-effort: эвристика UTF-16/CP1251 рядов; сложные piece table могут дать шум",
            [".mobi"] = "Kindle PalmDOC: LZ77-распаковка; HUFF/CDIC и DRM не поддерживаются",
            [".prc"] = "PalmDOC/MOBI без DRM",
            [".azw"] = "Kindle (PalmDOC); KF8-сжатие не поддерживается",
            [".azw3"] = "Kindle KF8: обычно HUFF/CDIC — рекомендуем конвертацию в EPUB",
            [".cbz"] = "Комикс-архив: только ComicInfo.xml, картинок нет текста",
            [".cbr"] = "RAR-комикс: не поддерживается, конвертируйте в CBZ",
            [".djvu"] = "DjVu: не поддерживается, требуется OCR",
            [".lit"] = "Microsoft LIT: не поддерживается, конвертируйте в EPUB",
        };
        ExtensionFlags.Clear();
        foreach (var ext in EntityAnalyzer.Core.Configuration.CorpusOptions.AllExtensions)
            ExtensionFlags.Add(new ExtensionFlagVM(ext,
                _settings.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase),
                hints.GetValueOrDefault(ext, ""), SaveExtensions));
    }

    private void SaveExtensions()
    {
        _settings.Extensions = [.. ExtensionFlags.Where(f => f.Included).Select(f => f.Extension)];
        SettingsService.Save(_settings);
        OnPropertyChanged(nameof(EnabledFormatsCount));
        RefreshReadiness();
    }

    /// <summary>QA-UX #5: сколько форматов будет прочитано — видно без раскрытия раздела.</summary>
    public int EnabledFormatsCount => ExtensionFlags.Count(f => f.Included);

    /// <summary>Чекбокс одного расширения корпуса.</summary>
    public sealed class ExtensionFlagVM : ObservableObject
    {
        public string Extension { get; }
        public string Hint { get; }

        private bool _included;
        public bool Included
        {
            get => _included;
            set { if (Set(ref _included, value)) Changed?.Invoke(); }
        }

        private readonly Action _changed;
        internal event Action? Changed;

        public ExtensionFlagVM(string ext, bool included, string hint, Action changed)
        {
            Extension = ext;
            Hint = hint;
            _included = included;
            _changed = changed;
            Changed = changed;
        }
    }

    public RelayCommand AddExtraDir { get; private set; } = null!;
    public RelayCommand RemoveExtraDir { get; private set; } = null!;

    private string? _selectedExtraDir;
    public string? SelectedExtraDir
    {
        get => _selectedExtraDir;
        set => Set(ref _selectedExtraDir, value);
    }

    private void InitExtraDirs(AppSettings settings)
    {
        foreach (var d in settings.ExtraCorpusDirs) ExtraDirs.Add(d);
        AddExtraDir = new RelayCommand(_ =>
        {
            var dlg = new OpenFolderDialog { Title = "Дополнительная папка корпуса" };
            if (dlg.ShowDialog() == true)
            {
                ExtraDirs.Add(dlg.FolderName);
                PersistExtraDirs();
            }
        });
        RemoveExtraDir = new RelayCommand(_ =>
        {
            if (SelectedExtraDir is null) return;
            ExtraDirs.Remove(SelectedExtraDir);
            PersistExtraDirs();
        }, _ => SelectedExtraDir is not null);
    }

    private void PersistExtraDirs()
    {
        _settings.ExtraCorpusDirs = [.. ExtraDirs];
        SettingsService.Save(_settings);
        RefreshReadiness();
    }

    // ─── Кэш: обслуживание (сводка, очистка) ───

    /// <summary>Каталог вывода — та же логика, что в BuildOptions (единый источник).</summary>
    private string ResolveOutputDir() => Path.IsPathRooted(_settings.OutputDir)
        ? _settings.OutputDir
        : Path.Combine(AppContext.BaseDirectory, _settings.OutputDir);

    /// <summary>Каталог кэша: совпадает с движковым — &lt;выход&gt;\.cache.</summary>
    private string ResolveCacheDir() => Path.Combine(ResolveOutputDir(), ".cache");

    private IncrementalIndexStore? _incrementalIndex;
    private string _incrementalIndexDir = "";

    /// <summary>
    /// Хэш файла из инкрементального индекса текущего кэша. Индекс сам
    /// проверяет размер и mtime, поэтому устаревшая запись не будет
    /// использована после изменения файла.
    /// </summary>
    internal bool TryGetIndexedHash(string fullPath, out string hash)
    {
        var cacheDir = ResolveCacheDir();
        if (!string.Equals(_incrementalIndexDir, cacheDir, StringComparison.OrdinalIgnoreCase))
        {
            _incrementalIndex = null;
            _incrementalIndexDir = cacheDir;
            try
            {
                var indexPath = Path.Combine(cacheDir, "incremental.json");
                if (File.Exists(indexPath))
                    _incrementalIndex = new IncrementalIndexStore(indexPath);
            }
            catch
            {
                _incrementalIndex = null;
            }
        }

        if (_incrementalIndex is not null)
            return _incrementalIndex.TryGetHash(fullPath, out hash);

        hash = "";
        return false;
    }

    /// <summary>Обновить сводку кэша (диск + L2). Никогда не бросает.</summary>
    public void RefreshCacheStats()
    {
        try
        {
            var dir = ResolveCacheDir();
            var disk = CacheMaintenance.Inspect(dir);
            var mem = MemoryCacheLayer.Stats;
            var orphans = disk.OrphanFiles > 0 ? $", мусорных файлов {disk.OrphanFiles:N0}" : "";
            CacheStatsText =
                $"На диске: {HumanBytes(disk.FilesBytes)} · записей {disk.Entries:N0}{orphans}\n" +
                $"В памяти (L2): {HumanBytes(mem.Bytes)} · записей {mem.Entries:N0}\n" +
                $"Папка: {dir}";
        }
        catch (Exception ex)
        {
            CacheStatsText = "Статистика кэша недоступна: " + ex.Message;
        }
    }

    private void ClearCache()
    {
        var dir = ResolveCacheDir();
        var disk = CacheMaintenance.Inspect(dir);
        var mem = MemoryCacheLayer.Stats;
        if (disk.FilesCount == 0 && mem.Entries == 0)
        {
            Log("Кэш уже пуст — чистить нечего.");
            RefreshCacheStats();
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            $"Очистить кэш?\n\nНа диске: {HumanBytes(disk.FilesBytes)} ({disk.FilesCount:N0} файлов, записей {disk.Entries:N0})\n" +
            $"В памяти: {HumanBytes(mem.Bytes)} ({mem.Entries:N0} записей)\n\n" +
            "Следующий анализ прочитает корпус заново.",
            "Очистка кэша", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        var freed = CacheMaintenance.Clear(dir);
        MemoryCacheLayer.ClearAll();
        _main.Track("cache", "clear"); // R2-17: локальная статистика — только действие, без данных
        Log($"🧹 Кэш очищен: на диске освобождено {HumanBytes(freed)}, память очищена.");
        RefreshCacheStats();
    }

    private static string StageName(string key) => key switch
    {
        "read" => "чтение",
        "normalize" => "нормализация",
        "match" => "поиск",
        "context" => "контексты",
        "write" => "запись",
        _ => key,
    };

    /// <summary>Человекочитаемый объём (КБ/МБ/ГБ).</summary>
    private static string HumanBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} ГБ",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} МБ",
        >= 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes} Б",
    };

    // ─── Пользовательские regex-исключения ───

    /// <summary>Многострочное поле: одна строка — один паттерн.</summary>
    public string UserExcludeText
    {
        get => string.Join("\n", _settings.UserExcludePatterns);
        set
        {
            _settings.UserExcludePatterns = value
                .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith("//"))
                .ToList();
            SettingsService.Save(_settings);
            OnPropertyChanged();
            ValidateRegexLines();
        }
    }

    // ─── UX: inline-валидация regex — ошибка видна сразу, а не в лог-файле ───

    private string _regexValidationMessage = "";
    public string RegexValidationMessage
    {
        get => _regexValidationMessage;
        private set => Set(ref _regexValidationMessage, value);
    }

    private void ValidateRegexLines()
    {
        var errors = new List<string>();
        var lines = UserExcludeText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            try { _ = new System.Text.RegularExpressions.Regex(line, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
            catch (ArgumentException ex) { errors.Add($"Строка {i + 1}: {ex.Message}"); }
        }
        RegexValidationMessage = string.Join("\n", errors);
    }

    /// <summary>ETA-строка под прогрессбаром.</summary>
    private string _etaText = "";
    public string EtaText
    {
        get => _etaText;
        private set => Set(ref _etaText, value);
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
                RefreshReadiness();
        }
    }

    /// <summary>Variant pool size of the current catalog (drives readiness).</summary>
    public int VariantPoolSize => _main.Catalog.VariantsByEntity.Values.Sum(v => v.Count);

    public bool CanStart { get; private set; }

    /// <summary>Форматы отчётов из настроек — для обособленного скана паттернов.</summary>
    public IReadOnlyCollection<string> SettingsFormats => _settings.Formats;

    /// <summary>
    /// Прогресс внешней операции (обособленный скан паттернов) в общий прогрессбар Обзора.
    /// </summary>
    public void ReportExternalProgress(AnalysisProgress p)
    {
        ProgressMax = p.TotalFiles;
        ProgressValue = p.ProcessedFiles;
        EtaText = $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0} · скан паттернов";
    }

    private IReadOnlyList<string> _blockers = [];
    public IReadOnlyList<string> Blockers
    {
        get => _blockers;
        private set
        {
            if (Set(ref _blockers, value))
            {
                OnPropertyChanged(nameof(StartHint));
                OnPropertyChanged(nameof(IsReady));
            }
        }
    }

    public string StartHint => CanStart ? "" : string.Join("   ·   ", Blockers);
    public bool IsReady => CanStart;

    /// <summary>Recomputes CanStart/Blockers and notifies bindings + commands.</summary>
    public void RefreshReadiness()
    {
        var result = ReadinessEvaluator.Evaluate(
            CorpusDir,
            corpusExists: Directory.Exists(CorpusDir),
            variantCount: VariantPoolSize,
            isBusy: IsRunning);

        CanStart = result.CanStart;
        Blockers = result.Blockers;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(VariantPoolSize));
        OnPropertyChanged(nameof(CorpusMissing));
        CommandManager.InvalidateRequerySuggested();
    }

    public bool IsIdle => !IsRunning;

    /// <summary>Empty State: корпус не выбран или недоступен — правая колонка показывает заглушку.</summary>
    public bool CorpusMissing => string.IsNullOrEmpty(CorpusDir) || !Directory.Exists(CorpusDir);

    private int _progressValue;
    public int ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    private int _progressMax = 100;
    public int ProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }

    // Stat tiles
    private string _statFiles = "—";
    public string StatFiles { get => _statFiles; private set => Set(ref _statFiles, value); }
    private string _statMentions = "—";
    public string StatMentions { get => _statMentions; private set => Set(ref _statMentions, value); }
    private string _statEntities = "—";
    public string StatEntities { get => _statEntities; private set => Set(ref _statEntities, value); }
    private string _statDuration = "—";
    public string StatDuration { get => _statDuration; private set => Set(ref _statDuration, value); }
    private string _statDuplicates = "—";
    public string StatDuplicates { get => _statDuplicates; private set => Set(ref _statDuplicates, value); }

    /// <summary>Совпадения, скрытые лимитом цитат на файл (UX: прозрачность выдачи).</summary>
    private string _statDisplayCapped = "—";
    public string StatDisplayCapped { get => _statDisplayCapped; private set => Set(ref _statDisplayCapped, value); }

    /// <summary>Сводка по кодексу над кнопкой запуска: ЧТО именно будет искаться.</summary>
    public string CodexSummary =>
        $"Поиск по кодексу: {_main.Codex.Cards.Count} карточек · {VariantPoolSize:N0} вариантов. " +
        "Свои сущности — на странице «Кодекс».";

    /// <summary>Вызывается после пересборки каталога — обновляет сводку кодекса и список сущностей для селективного анализа.</    /// </summary>
    public void OnCodexChanged()
    {
        OnPropertyChanged(nameof(CodexSummary));
        RefreshEntityList();
    }

    /// <summary>True, если child лежит внутри root (или совпадает с ним).</summary>
    internal static bool IsSubPath(string child, string root)
    {
        try
        {
            var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            return c.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public RelayCommand BrowseCorpus { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>R2-18: демо-корпус «за 30 секунд» — полный цикл без выбора папок.</summary>
    public RelayCommand RunDemoCommand { get; }

    /// <summary>R2-11: сохранить исследование (корпус+кодекс+паттерны+настройки) в .eaproj.</summary>
    public RelayCommand SaveProjectCommand { get; }
    /// <summary>R2-11: открыть .eaproj и восстановить контекст исследования.</summary>
    public RelayCommand OpenProjectCommand { get; }

    /// <summary>Открыть один из недавних проектов (параметр — путь .eaproj).</summary>
    public RelayCommand OpenRecentProjectCommand { get; }

    /// <summary>Убрать запись из недавних (параметр — путь).</summary>
    public RelayCommand RemoveRecentProjectCommand { get; }

    /// <summary>R2-6: следить за корпусом и перезапускать анализ при изменениях.</summary>
    public RelayCommand WatchCommand { get; }

    private bool _isWatching;
    public bool IsWatching { get => _isWatching; private set { if (Set(ref _isWatching, value)) OnPropertyChanged(nameof(WatchButtonText)); } }

    /// <summary>Текст кнопки-переключателя слежения.</summary>
    public string WatchButtonText => _isWatching ? "⏹ Остановить слежение" : "👁 Следить за корпусом";

    private List<EntityAnalyzer.Core.Analysis.CorpusWatcher>? _watchers;

    /// <summary>UX-онбординг: ссылка «что такое кодекс» из пустого состояния.</summary>
    public RelayCommand OpenCodexCommand { get; }
    /// <summary>QA-UX #3: переход на вкладку «Поиск и паттерны».</summary>
    public RelayCommand NavSearchTab { get; private set; } = null!;
    /// <summary>QA-UX #12: плитка дубликатов → «Отчёты».</summary>
    public RelayCommand OpenDuplicatesReport { get; private set; } = null!;

    /// <summary>Прокси вкладки Обзора для биндинга TabControl (источник — MainViewModel).</summary>
    public int SearchTabIndex
    {
        get => _main.DashboardTabIndex;
        set
        {
            _main.DashboardTabIndex = value;
            OnPropertyChanged();
        }
    }

    public DashboardViewModel(MainViewModel main, AppSettings settings)
    {
        _main = main;
        _settings = settings;
        // QA-UX #3: внешняя смена вкладки (пункт рейки) обновляет TabControl.
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.DashboardTabIndex))
                OnPropertyChanged(nameof(SearchTabIndex));
        };

        // BUG-N09: заполняем поля НАПРЯМУЮ из settings, минуя сеттеры с Persist().
        // Раньше CorpusDir=… первым делом вызывал Persist(), который записывал
        // _settings.Preset = _preset (ещё дефолтный «extended») в ТОТ ЖЕ объект,
        // из которого дальше читалось settings.Preset, — профиль пользователя
        // молча терялся при каждом старте.
        _corpusDir = NormalizePath(settings.CorpusDir);
        _preset = string.IsNullOrWhiteSpace(settings.Preset) ? "extended" : settings.Preset;
        _contextWords = Math.Clamp(settings.ContextWords, 5, 400);
        _mentionsPerFile = Math.Clamp(settings.MentionsPerFile, 0, 500);
        _fuzzy = settings.Fuzzy;
        _deepCorrupt = settings.DeepCorrupt;
        _themeKey = ThemeManager.Normalize(settings.ThemeKey);

        BrowseCorpus = new RelayCommand(_ =>
        {
            var dlg = new OpenFolderDialog { Title = "Выберите папку корпуса" };
            if (dlg.ShowDialog() == true)
            {
                CorpusDir = dlg.FolderName;
                Log($"Корпус: {CorpusDir}");
            }
        });

        RunCommand = new AsyncRelayCommand(RunAsync, () => CanStart);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);

        // R2-11: исследование как файл — сохранение и восстановление контекста.
        SaveProjectCommand = new RelayCommand(_ =>
        {
            var dlg = new SaveFileDialog
            {
                Title = "Сохранить исследование",
                Filter = "Исследование Entity Analyzer|*.eaproj",
                FileName = "исследование.eaproj",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var codexPath = UserCodexStore.FilePath;
                var patternsPath = Core.Semantics.SemanticPatternStore.UserPatternsPath;
                ProjectPackage.Save(dlg.FileName,
                    new ProjectManifest(DateTimeOffset.Now, Core.About.Version, CorpusDir, Preset),
                    codexJson: File.Exists(codexPath) ? File.ReadAllText(codexPath) : null,
                    semanticPatternsJson: File.Exists(patternsPath) ? File.ReadAllText(patternsPath) : null,
                    settingsJson: SettingsService.Serialize(_settings));
                RememberRecentProject(dlg.FileName);
                Log($"💾 Исследование сохранено: {dlg.FileName} — доступно в «Недавних».");
            }
            catch (Exception ex)
            {
                Log($"✗ Не удалось сохранить исследование: {ex.Message}");
            }
        });

        OpenProjectCommand = new RelayCommand(_ =>
        {
            var dlg = new OpenFileDialog { Title = "Открыть исследование", Filter = "Исследование Entity Analyzer|*.eaproj" };
            if (dlg.ShowDialog() == true) OpenProjectFromPath(dlg.FileName);
        });

        OpenRecentProjectCommand = new RelayCommand(
            p => { if (p is string path) OpenProjectFromPath(path); },
            p => p is string path && File.Exists(path));
        RemoveRecentProjectCommand = new RelayCommand(
            p => { if (p is string path) RemoveRecentProject(path); },
            p => p is string);

        LoadRecentProjects();

        // R2-6: watch-режим — изменения корпуса перезапускают анализ.
        WatchCommand = new RelayCommand(_ => ToggleWatch());

        // R2-18: онбординг — записать встроенный микрокорпус и сразу запустить.
        RunDemoCommand = new RelayCommand(_ =>
        {
            try
            {
                var dir = DemoCorpus.WriteTo(DemoCorpus.DefaultDir);
                CorpusDir = dir; // сеттер обновляет настройки и готовность запуска
                Log($"Демо-корпус готов: {dir} — запускаю анализ.");
                if (RunCommand.CanExecute(null)) RunCommand.Execute(null);
            }
            catch (Exception ex)
            {
                Log($"⚠ Не удалось подготовить демо-корпус: {ex.Message}");
            }
        });

        OpenCodexCommand = new RelayCommand(_ => _main.Navigate(_main.CodexVm));
        NavSearchTab = new RelayCommand(_ => _main.OpenSearchTab());
        // QA-UX #12: плитка «Дубликаты текстов» → страница «Отчёты».
        OpenDuplicatesReport = new RelayCommand(_ =>
        {
            _main.DashboardTabIndex = 0;
            _main.Navigate(_main.Reports);
        });

        // Кэш: статистика и очистка — секция «КЭШ» на «Обзоре».
        RefreshCacheStatsCommand = new RelayCommand(_ => RefreshCacheStats());
        ClearCacheCommand = new RelayCommand(_ => ClearCache(), _ => !IsRunning);

        // Item 14: сброс пользовательских цветов ветвей к палитре темы.
        ResetBranchColorsCommand = new RelayCommand(_ =>
        {
            _settings.Branch1Color = "";
            _settings.Branch2Color = "";
            _settings.Branch3Color = "";
            _settings.Branch4Color = "";
            _settings.Branch5Color = "";
            Persist();
            ThemeManager.Apply(_settings);
            OnPropertyChanged(nameof(Branch1Color));
            OnPropertyChanged(nameof(Branch2Color));
            OnPropertyChanged(nameof(Branch3Color));
            OnPropertyChanged(nameof(Branch4Color));
            OnPropertyChanged(nameof(Branch5Color));
            ThemeMessage = "Цвета ветвей сброшены к палитре темы.";
        });

        // BUG-N09: ApplyPresetToFields() при старте НЕ вызываем — поля уже
        // восстановлены из settings (пользовательские правки поверх пресета
        // должны переживать рестарт). Пресет применяет поля только при смене.
        InitExtraDirs(settings);
        InitExtensionFlags();
        RefreshReadiness();
        ValidateRegexLines();
        RefreshCacheStats();

        _initializing = false;
        OnPropertyChanged(nameof(Preset));
        OnPropertyChanged(nameof(PresetSummary));

        if (CorpusDir.Length == 0)
            Log("⚠ Папка корпуса не выбрана — укажите её в поле «Корпус».");
        else if (!Directory.Exists(CorpusDir))
            Log($"⚠ Сохранённый путь корпуса недоступен: {CorpusDir}");
    }

    /// <summary>Открыть .eaproj по пути (диалог и «Недавние» используют один поток).</summary>
    private void OpenProjectFromPath(string path)
    {
        try
        {
            var pkg = ProjectPackage.Load(path);
            var applied = new List<string>();

            // Кодекс: заменяем документ целиком и сразу persist'им в %APPDATA%
            // (тот же поток, что и «Сбросить к встроенному»).
            if (pkg.CodexJson is not null)
            {
                var doc = Core.Codex.CodexDocument.FromJson(pkg.CodexJson);
                UserCodexStore.Save(doc);
                _main.CodexVm.ReloadFrom(doc);
                _main.CodexVm.RebuildIndex.Execute(null);
                _main.Dashboard.OnCodexChanged();
                applied.Add($"кодекс ({doc.Cards.Count} карточек)");
            }

            // Паттерны: пишем пользовательский файл; движок подхватит при следующем прогоне.
            if (pkg.SemanticPatternsJson is not null)
            {
                var patternsPath = Core.Semantics.SemanticPatternStore.UserPatternsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(patternsPath)!);
                File.WriteAllText(patternsPath, pkg.SemanticPatternsJson);
                applied.Add("семантические паттерны");
            }

            // Настройки: применяем корпус/профиль к текущей сессии, полный
            // набор ляжет в settings.json и вступит при следующем запуске.
            if (pkg.SettingsJson is not null)
                SettingsService.WriteRaw(pkg.SettingsJson);

            if (!string.IsNullOrWhiteSpace(pkg.Manifest.CorpusDir))
            {
                CorpusDir = pkg.Manifest.CorpusDir;
                applied.Add("корпус");
            }
            if (!string.IsNullOrWhiteSpace(pkg.Manifest.Preset))
            {
                Preset = pkg.Manifest.Preset;
                applied.Add("профиль");
            }

            RememberRecentProject(path);
            Log($"📂 Исследование открыто: {path} — применено: {string.Join(", ", applied)}. " +
                $"Движок пакета {pkg.Manifest.EngineVersion}; настройки полностью вступят при следующем запуске.");
        }
        catch (Exception ex)
        {
            Log($"✗ Не удалось открыть исследование: {ex.Message}");
        }
    }

    private void LoadRecentProjects()
    {
        RecentProjects.Clear();
        var raw = _settings.RecentProjects ?? [];
        var stored = raw
            .Select(TryFullPath)
            .OfType<string>()
            .ToList();
        var valid = stored
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        foreach (var path in valid)
            RecentProjects.Add(RecentProjectItem.FromPath(path));

        // Чистим настройки от удалённых/битых путей: иначе они копятся годами
        // и однажды ломают Path.GetFullPath при добавлении нового проекта.
        if (valid.Count != raw.Count)
        {
            _settings.RecentProjects = valid;
            SettingsService.Save(_settings);
        }
        OnPropertyChanged(nameof(HasRecentProjects));
    }

    private void RememberRecentProject(string path)
    {
        var full = TryFullPath(path);
        if (full is null) return;
        var list = (_settings.RecentProjects ?? [])
            .Select(TryFullPath)
            .OfType<string>()
            .Where(p => !string.Equals(p, full, StringComparison.OrdinalIgnoreCase))
            .Prepend(full)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        _settings.RecentProjects = list;
        SettingsService.Save(_settings);
        LoadRecentProjects();
    }

    private void RemoveRecentProject(string path)
    {
        var full = TryFullPath(path);
        if (full is null) return;
        _settings.RecentProjects = (_settings.RecentProjects ?? [])
            .Where(p => !string.Equals(TryFullPath(p), full, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SettingsService.Save(_settings);
        LoadRecentProjects();
        Log("Недавнее исследование убрано из списка.");
    }

    /// <summary>Безопасно нормализует путь из настроек; битый путь — null.</summary>
    private static string? TryFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    /// <summary>BUG-N09: гасит побочные Persist() во время загрузки настроек.</summary>
    private bool _initializing = true;

    /// <summary>Trims whitespace and strips wrapping quotes from hand-typed paths.</summary>
    private static string NormalizePath(string? raw)
    {
        var s = raw?.Trim() ?? "";
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            s = s[1..^1].Trim();
        return s;
    }

    private void ApplyPresetToFields()
    {
        if (!Presets.All.TryGetValue(Preset, out var p)) return;
        ContextWords = p.ContextWords;
        MentionsPerFile = p.MentionsPerFile;
        Fuzzy = p.Fuzzy;
        DeepCorrupt = p.DeepCorrupt;
    }

    // ─────────────── R2-6: слежение за корпусом ───────────────

    private void ToggleWatch()
    {
        if (_isWatching) StopWatch();
        else StartWatch();
    }

    private void StartWatch()
    {
        var dirs = new List<string>();
        if (Directory.Exists(CorpusDir)) dirs.Add(CorpusDir);
        dirs.AddRange(ExtraDirs.Where(Directory.Exists));
        if (dirs.Count == 0)
        {
            Log("👁 Нечего наблюдать: папка корпуса недоступна.");
            return;
        }

        var extensions = ExtensionFlags.Where(f => f.Included).Select(f => f.Extension).ToList();
        _watchers = dirs
            .Select(d => new EntityAnalyzer.Core.Analysis.CorpusWatcher(
                [d], extensions, quietPeriod: TimeSpan.FromSeconds(3)))
            .ToList();
        foreach (var w in _watchers)
            w.Changed += OnCorpusChanged;

        IsWatching = true;
        Log($"👁 Слежение включено ({dirs.Count} кат.): правки текстов перезапускают анализ автоматически.");
    }

    private void StopWatch()
    {
        if (_watchers != null)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers = null;
        }
        IsWatching = false;
        Log("⏹ Слежение за корпусом остановлено.");
    }

    private void OnCorpusChanged()
    {
        // Пришли из таймерного потока наблюдателя — маршалинг в UI.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Log("👁 Корпус изменился — перезапускаю анализ…");
            // AsyncRelayCommand сам игнорирует вызов, пока идёт прогон:
            // «пропущенный» тик не теряется — следующее изменение повторит.
            RunCommand.Execute(null);
        });
    }

    private async Task RunAsync()
    {
        RefreshReadiness();
        if (!CanStart)
        {
            Log("⚠ Запуск невозможен: " + StartHint);
            return;
        }
        _main.Track("command", "run-analysis"); // R2-17

        var options = BuildOptions();

        // UX: предупреждение о папке отчётов внутри корпуса. Движок уже
        // исключает её из сканирования, но пользователю стоит знать.
        if (IsSubPath(options.Output.Dir, options.Corpus.Dir) ||
            options.Corpus.ExtraDirs.Any(d => IsSubPath(options.Output.Dir, d)))
            Log("⚠ Папка отчётов лежит внутри корпуса — она исключена из сканирования, " +
                "но надёжнее вынести её наружу (страница «Отчёты» → вкладка «Выгрузка»).");

        IsRunning = true;
        EtaText = "";
        LogLines.Clear();
        Log($"Старт: {Directory.EnumerateFiles(CorpusDir, "*.txt", SearchOption.AllDirectories).Count():N0} .txt файлов…");

        _cts = new CancellationTokenSource();
        var progress = new Progress<AnalysisProgress>(p =>
        {
            ProgressMax = p.TotalFiles;
            ProgressValue = p.ProcessedFiles;
            EtaText = p.FilesPerSec > 0
                ? $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0} · {p.FilesPerSec:0} файл/с · осталось ~{TimeSpan.FromSeconds(p.EtaSec):mm\\:ss}"
                : $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0}";
            if (p.ProcessedFiles % 100 == 0 || p.ProcessedFiles == p.TotalFiles)
                Log($"  {EtaText} — {p.CurrentFile}");
        });

        try
        {
            var engine = new AnalysisEngine(options, _main.Catalog, Preset);
            var session = await Task.Run(() =>
                engine.Run(_cts.Token, progress));

            _main.Session = session;
            _main.Results.LoadSession(session);
            _main.Excerpts.OnSessionLoaded();
            _main.Export.RefreshFrom(session);

            // Кодекс: данные для фильтра «только с упоминаниями» и предложения связей.
            try
            {
                _main.CodexVm.SetAnalysisData(session);
                if (_main.CodexVm.Suggestions.Count > 0)
                    Log($"🔗 Предложений связей: {_main.CodexVm.Suggestions.Count} — смотрите страницу «Кодекс»");
            }
            catch (Exception ex)
            {
                Log($"⚠ Кодекс не обновлён: {ex.Message}");
            }

            // Автоотчёты: файлы на диске сразу после анализа — их смотрит страница «Отчёты».
            try
            {
                var formats = new HashSet<string>(_settings.Formats, StringComparer.OrdinalIgnoreCase);
                ReportExporter.ExportAll(session, options.Output.Dir, formats,
                    perEntityFolders: true, catalog: _main.Catalog, options: options,
                    codex: _main.CodexVm.Doc);
                Log($"✓ Отчёты записаны: {options.Output.Dir}");
            }
            catch (Exception ex)
            {
                Log($"✗ Не удалось записать отчёты: {ex.Message}");
            }

            StatFiles = $"{session.Summary.FilesWithMentions}/{session.Files.Count}";
            StatMentions = session.Summary.TotalMentions.ToString("N0");
            StatEntities = session.Summary.UniqueEntities.ToString();
            StatDuration = $"{session.Duration.TotalSeconds:0.0} c";
            StatDisplayCapped = session.Quality.DisplayCapped.ToString("N0");

            // Прозрачность инкрементальности: сколько файлов взято из кэша.
            if (session.Quality.FilesUnchangedSkipped > 0)
                Log($"Кэш: пропущено неизменённых файлов {session.Quality.FilesUnchangedSkipped:N0}, " +
                    $"обработано новых/изменённых {session.Quality.CacheMisses:N0}");
            if (session.Quality.L2Hits > 0)
                Log($"Кэш L2: из памяти {session.Quality.L2Hits:N0}, к диску {session.Quality.L2Misses:N0}");
            RefreshCacheStats();

            // Дубликаты текстов: сводка в плитку и первые группы в журнал.
            var redundantFiles = session.DuplicateGroups.Sum(g => g.RedundantCount);
            StatDuplicates = redundantFiles.ToString("N0");
            if (redundantFiles > 0)
            {
                Log($"⚠ Дубликаты текстов: {redundantFiles} файлов в {session.DuplicateGroups.Count} группах — отчёт duplicates.txt");
                foreach (var g in session.DuplicateGroups.Take(5))
                    Log($"    ≡ {System.IO.Path.GetFileName(g.Files[0])} ≡ " +
                        string.Join(", ", g.Files.Skip(1).Select(System.IO.Path.GetFileName)));
                if (session.DuplicateGroups.Count > 5)
                    Log($"    … и ещё {session.DuplicateGroups.Count - 5} групп");
            }

            // Статистика семантических паттернов: топ-3 в журнал.
            if (session.Quality.SemanticStats.Count > 0)
            {
                var top = string.Join(", ", session.Quality.SemanticStats.Take(3)
                    .Select(s => $"{s.PatternName} ×{s.Matches} ({s.UniqueFiles} файл.)"));
                Log($"◈ Паттерны: {top}" +
                    (session.Quality.SemanticStats.Count > 3
                        ? $" … +{session.Quality.SemanticStats.Count - 3}"
                        : "") +
                    " — настроить на странице «Поиск»");
            }

            // История прогонов: модуль сам пишет журнал и обновляет свою страницу.
            try
            {
                _main.History.Record(session, [CorpusDir, .. ExtraDirs]);
            }
            catch (Exception ex)
            {
                Log($"⚠ История не записана: {ex.Message}");
            }

            Log($"✓ Готово за {session.Duration.TotalSeconds:0.0}s: упоминаний {session.Summary.TotalMentions}, " +
                $"воронка raw={session.Quality.RawMatches} → фильтры −{session.Quality.TotalFilterRejected} → " +
                $"лимит цитат −{session.Quality.DisplayCapped} → итог {session.Quality.FinalMentions}");
            // Прозрачность: пользователь видит, какая стадия стала узким местом.
            var stages = session.Quality.StageMs;
            if (stages.Count > 0)
            {
                var top = string.Join(" · ", stages
                    .Where(kv => kv.Key is "read" or "normalize" or "match" or "context" or "write")
                    .OrderByDescending(kv => kv.Value)
                    .Take(4)
                    .Select(kv => $"{StageName(kv.Key)} {kv.Value / 1000.0:0.0} с"));
                if (top.Length > 0) Log($"⏱ Стадии: {top}");
            }
            // QA-UX #6: анонс записи отчётов на диск — мостик «анализ → файлы».
            try
            {
                var formats = _settings.Formats;
                Log($"⤓ Отчёты записаны в {options.Output.Dir}" +
                    (formats is { Count: > 0 } ? $" ({string.Join("/", formats)})" : ""));
            }
            catch
            {
                Log($"⤓ Отчёты записаны в {options.Output.Dir}");
            }
            _main.StatusText = $"Анализ завершён: {session.Summary.TotalMentions:N0} упоминаний в {session.FilesWithMentionsSafe()} файлах";
            _main.Navigate(_main.Results);
        }
        catch (OperationCanceledException)
        {
            Log("⏹ Анализ отменён.");
            _main.StatusText = "Анализ отменён";
        }
        catch (Exception ex)
        {
            Log($"✗ Ошибка: {ex.Message}");
            _main.StatusText = "Ошибка анализа";
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    internal AnalyzerOptions BuildOptions()
    {
        var options = new AnalyzerOptions();
        options.Corpus.Dir = CorpusDir;
        options.Corpus.ExtraDirs = [.. ExtraDirs];
        options.Processing.UserExcludePatterns = _settings.UserExcludePatterns;
        options.Corpus.Extensions = [.. _settings.Extensions];
        options.Output.Dir = ResolveOutputDir();

        // Кэш: те же значения, что показывает секция «КЭШ» (единый источник — settings).
        // Путь — <выход>\.cache: он детерминирован, исключается из скана корпуса
        // и совпадает с тем, что видит обслуживание (сводка, очистка).
        options.Cache.Dir = ResolveCacheDir();
        options.Cache.Enabled = _settings.CacheEnabled;
        options.Cache.MaxAgeDays = _settings.CacheMaxAgeDays;
        options.Cache.MaxSizeMb = _settings.CacheMaxSizeMb;
        options.Cache.MemoryEnabled = _settings.MemoryCacheEnabled;
        options.Cache.MemoryBudgetMb = _settings.MemoryCacheBudgetMb;
        options.Processing.ContextMaxWords = ContextWords;
        options.Processing.MaxMentionsPerFile = MentionsPerFile;
        options.Processing.FuzzyEnabled = Fuzzy;
        options.Processing.DeepCorrupt = DeepCorrupt;
        options.Processing.BoundaryFiltersEnabled = BoundaryFilters;
        options.Processing.ListingDensityEnabled = ListingDensity;
        options.Processing.BlacklistPhrasesEnabled = BlacklistPhrases;
        options.Processing.MeaningfulnessEnabled = Meaningfulness;
        options.Processing.CommonWordRuleEnabled = CommonWordRule;
        options.Processing.TitleValidationEnabled = TitleValidation;
        options.Processing.CrossFileDeduplication = CrossFileDedup;

        // Issue 3: селективный анализ по выбранным сущностям
        if (UseSelectiveAnalysis && SelectedEntityIds.Count > 0)
            options.Processing.SelectedEntityIds = [.. SelectedEntityIds];

        return options;
    }

    private void Persist()
    {
        if (_initializing) return; // BUG-N09: не пишем дефолты поверх загружаемых настроек
        _settings.CorpusDir = CorpusDir;
        _settings.Preset = Preset;
        _settings.ContextWords = ContextWords;
        _settings.MentionsPerFile = MentionsPerFile;
        _settings.Fuzzy = Fuzzy;
        _settings.DeepCorrupt = DeepCorrupt;
        SettingsService.Save(_settings);
    }
    public void Log(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > 400) LogLines.RemoveAt(0);
    }
}

/// <summary>Строка списка недавних исследований на «Обзоре».</summary>
public sealed record RecentProjectItem(string Path, string FileName, string Folder)
{
    public string ToolTip => Path;

    public static RecentProjectItem FromPath(string path) => new(
        path,
        System.IO.Path.GetFileNameWithoutExtension(path),
        System.IO.Path.GetDirectoryName(path) ?? "");
}

// small helper kept near dashboard to avoid extra file
internal static class AnalysisSessionExtensions
{
    public static int FilesWithMentionsSafe(this AnalysisSession s) => s.Summary.FilesWithMentions;
}
