using System.Collections.ObjectModel;
using System.Windows.Input;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.Core.Exporting;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>
/// Группа 2, item 22/30: «дневник исследователя» — заметки прямо в приложении
/// с привязкой к файлам, сущностям, паттернам и цитатам. Хранение — SQLite
/// (diary.db) с метками времени; экспорт — Markdown. Кнопки «✎» на других
/// страницах создают заметку с уже готовой привязкой (StartNewNote).
/// </summary>
public sealed class DiaryViewModel : ObservableObject
{
    private const string AllKindsKey = "all";

    private readonly MainViewModel _main;
    private readonly NoteStore _store;
    private List<StoredNote> _all = [];

    public ObservableCollection<DiaryRow> Notes { get; } = [];

    /// <summary>Empty state: под фильтрами ничего не осталось.</summary>
    public bool IsDataEmpty => Notes.Count == 0;

    /// <summary>Подсказка для пустого состояния (дневник пуст или его скрыли фильтры).</summary>
    public string EmptyHint => _all.Count == 0
        ? "Заметок пока нет — создайте первую («＋ Новая заметка»)."
        : "Под фильтрами ничего не нашлось — измените поиск или вид привязки.";

    /// <summary>Всего заметок в дневнике (без учёта фильтров).</summary>
    public int TotalCount => _all.Count;

    // ─── Фильтры списка ───

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    /// <summary>Фильтр по виду привязки: «Все заметки» / без привязки / … / цитата.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> KindFilters { get; } =
    [
        new(AllKindsKey, "Все заметки"),
        new(NoteBindings.KeyNone, "Без привязки"),
        new(NoteBindings.KeyFile, "Файл"),
        new(NoteBindings.KeyEntity, "Сущность"),
        new(NoteBindings.KeyPattern, "Паттерн"),
        new(NoteBindings.KeyQuote, "Цитата"),
    ];

    private string _kindFilter = AllKindsKey;
    public string KindFilter
    {
        get => _kindFilter;
        set { if (Set(ref _kindFilter, value ?? AllKindsKey)) ApplyFilter(); }
    }

    // ─── Редактор заметки ───

