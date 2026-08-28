using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.App.Services;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Persistence;
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
        OnPropertyChanged(nameof(NavAdvancedSearch));
        StatusText = page switch
        {
            DashboardViewModel => "Настройте параметры и запустите анализ",
            ResultsViewModel => Session is null ? "Сначала запустите анализ" : $"Найдено упоминаний: {Session.Summary.TotalMentions}",
            CodexViewModel => "Кодекс мира: карточки, категории и связи",
            SchemaViewModel => "Конструктор категорий и типов связей",
            ReportsViewModel => "Просмотр отчётов по сущностям",
            HistoryViewModel => "История прогонов и сравнение результатов",
            ExportViewModel => Session is null ? "Нет данных для экспорта — сначала выполните анализ" : "Экспорт результатов",
            AdvancedSearchViewModel => Session is null
                ? "Гибкий поиск доступен после анализа"
                : $"Гибкий поиск: {Session.Summary.TotalMentions:N0} упоминаний в базе запроса",
            HelpViewModel => "Обучение работе с приложением и частые вопросы",
            _ => ""
        };
        if (page is ReportsViewModel rv)
            rv.Refresh();
        if (page is HistoryViewModel hv)
            hv.Refresh();
        if (page is AdvancedSearchViewModel av)
            av.Refresh();
        if (page is ExportViewModel ev)
            ev.RefreshAutoExportInfo();
    }

    // Two-way nav bindings: radio click → Navigate; Navigate → radio highlight.
    public bool NavDashboard { get => CurrentPage == Dashboard; set { if (value) Navigate(Dashboard); } }
    public bool NavResults { get => CurrentPage == Results; set { if (value) Navigate(Results); } }
    public bool NavCodex { get => CurrentPage == CodexVm; set { if (value) Navigate(CodexVm); } }
    public bool NavSchema { get => CurrentPage == SchemaVm; set { if (value) Navigate(SchemaVm); } }
    public bool NavReports { get => CurrentPage == Reports; set { if (value) Navigate(Reports); } }
    public bool NavHistory { get => CurrentPage == History; set { if (value) Navigate(History); } }
    public bool NavExport { get => CurrentPage == Export; set { if (value) Navigate(Export); } }
    public bool NavAdvancedSearch { get => CurrentPage == AdvancedSearch; set { if (value) Navigate(AdvancedSearch); } }
    public bool NavHelp { get => CurrentPage == Help; set { if (value) Navigate(Help); } }

    /// <summary>
    /// QA-UX #3: «Поиск и паттерны» виден в навигации — пункт переводит на
    /// «Обзор» и открывает вкладку поиска/паттернов.
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
    public AnalysisSession? Session { get; internal set; }

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

    /// <summary>Вызывается после пересборки каталога — обновляет сводку кодекса.</summary>
    public void OnCodexChanged() => OnPropertyChanged(nameof(CodexSummary));

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
                Log($"💾 Исследование сохранено: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                Log($"✗ Не удалось сохранить исследование: {ex.Message}");
            }
        });

        OpenProjectCommand = new RelayCommand(_ =>
        {
            var dlg = new OpenFileDialog { Title = "Открыть исследование", Filter = "Исследование Entity Analyzer|*.eaproj" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var pkg = ProjectPackage.Load(dlg.FileName);
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
                    var path = Core.Semantics.SemanticPatternStore.UserPatternsPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, pkg.SemanticPatternsJson);
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

                Log($"📂 Исследование открыто: {dlg.FileName} — применено: {string.Join(", ", applied)}. " +
                    $"Движок пакета {pkg.Manifest.EngineVersion}; настройки полностью вступят при следующем запуске.");
            }
            catch (Exception ex)
            {
                Log($"✗ Не удалось открыть исследование: {ex.Message}");
            }
        });

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

        // BUG-N09: ApplyPresetToFields() при старте НЕ вызываем — поля уже
        // восстановлены из settings (пользовательские правки поверх пресета
        // должны переживать рестарт). Пресет применяет поля только при смене.
        InitExtraDirs(settings);
        InitExtensionFlags();
        RefreshReadiness();
        ValidateRegexLines();

        _initializing = false;
        OnPropertyChanged(nameof(Preset));
        OnPropertyChanged(nameof(PresetSummary));

        if (CorpusDir.Length == 0)
            Log("⚠ Папка корпуса не выбрана — укажите её в поле «Корпус».");
        else if (!Directory.Exists(CorpusDir))
            Log($"⚠ Сохранённый путь корпуса недоступен: {CorpusDir}");
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
                    perEntityFolders: true, catalog: _main.Catalog, options: options);
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
        options.Output.Dir = Path.IsPathRooted(_settings.OutputDir)
            ? _settings.OutputDir
            : Path.Combine(AppContext.BaseDirectory, _settings.OutputDir);
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

// small helper kept near dashboard to avoid extra file
internal static class AnalysisSessionExtensions
{
    public static int FilesWithMentionsSafe(this AnalysisSession s) => s.Summary.FilesWithMentions;
}
