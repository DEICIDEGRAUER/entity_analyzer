using System.Collections.ObjectModel;
using System.Windows.Input;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Exporting;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>
/// Группа 3: «Словари» — языковые инструменты поверх данных прогона.
/// Первая вкладка — эпитеты сущностей (item 23); глоссарий терминов и
/// кандидаты в каталог (items 25/8) подключаются сюда же.
/// </summary>
public sealed class DictionariesViewModel : ObservableObject
{
    public EpithetsViewModel Epithets { get; }
    public GlossaryViewModel Glossary { get; }
    public CatalogViewModel Catalog { get; }

    public DictionariesViewModel(MainViewModel main)
    {
        Epithets = new EpithetsViewModel(main);
        Glossary = new GlossaryViewModel(main);
        Catalog = new CatalogViewModel(main);
    }

    public void Refresh()
    {
        Epithets.Refresh();
        Glossary.Refresh();
        Catalog.Refresh();
    }
}

/// <summary>
/// Item 23: «Эпитеты» — облако описательных слов рядом с каждой сущностью.
/// Данные собираются по уже готовым контекстам упоминаний; пользователь
/// подтверждает или отклоняет привязку, вердикты живут в epithets.jsonl.
/// </summary>
public sealed class EpithetsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly EpithetStore _store;
    private List<EntityEpithets> _all = [];
    private Dictionary<string, string> _verdicts = new(StringComparer.Ordinal);

    public ObservableCollection<EpithetEntityRow> Entities { get; } = [];
    public ObservableCollection<EpithetRow> Epithets { get; } = [];

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    private bool _showRejected;
    public bool ShowRejected
    {
        get => _showRejected;
        set { if (Set(ref _showRejected, value)) ApplyFilter(); }
    }

    private EpithetEntityRow? _selectedEntity;
    public EpithetEntityRow? SelectedEntity
    {
        get => _selectedEntity;
        set { if (Set(ref _selectedEntity, value)) ReloadRows(); }
    }

    public bool HasSession => _main.Session is not null;

    public bool IsDataEmpty => Entities.Count == 0;

    public string EmptyHint => _main.Session is null
        ? "Анализ ещё не запускался — выполните анализ на «Обзоре», затем вернитесь к словарю эпитетов."
        : _all.Count == 0
            ? "Эпитеты не собраны — рядом с упоминаниями не нашлось описательных слов, повторяющихся минимум дважды."
            : "Под фильтром ничего не нашлось — измените поиск.";

    public string CountText => SelectedEntity is null ? "" : $"видимых эпитетов: {Epithets.Count}";

    public RelayCommand ConfirmCommand { get; }
    public RelayCommand RejectCommand { get; }
    public RelayCommand ResetVerdictCommand { get; }
    public RelayCommand ExportCommand { get; }

    public EpithetsViewModel(MainViewModel main, EpithetStore? store = null)
    {
        _main = main;
        _store = store ?? EpithetStore.Default;
        ConfirmCommand = new RelayCommand(
            p => SetVerdict(p as EpithetRow, EpithetStore.Confirmed), p => p is EpithetRow);
        RejectCommand = new RelayCommand(
            p => SetVerdict(p as EpithetRow, EpithetStore.Rejected), p => p is EpithetRow);
        ResetVerdictCommand = new RelayCommand(
            p => SetVerdict(p as EpithetRow, ""), p => p is EpithetRow);
        ExportCommand = new RelayCommand(_ => Export(), _ => Entities.Count > 0);
    }

    /// <summary>Пересобрать облака эпитетов (при входе в раздел).</summary>
    public void Refresh()
    {
        _all = _main.Session is { } session ? EpithetMiner.Mine(session) : [];
        _verdicts = new Dictionary<string, string>(_store.AllActive(), StringComparer.Ordinal);
        ApplyFilter();
        OnPropertyChanged(nameof(HasSession));
    }

    private string VerdictOf(string entity, string word) =>
        _verdicts.TryGetValue(EpithetStore.MakeKey(entity, word), out var v) ? v : "";

    private List<EpithetStat> VisibleStats(EntityEpithets entity) =>
        ShowRejected
            ? [.. entity.Epithets]
            : [.. entity.Epithets.Where(s => VerdictOf(entity.Entity, s.Word) != EpithetStore.Rejected)];

    private void ApplyFilter()
    {
        var q = _search.Trim();
        var current = _selectedEntity?.Entity;
        Entities.Clear();
        foreach (var entity in _all)
        {
            if (q.Length > 0 && !entity.Entity.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            var visible = VisibleStats(entity).Count;
            if (visible == 0) continue;
            Entities.Add(new EpithetEntityRow(entity.Entity, visible));
        }
        _selectedEntity = Entities.FirstOrDefault(x =>
                              string.Equals(x.Entity, current, StringComparison.Ordinal))
                          ?? Entities.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedEntity));
        OnPropertyChanged(nameof(IsDataEmpty));
        OnPropertyChanged(nameof(EmptyHint));
        ReloadRows();
    }

    private void ReloadRows()
    {
        Epithets.Clear();
        if (_selectedEntity is { } sel)
        {
            var entity = _all.FirstOrDefault(e => string.Equals(e.Entity, sel.Entity, StringComparison.Ordinal));
            if (entity is not null)
                foreach (var s in VisibleStats(entity))
                    Epithets.Add(new EpithetRow(s, VerdictOf(entity.Entity, s.Word)));
        }
        OnPropertyChanged(nameof(CountText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetVerdict(EpithetRow? row, string verdict)
    {
        if (row is null || _selectedEntity is not { } sel) return;
        _store.Set(sel.Entity, row.Word, verdict);
        _verdicts[EpithetStore.MakeKey(sel.Entity, row.Word)] = verdict;
        _main.StatusText = verdict switch
        {
            EpithetStore.Confirmed => $"Эпитет подтверждён: {sel.Entity} — {row.Word}",
            EpithetStore.Rejected => $"Эпитет отклонён: {sel.Entity} — {row.Word}",
            _ => $"Вердикт сброшен: {sel.Entity} — {row.Word}",
        };
        ApplyFilter(); // счётчики сущностей зависят от вердиктов
    }

    private void Export()
    {
        if (Entities.Count == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт словаря эпитетов в Markdown",
            Filter = "Markdown|*.md",
            FileName = $"epithets_{DateTime.Now:yyyyMMdd_HHmm}.md",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var visible = _all
                .Select(e => new EntityEpithets(e.Entity, VisibleStats(e)))
                .Where(e => e.Epithets.Count > 0)
                .ToList();
            System.IO.File.WriteAllText(
                dlg.FileName,
                EpithetMarkdown.Build(visible, _store.AllActive()),
                System.Text.Encoding.UTF8);
            _main.StatusText = $"Словарь эпитетов экспортирован: {dlg.FileName}";
            _main.Dashboard.Log($"📖 Словарь эпитетов экспортирован ({visible.Count} сущностей) в {dlg.FileName}");
        }
        catch (Exception ex)
        {
            _main.StatusText = $"Ошибка экспорта словаря эпитетов: {ex.Message}";
        }
    }
}

/// <summary>Строка списка сущностей во вкладке «Эпитеты».</summary>
public sealed class EpithetEntityRow(string entity, int epithetsCount)
{
    public string Entity { get; } = entity;
    public int EpithetsCount { get; } = epithetsCount;
    public string EpithetsCountText => $"эпитетов: {EpithetsCount}";
}

/// <summary>Строка облака эпитетов выбранной сущности.</summary>
public sealed class EpithetRow
{
    public EpithetRow(EpithetStat stat, string verdict)
    {
        Word = stat.Word;
        CountText = $"{stat.Count}×";
        UniqueText = stat.Unique
            ? "только у этой сущности"
            : "также у: " + string.Join(", ", stat.AlsoUsedBy);
        Verdict = verdict;
    }

    public string Word { get; }
    public string CountText { get; }
    public string UniqueText { get; }
    public string Verdict { get; }

    public string VerdictText => Verdict switch
    {
        EpithetStore.Confirmed => "✔ подтверждён",
        EpithetStore.Rejected => "✕ отклонён",
        _ => "",
    };
}

/// <summary>
/// Item 25: «Глоссарий» — кандидаты в термины (заглавные слова внутри
/// предложений, не сущности). Пользователь подтверждает термин и записывает
/// определение; записи живут в glossary.jsonl отдельно от сущностей.
/// </summary>
public sealed class GlossaryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly GlossaryStore _store;
    private List<GlossaryCandidate> _candidates = [];
    private Dictionary<string, GlossaryRecord> _entries = new(StringComparer.Ordinal);

    public ObservableCollection<GlossaryRow> Items { get; } = [];

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    private bool _showRejected;
    public bool ShowRejected
    {
        get => _showRejected;
        set { if (Set(ref _showRejected, value)) ApplyFilter(); }
    }

    private GlossaryRow? _selectedRow;
    public GlossaryRow? SelectedRow
    {
        get => _selectedRow;
        set { if (Set(ref _selectedRow, value)) ReloadDefinition(); }
    }

    private string _definition = "";
    public string Definition
    {
        get => _definition;
        set => Set(ref _definition, value);
    }

    public bool HasSession => _main.Session is not null;
    public bool HasSelection => _selectedRow is not null;
    public bool IsDataEmpty => Items.Count == 0;

    public string EmptyHint => _main.Session is null
        ? "Анализ ещё не запускался — выполните анализ на «Обзоре», затем вернитесь к глоссарию."
        : _candidates.Count == 0
            ? "Кандидаты не найдены — в контекстах нет повторяющихся имён собственных вне известных сущностей."
            : "Под фильтром ничего не нашлось — измените поиск.";

    public RelayCommand SaveCommand { get; }
    public RelayCommand RejectCommand { get; }
    public RelayCommand ResetVerdictCommand { get; }
    public RelayCommand ExportCommand { get; }

    public GlossaryViewModel(MainViewModel main, GlossaryStore? store = null)
    {
        _main = main;
        _store = store ?? GlossaryStore.Default;
        SaveCommand = new RelayCommand(
            _ => SetStatus(GlossaryStore.Confirmed), _ => _selectedRow is not null);
        RejectCommand = new RelayCommand(
            _ => SetStatus(GlossaryStore.Rejected), _ => _selectedRow is not null);
        ResetVerdictCommand = new RelayCommand(
            _ => SetStatus(""), _ => _selectedRow is not null);
        ExportCommand = new RelayCommand(_ => Export(), _ => _candidates.Count > 0);
    }

    /// <summary>Пересобрать кандидатов (при входе в раздел).</summary>
    public void Refresh()
    {
        _candidates = _main.Session is { } session ? GlossaryBuilder.Build(session) : [];
        _entries = new Dictionary<string, GlossaryRecord>(_store.AllActive(), StringComparer.Ordinal);
        ApplyFilter();
        OnPropertyChanged(nameof(HasSession));
    }

    private string StatusOf(string word) =>
        _entries.TryGetValue(word, out var rec) ? rec.Status : "";

    private void ApplyFilter()
    {
        var q = _search.Trim();
        var current = _selectedRow?.Word;
        Items.Clear();
        foreach (var c in _candidates)
        {
            var status = StatusOf(c.Word);
            if (status == GlossaryStore.Rejected && !_showRejected) continue;
            if (q.Length > 0 && !c.Word.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Items.Add(new GlossaryRow(c, status));
        }
        _selectedRow = Items.FirstOrDefault(x => string.Equals(x.Word, current, StringComparison.Ordinal))
                       ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsDataEmpty));
        OnPropertyChanged(nameof(EmptyHint));
        ReloadDefinition();
        CommandManager.InvalidateRequerySuggested();
    }

    private void ReloadDefinition() =>
        Definition = _selectedRow is { } row && _entries.TryGetValue(row.Word, out var rec)
            ? rec.Definition
            : "";

    private void SetStatus(string status)
    {
        if (_selectedRow is not { } row) return;
        _store.Set(row.Word, Definition?.Trim() ?? "", status);
        _entries = new Dictionary<string, GlossaryRecord>(_store.AllActive(), StringComparer.Ordinal);
        _main.StatusText = status switch
        {
            GlossaryStore.Confirmed => $"Термин добавлен в глоссарий: {row.Word}",
            GlossaryStore.Rejected => $"Термин отклонён: {row.Word}",
            _ => $"Вердикт сброшен: {row.Word}",
        };
        ApplyFilter(); // статусы и видимость зависят от вердикта
    }

    private void Export()
    {
        if (_candidates.Count == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт глоссария в Markdown",
            Filter = "Markdown|*.md",
            FileName = $"glossary_{DateTime.Now:yyyyMMdd_HHmm}.md",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            System.IO.File.WriteAllText(
                dlg.FileName,
                GlossaryMarkdown.Build(_candidates, _store.AllActive()),
                System.Text.Encoding.UTF8);
            _main.StatusText = $"Глоссарий экспортирован: {dlg.FileName}";
            _main.Dashboard.Log($"📚 Глоссарий экспортирован в {dlg.FileName}");
        }
        catch (Exception ex)
        {
            _main.StatusText = $"Ошибка экспорта глоссария: {ex.Message}";
        }
    }
}