    private DiaryRow? _selected;
    public DiaryRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value) || value is null) return;
            LoadEditor(value.Note);
        }
    }

    private StoredNote? _editing;

    /// <summary>В редакторе — новая (ещё не сохранённая) заметка.</summary>
    public bool IsNewNote => _editing is null;

    private string _editTitle = "";
    public string EditTitle { get => _editTitle; set => Set(ref _editTitle, value); }

    private string _editBody = "";
    public string EditBody { get => _editBody; set => Set(ref _editBody, value); }

    /// <summary>Виды привязки для выпадающего списка редактора.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> BindingKinds { get; } =
    [
        new(NoteBindings.KeyNone, "Без привязки"),
        new(NoteBindings.KeyFile, "Файл"),
        new(NoteBindings.KeyEntity, "Сущность"),
        new(NoteBindings.KeyPattern, "Паттерн"),
        new(NoteBindings.KeyQuote, "Цитата"),
    ];

    private string _editKindKey = NoteBindings.KeyNone;
    public string EditKindKey
    {
        get => _editKindKey;
        set
        {
            if (!Set(ref _editKindKey, value ?? NoteBindings.KeyNone)) return;
            RebuildBindingOptions();
            EditBindingText = "";
            _pendingRef = null;
            _pendingLabel = null;
            OnPropertyChanged(nameof(BindingEnabled));
        }
    }

    /// <summary>Активен ли выбор значения привязки (вид не «без привязки»).</summary>
    public bool BindingEnabled => NoteBindings.Parse(_editKindKey) != NoteBindingKind.None;

    /// <summary>Варианты значений привязки для выбранного вида.</summary>
    public ObservableCollection<BindingOption> BindingOptions { get; } = [];

    private BindingOption? _selectedBinding;
    public BindingOption? SelectedBinding
    {
        get => _selectedBinding;
        set
        {
            if (!Set(ref _selectedBinding, value)) return;
            if (value is not null) EditBindingText = value.Display;
        }
    }

    /// <summary>Текст привязки: выбранный вариант или значение, введённое вручную.</summary>
    private string _editBindingText = "";
    public string EditBindingText { get => _editBindingText; set => Set(ref _editBindingText, value); }

    // Привязка, предложенная кнопкой «✎», но отсутствующая в списке вариантов:
    // в поле хранится человеческая подпись, а в ref — технический ключ (например,
    // ключ неотмеченной цитаты). Если пользователь правит текст — используется текст.
    private string? _pendingRef;
    private string? _pendingLabel;

    private string _editorMeta = "Новая заметка";
    public string EditorMeta { get => _editorMeta; private set => Set(ref _editorMeta, value); }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public RelayCommand NewCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ExportCommand { get; }

    public DiaryViewModel(MainViewModel main, NoteStore? store = null)
    {
        _main = main;
        _store = store ?? NoteStore.Default;
        NewCommand = new RelayCommand(_ => StartNew());
        SaveCommand = new RelayCommand(_ => Save());
        DeleteCommand = new RelayCommand(_ => Delete(), _ => _editing is not null);
        ExportCommand = new RelayCommand(_ => Export(), _ => _all.Count > 0);
    }

    /// <summary>Перечитать заметки (вызывается при входе в раздел).</summary>
    public void Refresh()
    {
        _all = [.. _store.LoadAll()];

        // Заметка могла исчезнуть из БД — редактор возвращается к новой заметке.
        if (_editing is not null && _all.All(n => n.Id != _editing.Id))
        {
            _editing = null;
            OnPropertyChanged(nameof(IsNewNote));
        }

        var keepId = _editing?.Id;
        ApplyFilter();
        if (keepId is { } id)
        {
            var row = Notes.FirstOrDefault(r => r.Note.Id == id);
            if (row is not null)
            {
                _selected = row; // тихо: редактор уже заполнен этой заметкой
                OnPropertyChanged(nameof(Selected));
            }
        }
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Начать новую пустую заметку.</summary>
    public void StartNew()
    {
        _editing = null;
        OnPropertyChanged(nameof(IsNewNote));
        EditTitle = "";
        EditBody = "";
        EditKindKey = NoteBindings.KeyNone; // сбрасывает варианты и текст привязки
        _selected = null;
        OnPropertyChanged(nameof(Selected));
        EditorMeta = "Новая заметка";
        Status = "";
    }

    /// <summary>
    /// Создать заметку с готовой привязкой — используется кнопками «✎»
    /// на страницах «Результаты», «Кодекс» и в паттернах.
    /// </summary>
    public void StartNewNote(NoteBindingKind kind, string reference, string label)
    {
        StartNew();
        EditKindKey = NoteBindings.ToKey(kind);
        var match = BindingOptions.FirstOrDefault(o =>
            string.Equals(o.Reference, reference, StringComparison.Ordinal));
        if (match is not null)
        {
            SelectedBinding = match;
        }
        else
        {
            _pendingRef = reference;
            _pendingLabel = string.IsNullOrWhiteSpace(label) ? reference : label;
            EditBindingText = _pendingLabel;
        }
        EditorMeta = $"Новая заметка · {NoteBindings.Describe(kind, reference, label)}";
    }

    private void LoadEditor(StoredNote note)
    {
        _editing = note;
        OnPropertyChanged(nameof(IsNewNote));
        EditTitle = note.Title;
        EditBody = note.Body;
        EditKindKey = NoteBindings.ToKey(note.Binding); // пересобирает варианты
        var match = BindingOptions.FirstOrDefault(o =>
            string.Equals(o.Reference, note.BindingRef, StringComparison.Ordinal));
        if (match is not null)
        {
            SelectedBinding = match;
        }
        else
        {
            _selectedBinding = null;
            OnPropertyChanged(nameof(SelectedBinding));
            EditBindingText = string.IsNullOrWhiteSpace(note.BindingLabel) ? note.BindingRef : note.BindingLabel;
        }
        EditorMeta = $"Создано {note.CreatedAt:yyyy-MM-dd HH:mm} · обновлено {note.UpdatedAt:yyyy-MM-dd HH:mm}";
        Status = "";
    }

    private void ApplyFilter()
    {
        var q = _search.Trim();
        var kindKey = _kindFilter;
        Notes.Clear();
        foreach (var n in _all)
        {
            if (kindKey != AllKindsKey && NoteBindings.ToKey(n.Binding) != kindKey) continue;
            if (q.Length > 0 && !Matches(n, q)) continue;
            Notes.Add(new DiaryRow(n));
        }
        OnPropertyChanged(nameof(IsDataEmpty));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(TotalCount));
        CommandManager.InvalidateRequerySuggested();
    }

    private static bool Matches(StoredNote n, string q) =>
        Contains(n.Title, q) || Contains(n.Body, q) ||
        Contains(n.BindingRef, q) || Contains(n.BindingLabel, q);

    private static bool Contains(string? hay, string q) =>
        hay?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Пересобрать варианты привязки под выбранный вид.</summary>
    private void RebuildBindingOptions()
    {
        BindingOptions.Clear();
        _selectedBinding = null;
        OnPropertyChanged(nameof(SelectedBinding));

        switch (NoteBindings.Parse(_editKindKey))
        {
            case NoteBindingKind.File:
                foreach (var f in _main.Session?.Files ?? [])
                    BindingOptions.Add(new BindingOption(f.FullPath, f.FileName));
                break;

            case NoteBindingKind.Entity:
                foreach (var card in _main.CodexVm.AllCards
                             .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                    BindingOptions.Add(new BindingOption(card.Name, card.Name));
                break;

            case NoteBindingKind.Pattern:
                foreach (var p in _main.AdvancedSearch.Patterns)
                    BindingOptions.Add(new BindingOption(p.Id, p.Name));
                break;

            case NoteBindingKind.Quote:
                foreach (var q in QuoteStore.Default.AllStarred())
                    BindingOptions.Add(new BindingOption(q.Key, QuoteLabel(q)));
                break;
        }
    }

    private static string QuoteLabel(QuoteRecord q)
    {
        var text = (q.Context ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length > 70) text = text[..69] + "…";
        return $"{text} — {q.Entity}, {q.FileName}";
    }

    private void Save()
    {
        var title = EditTitle.Trim();
        var body = EditBody;
        if (title.Length == 0 && body.Trim().Length == 0)
        {
            Status = "Пустая заметка не сохраняется — добавьте заголовок или текст.";
            return;
        }

        // Разрешение привязки: выбранный вариант либо введённый вручную текст.
        var kind = NoteBindings.Parse(EditKindKey);
        var reference = "";
        var label = "";
        if (kind != NoteBindingKind.None)
        {
            var text = EditBindingText.Trim();
            if (SelectedBinding is not null &&
                string.Equals(SelectedBinding.Display, text, StringComparison.Ordinal))
            {
                reference = SelectedBinding.Reference;
                label = SelectedBinding.Label;
            }
            else if (_pendingRef is not null && _pendingLabel is not null &&
                     string.Equals(_pendingLabel, text, StringComparison.Ordinal))
            {
                // Подпись от кнопки «✎» не правилась — сохраняем исходный ключ.
                reference = _pendingRef;
                label = _pendingLabel;
            }
            else
            {
                reference = text;
                label = text;
            }
            if (reference.Length == 0)
            {
                Status = "Выберите или введите значение привязки (либо верните «Без привязки»).";
                return;
            }
        }

        if (_editing is null)
        {
            var saved = _store.Add(title, body, kind, reference, label);
            if (saved is null)
            {
                Status = "Не удалось сохранить заметку — дневник недоступен.";
                return;
            }
            ReloadAndSelect(saved.Id);
        }
        else
        {
            var changed = _editing with
            {
                Title = title,
                Body = body,
                Binding = kind,
                BindingRef = reference,
                BindingLabel = label,
            };
            if (!_store.Update(changed))
            {
                Status = "Не удалось обновить заметку — дневник недоступен.";
                return;
            }
            ReloadAndSelect(changed.Id);
        }

        Status = "Заметка сохранена";
        _main.StatusText = "Дневник: заметка сохранена";
        _main.Dashboard.Log($"✎ Дневник: сохранена заметка «{(title.Length > 0 ? title : "без названия")}»");
        _main.Track("diary-note-saved", kind.ToString());
    }

    private void ReloadAndSelect(long id)
    {
        Refresh();
        var row = Notes.FirstOrDefault(r => r.Note.Id == id);
        if (row is null) return;
        _selected = row;
        OnPropertyChanged(nameof(Selected));
        LoadEditor(row.Note);
    }

    private void Delete()
    {
        if (_editing is not { } note) return;
        var name = string.IsNullOrWhiteSpace(note.Title) ? "без названия" : note.Title.Trim();
        var answer = System.Windows.MessageBox.Show(
            $"Удалить заметку «{name}»? Действие необратимо.",
            "Удаление заметки",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        if (!_store.Delete(note.Id))
        {
            Status = "Не удалось удалить заметку — дневник недоступен.";
            return;
        }
        StartNew();
        Refresh();
        Status = "Заметка удалена";
        _main.StatusText = "Дневник: заметка удалена";
        _main.Track("diary-note-deleted", "");
    }

    private void Export()
    {
        // Экспортируется текущий отфильтрованный список: «что вижу — то и в файле».
        var snapshot = Notes.Select(r => r.Note).ToList();
        if (snapshot.Count == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт дневника в Markdown",
            Filter = "Markdown|*.md",
            FileName = $"diary_{DateTime.Now:yyyyMMdd_HHmm}.md",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, DiaryMarkdown.Build(snapshot, DateTimeOffset.Now),
                System.Text.Encoding.UTF8);
            _main.StatusText = $"Дневник экспортирован: {dlg.FileName}";
            _main.Dashboard.Log($"✎ Дневник экспортирован ({snapshot.Count} заметок) в {dlg.FileName}");
        }
        catch (Exception ex)
        {
            _main.StatusText = $"Ошибка экспорта дневника: {ex.Message}";
        }
    }
}

/// <summary>Строка списка заметок: подписи и фрагмент текста.</summary>
public sealed class DiaryRow
{
    public StoredNote Note { get; }

    public DiaryRow(StoredNote note) => Note = note;

    public string Title => string.IsNullOrWhiteSpace(Note.Title) ? "Без названия" : Note.Title.Trim();

    public string Snippet
    {
        get
        {
            var b = (Note.Body ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
            return b.Length > 120 ? b[..119] + "…" : b;
        }
    }

    public string BindingText => Note.BindingText;
    public bool HasBinding => Note.BindingText.Length > 0;
    public string DatesText => $"обновлено {Note.UpdatedAt:yyyy-MM-dd HH:mm}";
}

/// <summary>Вариант значения привязки для выпадающего списка редактора.</summary>
public sealed class BindingOption
{
    public string Reference { get; }
    public string Label { get; }

    public BindingOption(string reference, string label)
    {
        Reference = reference ?? "";
        Label = label ?? "";
    }

    /// <summary>Отображаемый текст: подпись, а при её отсутствии — ссылка.</summary>
    public string Display => string.IsNullOrWhiteSpace(Label) ? Reference : Label;
}
