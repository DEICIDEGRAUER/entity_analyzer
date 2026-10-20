using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Semantics;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>Строка списка паттернов: чекбокс включённости + описание.</summary>
public sealed class PatternRowVM : ObservableObject
{
    public SemanticPattern Pattern { get; }

    public string Id => Pattern.Id;
    public string Name => Pattern.Name;
    public string Description => Pattern.Description;
    public string TokensText => string.Join("  ·  ", Pattern.Tokens.Select(Describe));

    /// <summary>True для встроенных паттернов (period-*, ritual-*, title-*, epithet-*).</summary>
    public bool IsEmbedded => Id.StartsWith("period-", StringComparison.OrdinalIgnoreCase)
        || Id.StartsWith("ritual-", StringComparison.OrdinalIgnoreCase)
        || Id.StartsWith("title-", StringComparison.OrdinalIgnoreCase)
        || Id.StartsWith("epithet-", StringComparison.OrdinalIgnoreCase);

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) Changed?.Invoke(); }
    }

    /// <summary>Уведомление владельца об изменении набора (для сохранения).</summary>
    internal event Action? Changed;

    public PatternRowVM(SemanticPattern pattern, Action changed)
    {
        Pattern = pattern;
        _enabled = pattern.Enabled;
        Changed = changed;
    }

    public SemanticPattern WithEnabled(bool enabled) =>
        Pattern with { Enabled = enabled };

    private static string Describe(Core.Semantics.SemanticToken t) => t.Kind switch
    {
        TokenKind.Literal => $"«{t.Value}»",
        TokenKind.Lexicon => $"словарь:{t.Value}",
        TokenKind.Proper => "имя собств.",
        TokenKind.Capitalized => RepeatSuffix(t, "Капит."),
        TokenKind.Regex => $"regex:{t.Value}",
        TokenKind.AnyOf => "любой из (" + string.Join(" | ", (t.Options ?? []).Select(Describe)) + ")",
        TokenKind.Not => "НЕ перед: (" + string.Join(", ", (t.Options ?? []).Select(Describe)) + ")",
        _ => RepeatSuffix(t, "любое слово"),
    } + (t.Required ? "" : " [опционально]");

    private static string RepeatSuffix(SemanticToken t, string baseText) =>
        t is { Min: 1, Max: 1 } ? baseText : $"{baseText}{{{t.Min}..{t.Max}}}";
}

/// <summary>Редактируемая строка токена в конструкторе паттерна.</summary>
public sealed class TokenRowVM : ObservableObject
{
    private string _kind = "anyword";
    private string _value = "";
    private bool _required = true;
    private string _min = "1";
    private string _max = "1";
    private string _optionsJson = "";

