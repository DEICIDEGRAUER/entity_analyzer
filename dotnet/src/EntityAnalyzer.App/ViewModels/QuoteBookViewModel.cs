using System.Collections.ObjectModel;
using System.Windows.Input;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.Core.Exporting;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>
/// Группа 2, item 26: «Цитатник» — постоянная книга отмеченных звёздочкой
/// цитат. Источник — quotes.jsonl (QuoteStore): отметки ставятся в
/// «Результатах» и переживают перезапуск. Раздел позволяет фильтровать
/// книгу, снимать отметки, открывать источник и экспортировать в Markdown.
/// </summary>
public sealed class QuoteBookViewModel : ObservableObject
{
    private const string AllEntitiesKey = "Все сущности";

    private readonly MainViewModel _main;
    private readonly QuoteStore _store;
    private List<QuoteRecord> _all = [];

    public ObservableCollection<QuoteRow> Items { get; } = [];

    /// <summary>Число цитат в текущем (отфильтрованном) списке.</summary>
    public int Count => Items.Count;

    /// <summary>Empty state: под фильтрами ничего не осталось.</summary>
    public bool IsDataEmpty => Items.Count == 0;

    /// <summary>Подсказка для пустого состояния (книга пуста или её скрыли фильтры).</summary>
    public string EmptyHint => _all.Count == 0
        ? "Цитатник пуст — отметьте лучшие цитаты звёздочкой (★) на странице «Результаты»."
        : "Под фильтрами ничего не нашлось — измените поиск или сущность.";

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    public ObservableCollection<string> EntityFilters { get; } = [AllEntitiesKey];

    private string _entityFilter = AllEntitiesKey;
    public string EntityFilter
    {
        get => _entityFilter;
        set { if (Set(ref _entityFilter, string.IsNullOrEmpty(value) ? AllEntitiesKey : value)) ApplyFilter(); }
    }

    public RelayCommand ExportCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand OpenSourceCommand { get; }

    public QuoteBookViewModel(MainViewModel main, QuoteStore? store = null)
    {
        _main = main;
        _store = store ?? QuoteStore.Default;
        ExportCommand = new RelayCommand(_ => Export(), _ => Items.Count > 0);
        RemoveCommand = new RelayCommand(p => RemoveQuote(p as QuoteRow), p => p is QuoteRow);
        ClearAllCommand = new RelayCommand(_ => ClearAll(), _ => Items.Count > 0);
        OpenSourceCommand = new RelayCommand(
            p => OpenSource(p as QuoteRow), p => (p as QuoteRow)?.CanOpenSource == true);
    }

    /// <summary>Перечитать книгу из хранилища (вызывается при входе в раздел).</summary>
    public void Refresh()
    {
        _all = [.. _store.AllStarred()];

        var current = _entityFilter;
        EntityFilters.Clear();
        EntityFilters.Add(AllEntitiesKey);
        foreach (var e in _all.Select(q => q.Entity)
                     .Where(e => !string.IsNullOrWhiteSpace(e))
                     .Distinct()
                     .OrderBy(e => e, StringComparer.CurrentCulture))
            EntityFilters.Add(e);
        _entityFilter = EntityFilters.Contains(current) ? current : AllEntitiesKey;
        OnPropertyChanged(nameof(EntityFilter));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = _search.Trim();
        var entity = _entityFilter;
        Items.Clear();
        foreach (var rec in _all)
        {
            if (entity != AllEntitiesKey && !string.Equals(rec.Entity, entity, StringComparison.Ordinal))
                continue;
            if (q.Length > 0 && !Matches(rec, q))
                continue;
            Items.Add(new QuoteRow(rec) { CanOpenSource = CanOpenSource(rec) });
        }
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsDataEmpty));
        OnPropertyChanged(nameof(EmptyHint));
        CommandManager.InvalidateRequerySuggested();
    }

    private static bool Matches(QuoteRecord rec, string q) =>
        Contains(rec.Context, q) || Contains(rec.Entity, q) ||
        Contains(rec.FileName, q) || Contains(rec.Variant, q);

    private static bool Contains(string? hay, string q) =>
        hay?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;

    private bool CanOpenSource(QuoteRecord rec) =>
        _main.Session is { } s && !string.IsNullOrEmpty(rec.FilePath) &&
        s.Files.Any(f => string.Equals(f.FullPath, rec.FilePath, StringComparison.OrdinalIgnoreCase));

    private void OpenSource(QuoteRow? row)
    {
        if (row is null || !_main.Results.IsolateFileByPath(row.Rec.FilePath)) return;
        _main.Navigate(_main.Results);
    }

    private void RemoveQuote(QuoteRow? row)
    {
        if (row is null) return;
        _store.Unstar(row.Rec.Key);
        _main.Results.SyncStarsWithStore();
        Refresh();
        _main.StatusText = $"Цитата снята из цитатника: {row.Entity}";
    }

    private void ClearAll()
    {
        var n = _store.StarredCount;
        if (n == 0) return;
        var answer = System.Windows.MessageBox.Show(
            $"Снять все звёздочки? Цитатник будет очищен: {n} цитат.\n" +
            "История отметок сохранится в файле quotes.jsonl.",
            "Очистка цитатника",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        _store.ClearAll();
        _main.Results.SyncStarsWithStore();
        _main.StatusText = "Цитатник очищен";
        _main.Dashboard.Log($"⭐ Цитатник очищен ({n} цитат снято)");
        Refresh();
    }

    private void Export()
    {
        if (Items.Count == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт цитатника в Markdown",
            Filter = "Markdown|*.md",
            FileName = $"quotes_{DateTime.Now:yyyyMMdd_HHmm}.md",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var md = QuoteBookMarkdown.Build([.. Items.Select(i => i.Rec)], DateTimeOffset.Now);
            System.IO.File.WriteAllText(dlg.FileName, md, System.Text.Encoding.UTF8);
            _main.StatusText = $"Цитатник экспортирован: {dlg.FileName}";
            _main.Dashboard.Log($"⭐ Цитатник экспортирован ({Items.Count} цитат) в {dlg.FileName}");
        }
        catch (Exception ex)
        {
            _main.StatusText = $"Ошибка экспорта цитатника: {ex.Message}";
        }
    }
}

/// <summary>Строка книги: отмеченная цитата + подписи для интерфейса.</summary>
public sealed class QuoteRow
{
    public QuoteRecord Rec { get; }

    public QuoteRow(QuoteRecord rec) => Rec = rec;

    public string Context => (Rec.Context ?? "").Trim();
    public string Entity => string.IsNullOrWhiteSpace(Rec.Entity) ? "—" : Rec.Entity;
    public string Variant => Rec.Variant;
    public string FileName => Rec.FileName;
    public int Line => Rec.Line;
    public string StarredAtText => $"отмечено {Rec.Ts:yyyy-MM-dd HH:mm}";

    /// <summary>Файл цитаты присутствует в текущей сессии (можно открыть в «Результатах»).</summary>
    public bool CanOpenSource { get; init; }
}