/// <summary>Строка списка кандидатов в глоссарий.</summary>
public sealed class GlossaryRow(GlossaryCandidate candidate, string status)
{
    public string Word { get; } = candidate.Word;
    public string CountText { get; } = $"{candidate.Count}×";
    public IReadOnlyList<string> Examples { get; } = candidate.Examples;
    public string Status { get; } = status;

    public string StatusText => Status switch
    {
        GlossaryStore.Confirmed => "✔ в глоссарии",
        GlossaryStore.Rejected => "✕ отклонён",
        _ => "",
    };
}

/// <summary>
/// Item 8: «Каталог» — обнаруженные анализом варианты имён (fuzzy-написания,
/// которых нет в каталоге). Добавление синонима обновляет кодекс и пересобирает
/// индекс: следующий прогон найдёт это написание точным проходом; отклонённые
/// кандидаты запоминаются (catalog_ignored.jsonl) и больше не предлагаются.
/// </summary>
public sealed class CatalogViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly CatalogIgnoreStore _ignore;
    private List<DiscoveredVariant> _all = [];

    public ObservableCollection<CatalogRow> Items { get; } = [];

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    private bool _showIgnored;
    public bool ShowIgnored
    {
        get => _showIgnored;
        set { if (Set(ref _showIgnored, value)) ApplyFilter(); }
    }

    public bool HasSession => _main.Session is not null;
    public bool IsDataEmpty => Items.Count == 0;
    public int TotalCount => _all.Count;

    public string EmptyHint => _main.Session is null
        ? "Анализ ещё не запускался — выполните анализ на «Обзоре», затем вернитесь к каталогу."
        : _all.Count == 0
            ? "Обнаруженных вариантов нет — все найденные fuzzy-написания уже известны каталогу."
            : !_showIgnored && _all.All(d => _ignore.IsIgnored(d.Entity, d.Spelling))
                ? "Все кандидаты отклонены — включите «показывать отклонённые», чтобы вернуть их в список."
                : "Под фильтром ничего не нашлось — измените поиск.";

    public RelayCommand AddCommand { get; }
    public RelayCommand RejectCommand { get; }
    public RelayCommand RestoreCommand { get; }

    public CatalogViewModel(MainViewModel main, CatalogIgnoreStore? ignore = null)
    {
        _main = main;
        _ignore = ignore ?? CatalogIgnoreStore.Default;
        AddCommand = new RelayCommand(p => Add(p as CatalogRow), p => p is CatalogRow);
        RejectCommand = new RelayCommand(
            p => Reject(p as CatalogRow), p => p is CatalogRow { Ignored: false });
        RestoreCommand = new RelayCommand(
            p => Restore(p as CatalogRow), p => p is CatalogRow { Ignored: true });
    }

    /// <summary>Пересобрать список обнаруженных вариантов (при входе в раздел).</summary>
    public void Refresh()
    {
        _all = _main.Session is { } session
            ? CatalogDiscoverer.Discover(session, _main.Catalog)
            : [];
        ApplyFilter();
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(TotalCount));
    }

    private void ApplyFilter()
    {
        var q = _search.Trim();
        Items.Clear();
        foreach (var d in _all)
        {
            var ignored = _ignore.IsIgnored(d.Entity, d.Spelling);
            if (ignored && !_showIgnored) continue;
            if (q.Length > 0 &&
                !d.Spelling.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !d.Entity.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            Items.Add(new CatalogRow(d, ignored));
        }
        OnPropertyChanged(nameof(IsDataEmpty));
        OnPropertyChanged(nameof(EmptyHint));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Add(CatalogRow? row)
    {
        if (row is null) return;
        if (!_main.CodexVm.AddDiscoveredAlias(row.Entity, row.Spelling))
        {
            _main.StatusText =
                $"Не удалось добавить «{row.Spelling}»: карточка «{row.Entity}» не найдена или синоним уже есть";
            return;
        }
        _all.RemoveAll(d =>
            string.Equals(d.Spelling, row.Spelling, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Entity, row.Entity, StringComparison.Ordinal));
        if (_ignore.IsIgnored(row.Entity, row.Spelling))
            _ignore.Clear(row.Entity, row.Spelling); // вердикт не нужен: написание уже в каталоге
        ApplyFilter();
        _main.StatusText =
            $"Синоним «{row.Spelling}» добавлен к «{row.Entity}»: индекс пересобран, следующий анализ будет полным";
        _main.Dashboard.Log($"🧩 В каталог добавлен синоним «{row.Spelling}» → «{row.Entity}» (из fuzzy-находок)");
    }

    private void Reject(CatalogRow? row)
    {
        if (row is null || row.Ignored) return;
        _ignore.Set(row.Entity, row.Spelling, CatalogIgnoreStore.Ignored);
        ApplyFilter();
        _main.StatusText =
            $"Кандидат «{row.Spelling}» отклонён: предлагаться не будет (вернуть можно флажком «Показывать отклонённые»)";
        _main.Dashboard.Log($"🚫 Кандидат каталога отклонён: «{row.Spelling}» → «{row.Entity}»");
    }

    private void Restore(CatalogRow? row)
    {
        if (row is null || !row.Ignored) return;
        _ignore.Clear(row.Entity, row.Spelling);
        ApplyFilter();
        _main.StatusText = $"Кандидат «{row.Spelling}» возвращён в список";
    }
}

/// <summary>Строка списка обнаруженных вариантов имён.</summary>
public sealed class CatalogRow(DiscoveredVariant candidate, bool ignored = false)
{
    public string Entity { get; } = candidate.Entity;
    public string Spelling { get; } = candidate.Spelling;
    public string CountText { get; } = $"{candidate.Count}×";
    public string Example { get; } = candidate.Example;
    public bool Ignored { get; } = ignored;
    public string VerdictText { get; } = ignored ? "✕ отклонён" : "";
}