    public string Kind { get => _kind; set => Set(ref _kind, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    public bool Required { get => _required; set => Set(ref _required, value); }
    public string Min { get => _min; set => Set(ref _min, value); }
    public string Max { get => _max; set => Set(ref _max, value); }

    /// <summary>JSON вложенных вариантов для any_of / not; пуст для остальных.</summary>
    public string OptionsJson { get => _optionsJson; set => Set(ref _optionsJson, value); }

    public bool NeedsOptions => Kind is "any_of" or "not";

    public SemanticToken ToToken(out string? error)
    {
        error = null;
        var kind = ParseKind(Kind);
        var min = int.TryParse(Min, out var mi) ? Math.Max(0, mi) : 1;
        var max = int.TryParse(Max, out var ma) ? ma : min;
        if (max < min) max = min;

        IReadOnlyList<SemanticToken>? options = null;
        if (NeedsOptions)
        {
            if (string.IsNullOrWhiteSpace(OptionsJson))
            {
                error = $"Токен «{Kind}» требует JSON-варианты в поле Options.";
                return new SemanticToken(kind, Value, Required, [], min, max);
            }
            try
            {
                options = SemanticPatternJson.ParseTokensArray(OptionsJson);
                if (options.Count == 0)
                    error = $"«{Kind}»: массив options пуст или некорректен.";
            }
            catch (Exception ex)
            {
                error = $"«{Kind}»: ошибка JSON вариантов — {ex.Message}";
                options = [];
            }
        }

        return new SemanticToken(kind, Value.Trim(), Required, options, min, max);
    }

    public static TokenRowVM FromToken(SemanticToken t)
    {
        return new TokenRowVM
        {
            Kind = SerializeKind(t.Kind),
            Value = t.Value,
            Required = t.Required,
            Min = t.Min.ToString(),
            Max = t.Max.ToString(),
            OptionsJson = t.Options is null ? "" :
                SemanticPatternJson.SerializeTokensArray(t.Options),
        };
    }

    private static string SerializeKind(TokenKind k) => k switch
    {
        TokenKind.AnyOf => "any_of",
        TokenKind.Literal => "literal",
        TokenKind.Lexicon => "lexicon",
        TokenKind.Proper => "proper",
        TokenKind.Capitalized => "capitalized",
        TokenKind.Regex => "regex",
        TokenKind.Not => "not",
        _ => "anyword",
    };

    private static TokenKind ParseKind(string s) => s?.ToLowerInvariant() switch
    {
        "literal" => TokenKind.Literal,
        "lexicon" => TokenKind.Lexicon,
        "proper" => TokenKind.Proper,
        "capitalized" => TokenKind.Capitalized,
        "regex" => TokenKind.Regex,
        "any_of" => TokenKind.AnyOf,
        "not" => TokenKind.Not,
        _ => TokenKind.AnyWord,
    };
}

/// <summary>
/// Раздел «Поиск и паттерны» на Обзоре: гибкие запросы совместных упоминаний,
/// обособленный скан семантических паттернов и полноценный редактор паттернов
/// (декларативные категории без изменения кода ядра).
/// </summary>
public sealed class AdvancedSearchViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public IReadOnlyList<string> Modes { get; } =
    [
        "Вместе в одном файле",
        "Рядом — не далее N строк",
        "В одном файле, но разнесены ≥ N строк",
        "Первый без второго",
    ];

    public ObservableCollection<string> EntityNames { get; } = [];

    private string? _entityA;
    public string? EntityA { get => _entityA; set => Set(ref _entityA, value); }

    private string? _entityB;
    public string? EntityB { get => _entityB; set => Set(ref _entityB, value); }

    private string? _selectedMode = "Вместе в одном файле";
    public string? SelectedMode { get => _selectedMode; set => Set(ref _selectedMode, value); }

    private int _gapLines = 5;
    public int GapLines { get => _gapLines; set => Set(ref _gapLines, Math.Clamp(value, 1, 500)); }

    public ObservableCollection<CoOccurRow> CoOccurResults { get; } = [];

    private string _coOccurSummary = "";
    public string CoOccurSummary { get => _coOccurSummary; private set => Set(ref _coOccurSummary, value); }

    public bool HasCoOccurResults => CoOccurResults.Count > 0;

    /// <summary>Item 41: кнопка «Найти» всегда активна — причина отказа видна в подсказке.</summary>
    public RelayCommand FindCommand { get; }

    /// <summary>Item 41: показывать кнопку перехода к запуску анализа (нет сессии).</summary>
    private bool _showRunHint;
    public bool ShowRunHint { get => _showRunHint; private set => Set(ref _showRunHint, value); }

    /// <summary>Item 41: переход на «Обзор» к запуску анализа.</summary>
    public RelayCommand GoRunAnalysisCommand { get; }

    // ─── Паттерны ───

    public ObservableCollection<PatternRowVM> Patterns { get; } = [];

    private PatternRowVM? _selectedPatternRow;
    public PatternRowVM? SelectedPatternRow
    {
        get => _selectedPatternRow;
        set { if (Set(ref _selectedPatternRow, value)) LoadDraft(); }
    }

    private string _patternFilter = "";
    public string PatternFilter
    {
        get => _patternFilter;
        set { if (Set(ref _patternFilter, value)) PatternsView.Refresh(); }
    }

    public System.ComponentModel.ICollectionView PatternsView { get; }

    public ObservableCollection<TokenRowVM> DraftTokens { get; } = [];

    private string _editName = "";
    public string EditName { get => _editName; set => Set(ref _editName, value); }

    private string _editId = "";
    public string EditId { get => _editId; set => Set(ref _editId, value); }

    private string _editDescription = "";
    public string EditDescription { get => _editDescription; set => Set(ref _editDescription, value); }

    private bool _editEnabled = true;
    public bool EditEnabled { get => _editEnabled; set => Set(ref _editEnabled, value); }

    private string _editWindow = "4";
    public string EditWindow { get => _editWindow; set => Set(ref _editWindow, value); }

    private string _editTotalWindow = "";
    public string EditTotalWindow { get => _editTotalWindow; set => Set(ref _editTotalWindow, value); }

    private string _jsonPreview = "";
    public string JsonPreview { get => _jsonPreview; set => Set(ref _jsonPreview, value); }

    private string _validationMessage = "";
    public string ValidationMessage { get => _validationMessage; private set => Set(ref _validationMessage, value); }

    public IReadOnlyList<string> TokenKinds { get; } =
        ["literal", "lexicon", "capitalized", "proper", "regex", "anyword", "any_of", "not"];

    public RelayCommand AddPatternCommand { get; }
    public RelayCommand DuplicatePatternCommand { get; }
    public RelayCommand DeletePatternCommand { get; }
    public RelayCommand AddTokenCommand { get; }
    public RelayCommand RemoveTokenCommand { get; }
    public RelayCommand SaveDraftCommand { get; }
    public RelayCommand ApplyJsonCommand { get; }
    public RelayCommand FillExampleCommand { get; }
    public RelayCommand OpenPatternsFileCommand { get; }
    public RelayCommand ReloadPatternsCommand { get; }
    public RelayCommand ResetPatternsCommand { get; }

    // ─── Item 46/48: редактор словарей ───

    /// <summary>Имена словарей (встроенные + пользовательские) после слияния.</summary>
    public ObservableCollection<string> LexiconNames { get; } = [];

    /// <summary>Эффективные слова выбранного словаря.</summary>
    public ObservableCollection<string> LexiconWords { get; } = [];

    private Dictionary<string, List<string>> _userLexiconEdits = new(StringComparer.OrdinalIgnoreCase);
    private LexiconSet _mergedLexicons = new();

    private string? _selectedLexicon;
    public string? SelectedLexicon
    {
        get => _selectedLexicon;
        set { if (Set(ref _selectedLexicon, value)) LoadLexiconWords(); }
    }

    private string _newWord = "";
    public string NewWord { get => _newWord; set => Set(ref _newWord, value); }

    private string _lexiconSummary = "";
    public string LexiconSummary { get => _lexiconSummary; private set => Set(ref _lexiconSummary, value); }

    private string _lexiconValidation = "";
    public string LexiconValidation { get => _lexiconValidation; private set => Set(ref _lexiconValidation, value); }

    public RelayCommand AddLexiconWordCommand { get; }
    public RelayCommand RemoveLexiconWordCommand { get; }
    public RelayCommand OpenLexiconsFileCommand { get; }
    public RelayCommand ResetLexiconCommand { get; }

    public ObservableCollection<SemanticMatch> SemanticResults { get; } = [];

    private string _semanticSummary = "";
    public string SemanticSummary { get => _semanticSummary; private set => Set(ref _semanticSummary, value); }

    public bool HasSemanticResults => SemanticResults.Count > 0;

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set { if (Set(ref _isScanning, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    /// <summary>Обособленный скан паттернов (без анализа сущностей).</summary>
    public AsyncRelayCommand ScanPatternsOnlyCommand { get; }

    /// <summary>Группа 2 (item 30): кнопка «✎» у паттерна — заметка с привязкой к паттерну.</summary>
    public RelayCommand AddPatternNoteCommand { get; }

    public AdvancedSearchViewModel(MainViewModel main)
    {
        _main = main;

        FindCommand = new RelayCommand(_ => Find()); // item 41: всегда активна, причина — в подсказке
        GoRunAnalysisCommand = new RelayCommand(_ =>
        {
            _main.DashboardTabIndex = 0;
            _main.Navigate(_main.Dashboard);
        });
        OpenPatternsFileCommand = new RelayCommand(_ => OpenPatternsFile());
        ReloadPatternsCommand = new RelayCommand(_ => ReloadSemanticConfig());
        ResetPatternsCommand = new RelayCommand(_ => ResetToEmbedded());
        AddPatternCommand = new RelayCommand(_ => AddPattern());
        DuplicatePatternCommand = new RelayCommand(_ => DuplicatePattern(), _ => SelectedPatternRow is not null);
        DeletePatternCommand = new RelayCommand(_ => DeletePattern(), _ => SelectedPatternRow is not null);
        AddTokenCommand = new RelayCommand(_ => DraftTokens.Add(new TokenRowVM()));
        RemoveTokenCommand = new RelayCommand(_ =>
        {
            if (SelectedToken is { } t) DraftTokens.Remove(t);
        }, _ => SelectedToken is not null);
        SaveDraftCommand = new RelayCommand(_ => SaveDraft(), _ => SelectedPatternRow is not null || DraftTokens.Count > 0);
        ApplyJsonCommand = new RelayCommand(_ => ApplyJson());
        FillExampleCommand = new RelayCommand(_ => FillPeriodExample());

        // Item 46/48: редактор словарей.
        AddLexiconWordCommand = new RelayCommand(_ => AddLexiconWord(), _ => SelectedLexicon is not null);
        RemoveLexiconWordCommand = new RelayCommand(p =>
        {
            if (p is string word) RemoveLexiconWord(word);
        }, p => p is string && SelectedLexicon is not null);

        // Группа 2 (item 30): заметка дневника с привязкой к паттерну.
        AddPatternNoteCommand = new RelayCommand(p =>
        {
            if (p is not PatternRowVM row) return;
            _main.Diary.StartNewNote(NoteBindingKind.Pattern, row.Id, row.Name);
            _main.Navigate(_main.Diary);
        }, p => p is PatternRowVM);
        OpenLexiconsFileCommand = new RelayCommand(_ => OpenLexiconsFile());
        ResetLexiconCommand = new RelayCommand(_ => ResetLexicon(), _ => SelectedLexicon is not null);

        ScanPatternsOnlyCommand = new AsyncRelayCommand(
            () => ScanPatternsOnlyAsync(), () => !IsScanning && _main.Dashboard.CanStart);

        PatternsView = System.Windows.Data.CollectionViewSource.GetDefaultView(Patterns);
        PatternsView.Filter = FilterPattern;

        LoadPatterns();
        ReloadLexiconEditor();
        LoadEntityNames();
    }

    /// <summary>Выбранный токен в редакторе (для кнопки удаления).</summary>
    public TokenRowVM? SelectedToken { get; set; }

    // ─── Песочница паттернов (R1-8) ───

    private string _sandboxText = "";
    public string SandboxText
    {
        get => _sandboxText;
        set { if (Set(ref _sandboxText, value)) RunSandbox(); }
    }

    public ObservableCollection<SemanticMatch> SandboxResults { get; } = [];

    private string _sandboxSummary = "Вставьте предложение — находки появятся автоматически.";
    public string SandboxSummary { get => _sandboxSummary; private set => Set(ref _sandboxSummary, value); }

    public bool HasSandboxResults => SandboxResults.Count > 0;

    /// <summary>Live-превью: включённые паттерны сканируют вставленный текст.</summary>
    private void RunSandbox()
    {
        SandboxResults.Clear();
        if (string.IsNullOrWhiteSpace(_sandboxText))
        {
            SandboxSummary = "Вставьте предложение — находки появятся автоматически.";
            OnPropertyChanged(nameof(HasSandboxResults));
            return;
        }

        try
        {
            var enabled = Patterns.Where(p => p.Enabled).Select(p => p.WithEnabled(true)).ToList();
            if (enabled.Count == 0)
            {
                SandboxSummary = "Все паттерны выключены — включите хотя бы один в списке выше.";
                OnPropertyChanged(nameof(HasSandboxResults));
                return;
            }

            var store = SemanticPatternStore.Load();
            var names = _main.CodexVm.AllCards
                .SelectMany(c => new[] { c.Name, c.Canonical }.Concat(c.Aliases))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var matcher = new SemanticMatcher(enabled, store.Lexicons, names);

            var normalized = EntityAnalyzer.Core.Text.TextNormalizer.Normalize(_sandboxText);
            var lineStarts = BuildLineStarts(normalized);
            var matches = matcher.Scan(normalized, "песочница", lineStarts);

            foreach (var m in matches.Take(50)) SandboxResults.Add(m);
            SandboxSummary = matches.Count == 0
                ? $"Конструкций не найдено ({enabled.Count} вкл. паттернов)."
                : $"Находок: {matches.Count} (показаны первые {Math.Min(50, matches.Count)}).";
        }
        catch (Exception ex)
        {
            SandboxSummary = "Ошибка песочницы: " + ex.Message;
        }
        OnPropertyChanged(nameof(HasSandboxResults));
    }

    private static int[] BuildLineStarts(string normalized)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < normalized.Length; i++)
            if (normalized[i] == '\n') starts.Add(i + 1);
        return [.. starts];
    }

    /// <summary>Вызывается при входе на вкладку: обновить сущности и результаты.</summary>
    public void Refresh()
    {
        LoadEntityNames();
        LoadSemanticResults();
        CommandManager.InvalidateRequerySuggested();
    }

    private void LoadEntityNames()
    {
        var currentA = EntityA;
        var currentB = EntityB;
        EntityNames.Clear();
        foreach (var name in _main.CodexVm.AllCards.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            EntityNames.Add(name);

        EntityA = currentA is not null && EntityNames.Contains(currentA) ? currentA : EntityNames.FirstOrDefault();
        EntityB = currentB is not null && EntityNames.Contains(currentB) ? currentB : EntityNames.Skip(1).FirstOrDefault();
    }

    private void Find()
    {
        CoOccurResults.Clear();
        OnPropertyChanged(nameof(HasCoOccurResults));
        ShowRunHint = false;

        // Item 41: вместо заблокированной кнопки — понятная причина отказа.
        if (EntityNames.Count == 0)
        {
            CoOccurSummary = "Кодекс пуст: сначала добавьте карточки сущностей на странице «Кодекс».";
            return;
        }

        var needsBoth = SelectedMode != "Первый без второго";
        if (EntityA is null || (needsBoth && EntityB is null))
        {
            CoOccurSummary = needsBoth ? "Выберите оба объекта." : "Выберите первый объект.";
            return;
        }
        if (needsBoth && string.Equals(EntityA, EntityB, StringComparison.Ordinal))
        {
            CoOccurSummary = "Объекты должны различаться — выберите другую пару.";
            return;
        }

        if (_main.Session is not { } session)
        {
            CoOccurSummary = "Нет данных анализа: сначала выполните прогон корпуса на странице «Обзор».";
            ShowRunHint = true;
            return;
        }

        var mode = SelectedMode switch
        {
            "Рядом — не далее N строк" => CoOccurMode.NearLines,
            "В одном файле, но разнесены ≥ N строк" => CoOccurMode.FarLines,
            "Первый без второго" => CoOccurMode.FirstWithoutSecond,
            _ => CoOccurMode.SameFile,
        };

        var rows = CoOccurrenceQuery.Run(session, EntityA, EntityB, mode, GapLines);

        foreach (var r in rows) CoOccurResults.Add(r);
        CoOccurSummary = rows.Count == 0
            ? "Совместных упоминаний не найдено — измените пару или условие."
            : $"Найдено файлов: {rows.Count}";
        OnPropertyChanged(nameof(HasCoOccurResults));
    }

    // ─── Item 46/48: редактор словарей ───

    /// <summary>Перечитать паттерны и словари (кнопка «Перечитать» и первичная загрузка).</summary>
    private void ReloadSemanticConfig()
    {
        LoadPatterns();
        ReloadLexiconEditor();
    }

    /// <summary>
    /// Item 48: перечитать эффективные словари (встроенные + пользовательские
    /// правки) и показать их в редакторе.
    /// </summary>
    private void ReloadLexiconEditor()
    {
        _userLexiconEdits = new Dictionary<string, List<string>>(
            SemanticPatternStore.LoadUserLexiconEdits(), StringComparer.OrdinalIgnoreCase);
        var store = SemanticPatternStore.Load();
        _mergedLexicons = store.Lexicons;

        var current = SelectedLexicon;
        LexiconNames.Clear();
        foreach (var n in store.Lexicons.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            LexiconNames.Add(n);
        SelectedLexicon = current is not null && LexiconNames.Contains(current) ? current : LexiconNames.FirstOrDefault();
        LoadLexiconWords();
        CommandManager.InvalidateRequerySuggested();
    }

    private void LoadLexiconWords()
    {
        LexiconWords.Clear();
        if (SelectedLexicon is not { } name) { LexiconSummary = ""; return; }
        foreach (var w in _mergedLexicons.WordsOf(name).OrderBy(w => w, StringComparer.OrdinalIgnoreCase))
            LexiconWords.Add(w);
        var edits = _userLexiconEdits.TryGetValue(name, out var list) ? list.Count : 0;
        LexiconSummary = $"Слов: {LexiconWords.Count} · пользовательских правок: {edits}";
    }

    private List<string> GetLexiconEdits(string name)
    {
        if (!_userLexiconEdits.TryGetValue(name, out var list))
            _userLexiconEdits[name] = list = [];
        return list;
    }

    private void AddLexiconWord()
    {
        if (SelectedLexicon is not { } name) return;
        var word = NewWord.Trim().ToLowerInvariant();
        if (word.Length == 0)
        {
            LexiconValidation = "Введите слово для добавления.";
            return;
        }

        var edits = GetLexiconEdits(name);
        // Повторное добавление снимает пометку «!слово» (слово снова видно).
        edits.RemoveAll(w => w.Equals(word, StringComparison.OrdinalIgnoreCase)
                          || w.Equals("!" + word, StringComparison.OrdinalIgnoreCase));
        edits.Add(word);
        NewWord = "";
        PersistLexicons($"✓ «{word}» добавлено в словарь «{name}».");
    }

    private void RemoveLexiconWord(string word)
    {
        if (SelectedLexicon is not { } name) return;
        var w = word.Trim().ToLowerInvariant();
        if (w.Length == 0) return;

        var edits = GetLexiconEdits(name);
        edits.RemoveAll(e => e.Equals(w, StringComparison.OrdinalIgnoreCase));
        if (!edits.Any(e => e.Equals("!" + w, StringComparison.OrdinalIgnoreCase)))
            edits.Add("!" + w);
        PersistLexicons($"✓ «{w}» исключено из словаря «{name}». Вернуть слово — добавьте его снова.");
    }

    private void ResetLexicon()
    {
        if (SelectedLexicon is not { } name) return;
        var removed = _userLexiconEdits.Remove(name);
        if (removed)
            PersistLexicons($"✓ Правки словаря «{name}» сброшены — словарь как встроенный.");
        else
            LexiconValidation = $"Для словаря «{name}» пользовательских правок нет.";
    }

    private void PersistLexicons(string successMessage)
    {
        foreach (var key in _userLexiconEdits.Keys.ToList())
            if (_userLexiconEdits[key].Count == 0)
                _userLexiconEdits.Remove(key);
        try
        {
            SemanticPatternStore.SaveUserLexiconEdits(_userLexiconEdits);
        }
        catch (Exception ex)
        {
            LexiconValidation = "Не сохранено: " + ex.Message;
            return;
        }
        ReloadLexiconEditor();
        LexiconValidation = successMessage;
    }

    private void OpenLexiconsFile()
    {
        try
        {
            if (!File.Exists(SemanticPatternStore.UserLexiconsPath))
                SemanticPatternStore.SaveUserLexiconEdits(_userLexiconEdits);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = SemanticPatternStore.UserLexiconsPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Не удалось открыть файл словарей: " + ex.Message,
                "Семантические словари");
        }
    }

    // ─── Обособленный скан паттернов ───

    private async Task ScanPatternsOnlyAsync()
    {
        IsScanning = true;
        try
        {
            var options = _main.Dashboard.BuildOptions();
            var formats = new HashSet<string>(_main.Dashboard.SettingsFormats, StringComparer.OrdinalIgnoreCase);
            var engine = new AnalysisEngine(options, _main.Catalog, _main.Dashboard.Preset);

            var result = await Task.Run(() =>
                engine.ScanPatternsOnly(default, new Progress<AnalysisProgress>(p =>
                    _main.Dashboard.ReportExternalProgress(p))));

            SemanticResults.Clear();
            foreach (var m in result.Matches.Take(500)) SemanticResults.Add(m);
            SemanticSummary = $"Находок: {result.Matches.Count:N0} · файлов просканировано {result.FilesScanned:N0} · " +
                              $"за {result.Duration.TotalSeconds:0.0}s";
            OnPropertyChanged(nameof(HasSemanticResults));

            // Собственные папки паттернов — как у сущностей (by_pattern/<id>/).
            var dir = Path.Combine(options.Output.Dir, "by_pattern");
            ReportExporter.WritePerPatternFolders(result.Matches, dir, formats);
            File.WriteAllText(Path.Combine(options.Output.Dir, "patterns_scan.txt"),
                $"Обособленный скан семантических паттернов\n" +
                $"Файлов: {result.FilesScanned}\nНаходок: {result.Matches.Count}\n" +
                $"Время: {result.Duration.TotalSeconds:0.0}s\nОтчёты: {dir}\n",
                new System.Text.UTF8Encoding(false));

            _main.Dashboard.Log($"◈ Скан паттернов: находок {result.Matches.Count:N0} по {result.FilesScanned:N0} файлам — отчёты в by_pattern/");
            _main.StatusText = $"Скан паттернов завершён: {result.Matches.Count:N0} находок";
        }
        catch (OperationCanceledException)
        {
            _main.Dashboard.Log("⏹ Скан паттернов отменён.");
        }
        catch (Exception ex)
        {
            _main.Dashboard.Log($"✗ Ошибка скана паттернов: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
        }
    }

    // ─── Редактор паттернов ───

    private bool FilterPattern(object item) =>
        string.IsNullOrWhiteSpace(PatternFilter) ||
        item is PatternRowVM r &&
        (r.Name.Contains(PatternFilter, StringComparison.OrdinalIgnoreCase) ||
         r.Id.Contains(PatternFilter, StringComparison.OrdinalIgnoreCase) ||
         r.Description.Contains(PatternFilter, StringComparison.OrdinalIgnoreCase));

    private void LoadPatterns()
    {
        var store = SemanticPatternStore.Load();
        Patterns.Clear();
        foreach (var p in store.Patterns)
            Patterns.Add(new PatternRowVM(p, SavePatternFlags));
        OnPropertyChanged(nameof(Patterns));
        PatternsView.Refresh();
    }

    private void SavePatternFlags()
    {
        try
        {
            var store = new SemanticPatternStore();
            store.SaveUserPatterns(Patterns.Select(r => r.WithEnabled(r.Enabled)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("patterns save: " + ex.Message);
        }
    }

    private void LoadSemanticResults()
    {
        SemanticResults.Clear();
        if (_main.Session is not { } session) { SemanticSummary = ""; return; }

        foreach (var m in session.SemanticMatches.Take(500)) SemanticResults.Add(m);
        SemanticSummary = session.SemanticMatches.Count == 0
            ? "Находок нет. Паттерны сканируются при каждом анализе."
            : $"Находок: {session.SemanticMatches.Count} (показаны первые {SemanticResults.Count})";
        OnPropertyChanged(nameof(HasSemanticResults));
    }

    private void LoadDraft()
    {
        ValidationMessage = "";
        if (SelectedPatternRow is not { } row)
        {
            DraftTokens.Clear();
            EditName = EditId = EditDescription = "";
            EditWindow = "4";
            EditTotalWindow = "";
            EditEnabled = true;
            JsonPreview = "";
            return;
        }

        var p = row.Pattern;
        EditName = p.Name;
        EditId = p.Id;
        EditDescription = p.Description;
        EditEnabled = p.Enabled;
        EditWindow = p.Window.ToString();
        EditTotalWindow = p.TotalWindow?.ToString() ?? "";
        DraftTokens.Clear();
        foreach (var t in p.Tokens) DraftTokens.Add(TokenRowVM.FromToken(t));
        JsonPreview = SemanticPatternJson.SerializeTokensArray(p.Tokens);
    }

    private List<SemanticToken> CollectTokens(out List<string> errors)
    {
        errors = [];
        var tokens = new List<SemanticToken>();
        foreach (var row in DraftTokens)
        {
            var token = row.ToToken(out var err);
            if (err is not null) errors.Add(err);
            tokens.Add(token);
        }
        return tokens;
    }

    private void SaveDraft()
    {
        var tokens = CollectTokens(out var errors);
        if (errors.Count > 0)
        {
            ValidationMessage = "Не сохранено:\n" + string.Join("\n", errors);
            return;
        }
        if (tokens.Count == 0)
        {
            ValidationMessage = "Паттерн должен содержать хотя бы один токен.";
            return;
        }
        if (tokens[^1].Kind == TokenKind.Not)
        {
            ValidationMessage = "Страж «not» не может быть последним токеном.";
            return;
        }

        var id = EditId.Trim();
        if (id.Length == 0)
        {
            id = "user-" + Guid.NewGuid().ToString("N")[..8];
            EditId = id;
        }

        int.TryParse(EditWindow, out var window);
        int? totalWindow = int.TryParse(EditTotalWindow, out var tw) && tw > 0 ? tw : null;

        var pattern = new SemanticPattern(
            id, string.IsNullOrWhiteSpace(EditName) ? id : EditName.Trim(),
            EditDescription.Trim(), tokens,
            Math.Clamp(window is >= 1 ? window : 4, 1, 50),
            EditEnabled, "", totalWindow);

        // Заменить существующий с тем же id либо добавить новый.
        var existing = Patterns.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var idx = Patterns.IndexOf(existing);
            Patterns[idx] = new PatternRowVM(pattern, SavePatternFlags);
            SelectedPatternRow = Patterns[idx];
        }
        else
        {
            var row = new PatternRowVM(pattern, SavePatternFlags);
            Patterns.Add(row);
            SelectedPatternRow = row;
        }

        PersistAll();
        ValidationMessage = "✓ Сохранено в пользовательский конфиг.";
    }

    private void PersistAll() =>
        new SemanticPatternStore().SaveUserPatterns(Patterns.Select(r => r.WithEnabled(r.Enabled)));

    private void AddPattern()
    {
        var id = "user-" + Guid.NewGuid().ToString("N")[..8];
        var pattern = new SemanticPattern(id, "Новый паттерн",
            "Опишите конструкцию: последовательность классов токенов.",
            [new SemanticToken(TokenKind.AnyWord, "")], 4, true, "", null);
        var row = new PatternRowVM(pattern, SavePatternFlags);
        Patterns.Add(row);
        SelectedPatternRow = row;
        PersistAll();
    }

    private void DuplicatePattern()
    {
        if (SelectedPatternRow is not { } src) return;
        var id = src.Id + "-copy" + Guid.NewGuid().ToString("N")[..4];
        var copy = src.Pattern with { Id = id, Name = src.Name + " (копия)" };
        var row = new PatternRowVM(copy, SavePatternFlags);
        Patterns.Add(row);
        SelectedPatternRow = row;
        PersistAll();
    }

    private void DeletePattern()
    {
        if (SelectedPatternRow is not { } row) return;
        var isEmbedded = row.Id.StartsWith("period-") || row.Id.StartsWith("ritual-") || row.Id.StartsWith("title-") || row.Id.StartsWith("epithet-");
        var msg = isEmbedded
            ? $"Удалить паттерн «{row.Name}» ({row.Id})?\nЭто встроенный паттерн — он будет скрыт навсегда. Вернуть можно кнопкой «Сбросить к встроенным»."
            : $"Удалить паттерн «{row.Name}» ({row.Id})?";
        var answer = System.Windows.MessageBox.Show(
            msg, "Удаление паттерна", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        // Если это встроенный паттерн — сохраняем «могильник» (tombstone) с Enabled=false и спец. именем,
        // чтобы SemanticPatternStore.Load() исключил его при слиянии.
        if (isEmbedded)
        {
            var tombstone = row.Pattern with { Enabled = false, Name = "[DELETED] " + row.Name };
            var tombstoneRow = new PatternRowVM(tombstone, SavePatternFlags);
            var idx = Patterns.IndexOf(row);
            Patterns[idx] = tombstoneRow;
            SelectedPatternRow = null;
        }
        else
        {
            Patterns.Remove(row);
            SelectedPatternRow = null;
        }
        PersistAll();
    }

    private void ApplyJson()
    {
        try
        {
            var tokens = SemanticPatternJson.ParseTokensArray(JsonPreview);
            DraftTokens.Clear();
            foreach (var t in tokens) DraftTokens.Add(TokenRowVM.FromToken(t));
            ValidationMessage = tokens.Count == 0
                ? "Массив токенов пуст."
                : $"✓ Загружено токенов: {tokens.Count}. Нажмите «Сохранить в конфиг».";
        }
        catch (Exception ex)
        {
            ValidationMessage = "Ошибка JSON: " + ex.Message;
        }
    }

    /// <summary>Заполнить форму учебным примером «Год Красной Луны».</summary>
    private void FillPeriodExample()
    {
        EditName = "Названия периодов (пример)";
        EditDescription = "Головное слово периода + 1–3 капитализированных слова: «Год Красной Луны», «Час Воющей Собаки».";
        EditWindow = "2";
        EditTotalWindow = "";
        EditEnabled = true;
        DraftTokens.Clear();
        DraftTokens.Add(new TokenRowVM
        {
            Kind = "any_of",
            OptionsJson = """
                [
                  {"kind":"literal","value":"год"},
                  {"kind":"literal","value":"года"},
                  {"kind":"literal","value":"году"},
                  {"kind":"literal","value":"час"},
                  {"kind":"literal","value":"часа"},
                  {"kind":"literal","value":"эра"},
                  {"kind":"literal","value":"эры"},
                  {"kind":"literal","value":"век"}
                ]
                """,
        });
        DraftTokens.Add(new TokenRowVM { Kind = "capitalized", Min = "1", Max = "3" });
        JsonPreview = SemanticPatternJson.SerializeTokensArray(CollectTokens(out _));
        ValidationMessage = "Пример загружен. Сохраните его в конфиг или измените под свою задачу.";
    }

    private void OpenPatternsFile()
    {
        try
        {
            var store = new SemanticPatternStore();
            store.EnsureUserFile();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = SemanticPatternStore.UserPatternsPath,
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show("Не удалось открыть файл конфигурации: " + ex.Message,
                "Семантические паттерны");
        }
    }

    private void ResetToEmbedded()
    {
        try
        {
            if (System.IO.File.Exists(SemanticPatternStore.UserPatternsPath))
                System.IO.File.Delete(SemanticPatternStore.UserPatternsPath);
            LoadPatterns();
        }
        catch (System.Exception ex)
        {
            System.Windows.MessageBox.Show("Не удалось сбросить конфигурацию: " + ex.Message,
                "Семантические паттерны");
        }
    }
}
