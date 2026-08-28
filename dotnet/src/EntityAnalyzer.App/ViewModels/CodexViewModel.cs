using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.App.Services;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Exporting;
using EntityAnalyzer.Core.Logging;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Variants;
using Microsoft.Win32;
using EntityAnalyzer.App.Views;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>Root of the card encyclopedia UI: filters, gallery/table, editor.</summary>
public sealed class CodexViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppSettings _settings;

    public CodexDocument Doc { get; private set; }

    /// <summary>Filtered cards shown in gallery/table.</summary>
    public ObservableCollection<Card> Cards { get; } = [];

    /// <summary>All cards for link-target pickers.</summary>
    public IReadOnlyList<Card> AllCards => Doc.Cards;

    // ─── Filters ───

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set { if (Set(ref _searchQuery, value)) ApplyFilters(); }
    }

    private string _selectedType = "Все категории";
    public string SelectedType
    {
        get => _selectedType;
        set { if (Set(ref _selectedType, value)) ApplyFilters(); }
    }

    private string _selectedGroup = "Все группы";
    public string SelectedGroup
    {
        get => _selectedGroup;
        set { if (Set(ref _selectedGroup, value)) ApplyFilters(); }
    }

    private string? _selectedTag;
    public string? SelectedTag
    {
        get => _selectedTag;
        set { if (Set(ref _selectedTag, value)) ApplyFilters(); }
    }

    private bool _onlyMentioned;
    public bool OnlyMentioned
    {
        get => _onlyMentioned;
        set { if (Set(ref _onlyMentioned, value)) ApplyFilters(); }
    }

    private SortMode _sort = SortMode.ByName;
    public SortMode Sort
    {
        get => _sort;
        set { if (Set(ref _sort, value)) ApplyFilters(); }
    }

    public ObservableCollection<string> TypeNames { get; } = ["Все категории"];
    public ObservableCollection<string> GroupNames { get; } = ["Все группы"];
    public ObservableCollection<string> TagNames { get; } = [];

    // ─── View mode & stats ───

    private bool _isGalleryView = true;
    public bool IsGalleryView
    {
        get => _isGalleryView;
        set { if (Set(ref _isGalleryView, value)) OnViewModeChanged(); }
    }
    public bool IsTableView => !_isGalleryView && !_isGraphView;

    private bool _isGraphView; // R2-7: третий вид — граф совместных упоминаний
    public bool IsGraphView
    {
        get => _isGraphView;
        set { if (Set(ref _isGraphView, value)) OnViewModeChanged(); }
    }

    private void OnViewModeChanged()
    {
        OnPropertyChanged(nameof(IsTableView));
        OnPropertyChanged(nameof(IsGraphView));
        GraphInvalidated?.Invoke();
    }

    /// <summary>Code-behind подписывается: перерисовать канву графа.</summary>
    public event Action? GraphInvalidated;

    private string _statsText = "";
    public string StatsText { get => _statsText; private set => Set(ref _statsText, value); }

    /// <summary>Empty State: после фильтров не осталось ни одной карточки.</summary>
    public bool IsDataEmpty => Cards.Count == 0;

    // ─── Граф совместных упоминаний (R2-7) ───

    /// <summary>Есть ли данные для графа (сессия последнего анализа).</summary>
    public bool GraphAvailable => _main.Session is not null;

    public string GraphEmptyText => GraphAvailable
        ? ""
        : "Граф появится после первого анализа корпуса — рёбра строятся из совместных упоминаний.";

    /// <summary>Фильтры пяти ветвей (индексы совпадают с BranchResolver.ResolveIndex).</summary>
    public bool[] BranchFilter { get; } = [true, true, true, true, true];

    /// <summary>Пересобрать граф с учётом фильтров ветвей. null — нет данных.</summary>
    public CoOccurrenceGraph? GetFilteredGraph()
    {
        var session = _main.Session;
        if (session is null) return null;
        var full = CoOccurrenceGraph.Build(session);
        var nodes = full.Nodes.Where(n => BranchFilter[BranchResolver.ResolveIndex(n.Name)]).ToList();
        var names = nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        var links = full.Links.Where(l => names.Contains(l.Source) && names.Contains(l.Target)).ToList();
        return new CoOccurrenceGraph(nodes, links);
    }

    /// <summary>Выбрать карточку по имени сущности графа (клик по узлу).</summary>
    public void SelectCardByName(string name)
    {
        var card = Doc.Cards.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Canonical, name, StringComparison.OrdinalIgnoreCase) ||
            c.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (card is not null) Selected = card;
    }

    // ─── Selection & editor draft ───

    private Card? _selected;
    public Card? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            LoadDraft();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelection => Selected is not null;

    private string _editName = "";
    public string EditName { get => _editName; set { if (Set(ref _editName, value)) MarkDirty(); } }

    private string _editCanonical = "";
    public string EditCanonical { get => _editCanonical; set { if (Set(ref _editCanonical, value)) MarkDirty(); } }

    private string _editAliases = "";
    public string EditAliases
    {
        get => _editAliases;
        set
        {
            if (Set(ref _editAliases, value))
            {
                MarkDirty();
                OnPropertyChanged(nameof(EditAliasesCount));
            }
        }
    }

    /// <summary>Число непустых строк-алиасов (для заголовка сворачиваемого блока).</summary>
    public int EditAliasesCount => _editAliases
        .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Length;

    private string _editGroup = "";
    public string EditGroup { get => _editGroup; set { if (Set(ref _editGroup, value)) MarkDirty(); } }

    private string _editTags = "";
    public string EditTags { get => _editTags; set { if (Set(ref _editTags, value)) MarkDirty(); } }

    private string _editDescription = "";
    public string EditDescription { get => _editDescription; set { if (Set(ref _editDescription, value)) MarkDirty(); } }

    private string? _editColor;
    public string? EditColor { get => _editColor; set { if (Set(ref _editColor, value)) MarkDirty(); } }

    private string _editImage = "";
    public string EditImage { get => _editImage; set { if (Set(ref _editImage, value)) MarkDirty(); } }

    private bool _dirty;
    public bool IsDirty { get => _dirty; private set { if (Set(ref _dirty, value)) OnPropertyChanged(nameof(DirtyHint)); } }
    public bool HasSelectionAndDirty => HasSelection && IsDirty;
    public string DirtyHint => IsDirty ? "•  есть несохранённые правки" : "";

    /// <summary>Dynamic schema-driven rows for the editor form.</summary>
    public ObservableCollection<FieldRowVM> FieldRows { get; } = [];

    // Custom per-card field creation
    private string _newFieldName = "";
    public string NewFieldName { get => _newFieldName; set => Set(ref _newFieldName, value); }

    private FieldType _newFieldType = FieldType.Text;
    public FieldType NewFieldType { get => _newFieldType; set => Set(ref _newFieldType, value); }

    public IReadOnlyList<FieldType> AllFieldTypes { get; } =
        [.. Enum.GetValues<FieldType>()];

    // ─── Links ───

    public ObservableCollection<LinkRowVM> LinkRows { get; } = [];

    private Card? _newLinkTarget;
    public Card? NewLinkTarget { get => _newLinkTarget; set => Set(ref _newLinkTarget, value); }

    private RelationType? _newLinkRelation;
    public RelationType? NewLinkRelation { get => _newLinkRelation; set => Set(ref _newLinkRelation, value); }

    private string _newLinkNote = "";
    public string NewLinkNote { get => _newLinkNote; set => Set(ref _newLinkNote, value); }

    // ─── Path finder ───

    private Card? _pathTarget;
    public Card? PathTarget { get => _pathTarget; set => Set(ref _pathTarget, value); }

    private string _pathResult = "";
    public string PathResult { get => _pathResult; private set => Set(ref _pathResult, value); }

    // ─── Merge duplicates ───

    private Card? _mergeSource;
    public Card? MergeSource { get => _mergeSource; set => Set(ref _mergeSource, value); }

    // ─── Suggestions from analysis ───

    public ObservableCollection<LinkProposal> Suggestions { get; } = [];

    private string _suggestionsHint = "";
    public string SuggestionsHint { get => _suggestionsHint; private set => Set(ref _suggestionsHint, value); }

    // ─── Mentions tab ───

    public ObservableCollection<MentionRowVM> Mentions { get; } = [];

    private MentionRowVM? _selectedMention;
    public MentionRowVM? SelectedMention
    {
        get => _selectedMention;
        set
        {
            if (!Set(ref _selectedMention, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public RelayCommand OpenFullTextCommand { get; private set; } = null!;

    // ─── Commands ───

    public RelayCommand AddCard { get; }
    public RelayCommand DeleteCard { get; }
    public RelayCommand ApplyEdits { get; }
    /// <summary>QA-UX #11: откат черновика к сохранённым значениям карточки.</summary>
    public RelayCommand DiscardEdits { get; private set; } = null!;
    public RelayCommand PickImage { get; }
    public RelayCommand AddCustomField { get; }
    public RelayCommand AddLink { get; }
    public RelayCommand RemoveLink { get; }
    public RelayCommand FindPathCommand { get; }
    public RelayCommand MergeCommand { get; }
    public RelayCommand AcceptSuggestions { get; }
    public RelayCommand ExportMarkdown { get; }
    public RelayCommand ExportJson { get; }
    /// <summary>R1-17: Anki-колода для интервального повторения.</summary>
    public RelayCommand ExportAnki { get; }
    /// <summary>R1-17: Zotero/Excel CSV (симметричен импорту).</summary>
    public RelayCommand ExportResearchCsv { get; }
    public RelayCommand ImportJson { get; }
    public RelayCommand ImportCsv { get; }
    public RelayCommand OpenWorldGraph { get; }
    public RelayCommand ResetToEmbedded { get; }
    public AsyncRelayCommand RebuildIndex { get; private set; } = null!;

    private readonly CatalogChangeLog _changeLog;

    public CodexViewModel(MainViewModel main, AppSettings settings)
    {
        _main = main;
        _settings = settings;
        Doc = main.Codex;

        _changeLog = new CatalogChangeLog(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EntityAnalyzer", "catalog_history.jsonl"));

        AddCard = new RelayCommand(_ =>
        {
            CommitIfDirty();
            var type = Doc.Types.FirstOrDefault(t => t.Name == (SelectedType != "Все категории" ? SelectedType : null))
                       ?? Doc.Types[0];
            var card = Card.Create(type.Id, $"Новая карточка {Doc.Cards.Count + 1}");
            Doc.Cards.Add(card);
            Record("создана", card.Name, $"категория «{type.Name}»");
            RefreshCollections();
            ApplyFilters();
            Selected = card;
        });

        DeleteCard = new RelayCommand(_ =>
        {
            if (Selected is null) return;
            var name = Selected.Name;
            for (int i = Doc.Links.Count - 1; i >= 0; i--)
                if (Doc.Links[i].FromId == Selected.Id || Doc.Links[i].ToId == Selected.Id)
                    Doc.Links.RemoveAt(i);
            Doc.Cards.Remove(Selected);
            Record("удалена", name, "вместе со связями");
            Selected = null;
            RefreshCollections();
            ApplyFilters();
            Save();
        }, _ => HasSelection);

        ApplyEdits = new RelayCommand(_ => CommitDraft(), _ => HasSelection && IsDirty);
        // QA-UX #11: явная отмена черновика — раньше единственный способ
        // «отменить» был случайный автопримен при переключении карточки.
        DiscardEdits = new RelayCommand(_ => LoadDraft(), _ => HasSelection && IsDirty);

        PickImage = new RelayCommand(_ =>
        {
            var dlg = new OpenFileDialog { Title = "Изображение карточки", Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (dlg.ShowDialog() == true) EditImage = dlg.FileName;
        });

        AddCustomField = new RelayCommand(_ =>
        {
            if (Selected is null || string.IsNullOrWhiteSpace(NewFieldName)) return;
            var id = "custom_" + Guid.NewGuid().ToString("N")[..8];
            Selected.Fields[id] = NewFieldType switch
            {
                FieldType.Number => FieldValue.FromNumber(0),
                FieldType.Flag => FieldValue.FromFlag(false),
                _ => FieldValue.Empty,
            };
            // register in the category schema so the row renders with the right editor
            var type = Doc.FindType(Selected.TypeId);
            type?.Fields.Add(new FieldSchema
            {
                Id = id,
                Name = NewFieldName.Trim(),
                Type = NewFieldType,
                Order = 900 + type.Fields.Count,
            });
            Record("поле добавлено", Selected.Name, $"«{NewFieldName.Trim()}» ({NewFieldType})");
            NewFieldName = "";
            LoadFieldRows();
            Save();
        }, _ => HasSelection && !string.IsNullOrWhiteSpace(NewFieldName));

        AddLink = new RelayCommand(_ =>
        {
            if (Selected is null || NewLinkTarget is null || NewLinkRelation is null) return;
            if (NewLinkTarget.Id == Selected.Id) return;
            if (Doc.Links.Any(l => l.FromId == Selected.Id && l.ToId == NewLinkTarget.Id && l.RelationId == NewLinkRelation.Id))
                return;
            Doc.Links.Add(new CardLink(Selected.Id, NewLinkTarget.Id, NewLinkRelation.Id, NewLinkNote.Trim()));
            Record("связь", Selected.Name, $"—{NewLinkRelation.ForwardLabel}→ {NewLinkTarget.Name}");
            NewLinkNote = "";
            LoadLinks();
            Save();
        }, _ => HasSelection && NewLinkTarget is not null && NewLinkRelation is not null);

        RemoveLink = new RelayCommand(param =>
        {
            if (param is not LinkRowVM row || Selected is null) return;
            var link = Doc.Links.FirstOrDefault(l => l.FromId == row.FromId && l.ToId == row.ToId && l.RelationId == row.RelationId);
            if (link is null) return;
            Doc.Links.Remove(link);
            Record("связь удалена", Selected.Name, $"{row.FromName} → {row.ToName}");
            LoadLinks();
            Save();
        });

        FindPathCommand = new RelayCommand(_ =>
        {
            if (Selected is null || PathTarget is null) return;
            var steps = CodexService.FindPath(Doc, Selected.Id, PathTarget.Id, maxDepth: 4);
            PathResult = steps.Length == 0
                ? "Путь не найден (глубина ≤ 4)"
                : string.Join(" → ", steps.Select(s => $"{s.FromName} —{s.Label}→ {s.ToName}"));
        }, _ => HasSelection && PathTarget is not null);

        MergeCommand = new RelayCommand(_ =>
        {
            if (Selected is null || MergeSource is null || MergeSource.Id == Selected.Id) return;
            var report = CodexService.MergeCards(Doc, Selected.Id, MergeSource.Id);
            Record("объединение", Selected.Name, report);
            MergeSource = null;
            RefreshCollections();
            ApplyFilters();
            LoadLinks();
            Save();
            _main.Dashboard.Log("⚖ " + report);
        }, _ => HasSelection && MergeSource is not null);

        AcceptSuggestions = new RelayCommand(_ =>
        {
            var added = CodexService.AcceptProposals(Doc, [.. Suggestions], "cooccur");
            Record("предложения", "", $"принято связей: {added}");
            Suggestions.Clear();
            SuggestionsHint = "";
            Save();
            RebuildIndex.Execute(null);
            _main.Dashboard.Log($"🔗 Принято предложений связей: {added}");
        }, _ => Suggestions.Count > 0);

        ExportMarkdown = new RelayCommand(_ =>
        {
            var dlg = new OpenFolderDialog { Title = "Папка для Markdown-заметок" };
            if (dlg.ShowDialog() != true) return;
            CodexExporters.WriteMarkdownNotes(dlg.FolderName, Doc);
            Record("экспорт", "Markdown", dlg.FolderName);
            _main.Dashboard.Log($"⬇ Markdown-заметки: {dlg.FolderName}");
        }, _ => Doc.Cards.Count > 0);

        ExportJson = new RelayCommand(_ =>
        {
            var dlg = new SaveFileDialog { Title = "Полный JSON кодекса", Filter = "JSON|*.json", FileName = "codex_export.json" };
            if (dlg.ShowDialog() != true) return;
            CodexExporters.WriteFullJson(dlg.FileName, Doc);
            Record("экспорт", "JSON", dlg.FileName);
            _main.Dashboard.Log($"⬇ Кодекс сохранён: {dlg.FileName}");
        }, _ => Doc.Cards.Count > 0);

        // R1-17: экспорт в экосистемы исследователя.
        ExportAnki = new RelayCommand(_ =>
        {
            var dlg = new SaveFileDialog
            {
                Title = "Anki-колода",
                Filter = "TSV для Anki|*.txt",
                FileName = "codex_anki_deck.txt",
            };
            if (dlg.ShowDialog() != true) return;
            CodexExporters.WriteAnkiDeck(dlg.FileName, Doc);
            Record("экспорт", "Anki", dlg.FileName);
            _main.Dashboard.Log($"⬇ Anki-колода: {dlg.FileName} ({Doc.Cards.Count} карточек)");
        }, _ => Doc.Cards.Count > 0);

        ExportResearchCsv = new RelayCommand(_ =>
        {
            var dlg = new SaveFileDialog
            {
                Title = "Zotero/Excel CSV",
                Filter = "CSV|*.csv",
                FileName = "codex_research.csv",
            };
            if (dlg.ShowDialog() != true) return;
            CodexExporters.WriteResearchCsv(dlg.FileName, Doc);
            Record("экспорт", "CSV", dlg.FileName);
            _main.Dashboard.Log($"⬇ Zotero/Excel CSV: {dlg.FileName}");
        }, _ => Doc.Cards.Count > 0);

        ImportJson = new RelayCommand(_ =>
        {
            var dlg = new OpenFileDialog { Title = "Импорт кодекса из JSON", Filter = "JSON|*.json" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var imported = CodexDocument.FromJson(File.ReadAllText(dlg.FileName));
                int before = Doc.Cards.Count;
                foreach (var t in imported.Types)
                    if (Doc.Types.All(x => x.Id != t.Id && x.Name != t.Name)) Doc.Types.Add(t);
                foreach (var r in imported.Relations)
                    if (Doc.Relations.All(x => x.Id != r.Id)) Doc.Relations.Add(r);
                foreach (var c in imported.Cards)
                    if (Doc.Cards.All(x => !string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase)))
                    { Doc.Cards.Add(c); }
                foreach (var l in imported.Links)
                    if (Doc.Cards.Any(c => c.Id == l.FromId) && Doc.Cards.Any(c => c.Id == l.ToId) &&
                        Doc.Links.All(x => x.FromId != l.FromId || x.ToId != l.ToId || x.RelationId != l.RelationId))
                        Doc.Links.Add(l);
                Record("импорт", "JSON", $"+{Doc.Cards.Count - before} карточек из {dlg.FileName}");
                AfterStructureChange();
                _main.Dashboard.Log($"⬆ Импортировано карточек: {Doc.Cards.Count - before}");
            }
            catch (Exception ex)
            {
                _main.Dashboard.Log($"✗ Импорт не удался: {ex.Message}");
            }
        });

        ImportCsv = new RelayCommand(_ =>
        {
            var dlg = new OpenFileDialog { Title = "Массовый импорт CSV", Filter = "CSV|*.csv;*.txt" };
            if (dlg.ShowDialog() != true) return;
            var (created, errors) = CodexExporters.ImportCsv(Doc, File.ReadAllText(dlg.FileName));
            Record("импорт", "CSV", $"+{created} карточек, ошибок {errors.Count}");
            foreach (var e in errors.Take(5)) _main.Dashboard.Log($"⚠ CSV: {e}");
            AfterStructureChange();
            _main.Dashboard.Log($"⬆ CSV: создано карточек {created}, ошибок {errors.Count}");
        });

        OpenWorldGraph = new RelayCommand(_ =>
        {
            var outDir = Path.IsPathRooted(_settings.OutputDir)
                ? _settings.OutputDir
                : Path.Combine(AppContext.BaseDirectory, _settings.OutputDir);
            Directory.CreateDirectory(outDir);
            var file = Path.Combine(outDir, "world_atlas.html");
            File.WriteAllText(file, CodexExporters.BuildHtmlAtlas(Doc));
            Record("граф", "мир", file);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = true,
            });
        }, _ => Doc.Cards.Count > 0);

        ResetToEmbedded = new RelayCommand(_ =>
        {
            // UX: опасное действие — за предохранителем. Перечисляем, что будет потеряно.
            var msg = "Заменить текущий кодекс встроенным справочником?\n\n" +
                      $"Будут удалены ВСЕ ваши изменения: карточек в кодексе сейчас {Doc.Cards.Count}, связей {Doc.Links.Count}.\n" +
                      "Действие нельзя отменить (экспортируйте кодекс кнопками MD/JSON, если данные нужны).";
            var answer = System.Windows.MessageBox.Show(
                msg, "Сбросить кодекс к встроенному?",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            Doc = DefaultCodex.Load();
            UserCodexStore.Delete();
            Record("сброс", "кодекс", "к встроенному каталогу");
            Selected = null;
            AfterStructureChange();
            RebuildIndex.Execute(null);
            _main.Dashboard.Log("⟲ Кодекс сброшен к встроенному справочнику.");
        });

        RebuildIndex = new AsyncRelayCommand(RebuildCatalogAsync);

        OpenFullTextCommand = new RelayCommand(_ =>
        {
            if (SelectedMention is null) return;
            new FileViewerWindow(SelectedMention.FilePath, SelectedMention.AllMentions, SelectedMention.Entity).Show();
        }, _ => SelectedMention is not null);

        RefreshCollections();
        ApplyFilters();
    }

    // ─────────────────────── data flow ───────────────────────

    /// <summary>Reloads everything after the document was replaced/migrated.</summary>
    public void ReloadFrom(CodexDocument doc)
    {
        Doc = doc;
        Selected = null;
        AfterStructureChange();
    }

    /// <summary>Public hook for the schema editor: categories/relations changed.</summary>
    public void RefreshCollectionsPublic() => RefreshCollections();

    private void AfterStructureChange()
    {
        RefreshCollections();
        ApplyFilters();
        Save();
    }

    private void RefreshCollections()
    {
        TypeNames.Clear();
        TypeNames.Add("Все категории");
        foreach (var t in Doc.Types) TypeNames.Add(t.Name);

        GroupNames.Clear();
        GroupNames.Add("Все группы");
        foreach (var g in Doc.Cards.Select(c => c.Group).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g))
            GroupNames.Add(g);

        TagNames.Clear();
        foreach (var t in Doc.Cards.SelectMany(c => c.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t))
            TagNames.Add(t);

        UpdateStats();
    }

    private void UpdateStats()
    {
        var counts = CodexService.TypeCounts(Doc);
        StatsText = $"Карточек: {Doc.Cards.Count} · связей: {Doc.Links.Count} · " +
                    string.Join(" · ", counts.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    /// <summary>Applies current filter state to the visible card list.</summary>
    public void ApplyFilters()
    {
        var mentioned = OnlyMentioned ? _mentionedNames : null;
        var typeId = SelectedType == "Все категории" ? "" :
            Doc.Types.FirstOrDefault(t => t.Name == SelectedType)?.Id ?? "";

        var filtered = CodexService.FilterCards(
            Doc,
            query: SearchQuery,
            typeId: typeId,
            group: SelectedGroup == "Все группы" ? "" : SelectedGroup,
            tagsAny: SelectedTag is null ? null : [SelectedTag],
            mentionedNames: mentioned,
            mentionsByCard: _mentionsByCard,
            sort: Sort);

        Cards.Clear();
        foreach (var c in filtered) Cards.Add(c);
        UpdateStats();
        OnPropertyChanged(nameof(IsDataEmpty));
    }

    private HashSet<string>? _mentionedNames;
    private Dictionary<string, int>? _mentionsByCard;

    /// <summary>Called after each analysis to power the «только с упоминаниями» filter and sorting.</summary>
    public void SetAnalysisData(AnalysisSession session)
    {
        _mentionedNames = [.. session.Files.SelectMany(f => f.Mentions).Select(m => m.Entity).Distinct()];
        _mentionsByCard = _mentionedNames.ToDictionary(n => n, n =>
            session.Files.Sum(f => f.Mentions.Count(m => m.Entity == n)));

        // Suggestions from co-occurrence.
        var pairs = CoOccurrenceGraph.Build(session).Links.Select(l => (l.Source, l.Target, l.Weight));
        var proposals = CodexService.SuggestLinks(Doc, pairs);
        Suggestions.Clear();
        foreach (var p in proposals.Take(20)) Suggestions.Add(p);
        SuggestionsHint = proposals.Count == 0
            ? ""
            : $"Найдено {proposals.Count} пар, часто упоминающихся вместе — можно создать связи.";

        ApplyFilters();
    }

    // ─────────────────────── editor draft ───────────────────────

    private void MarkDirty()
    {
        IsDirty = true;
        OnPropertyChanged(nameof(HasSelectionAndDirty));
        CommandManager.InvalidateRequerySuggested();
    }

    private void CommitIfDirty()
    {
        if (HasSelection && IsDirty) CommitDraft();
    }

    private void LoadDraft()
    {
        var d = Selected;
        _editName = d?.Name ?? "";
        _editCanonical = d?.Canonical ?? "";
        _editAliases = d is null ? "" : string.Join("\n", d.Aliases.Where(a => a != d!.Name && a != d.Canonical));
        _editGroup = d?.Group ?? "";
        _editTags = d is null ? "" : string.Join(", ", d.Tags);
        _editDescription = d?.Description ?? "";
        _editColor = d?.ColorMark;
        _editImage = d?.ImagePath ?? "";

        OnPropertyChanged(nameof(EditName));
        OnPropertyChanged(nameof(EditCanonical));
        OnPropertyChanged(nameof(EditAliases));
        OnPropertyChanged(nameof(EditGroup));
        OnPropertyChanged(nameof(EditTags));
        OnPropertyChanged(nameof(EditDescription));
        OnPropertyChanged(nameof(EditColor));
        OnPropertyChanged(nameof(EditImage));

        IsDirty = false;
        OnPropertyChanged(nameof(HasSelectionAndDirty));
        LoadFieldRows();
        LoadLinks();
        LoadMentions();
        PathResult = "";
    }

    /// <summary>Fills the «Упоминания» tab from the last analysis session.</summary>
    private void LoadMentions()
    {
        Mentions.Clear();
        SelectedMention = null;
        var card = Selected;
        var session = _main.Session;
        if (card is null || session is null) return;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.Add(card.Name);
        foreach (var a in card.Aliases) names.Add(a);

        foreach (var file in session.Files)
        {
            var hits = file.Mentions.Where(m => names.Contains(m.Entity)).ToList();
            if (hits.Count == 0) continue;
            foreach (var m in hits.Take(30))
                Mentions.Add(new MentionRowVM(file.FullPath, m.Entity, m.Context, m.Line, hits)
                {
                    Mention = m, // R1-19: бейдж достоверности конкретной цитаты
                });
        }
        OnPropertyChanged(nameof(MentionsCount));
    }

    public string MentionsCount => $"Упоминаний в последнем анализе: {Mentions.Count}";

    private void LoadFieldRows()
    {
        FieldRows.Clear();
        var card = Selected;
        if (card is null) return;
        var schema = Doc.FindType(card.TypeId)?.Fields.OrderBy(f => f.Order) ?? Enumerable.Empty<FieldSchema>();
        foreach (var f in schema)
        {
            card.Fields.TryGetValue(f.Id, out var value);
            FieldRows.Add(FieldRowVM.Create(f, value, card, Doc, () => { card.ModifiedAt = DateTime.Now; Save(); }));
        }
    }

    private void LoadLinks()
    {
        LinkRows.Clear();
        var card = Selected;
        if (card is null) return;

        foreach (var l in Doc.Links.Where(l => l.FromId == card.Id))
        {
            var target = Doc.FindCard(l.ToId);
            if (target is null) continue;
            LinkRows.Add(new LinkRowVM(l.FromId, card.Name, target.Id, target.Name, l.RelationId,
                Doc.FindRelation(l.RelationId)?.ForwardLabel ?? l.RelationId, l.Note, Forward: true));
        }
        foreach (var l in Doc.Links.Where(l => l.ToId == card.Id))
        {
            var source = Doc.FindCard(l.FromId);
            if (source is null) continue;
            LinkRows.Add(new LinkRowVM(l.FromId, source.Name, l.ToId, card.Name, l.RelationId,
                Doc.FindRelation(l.RelationId)?.ReverseLabel ?? l.RelationId, l.Note, Forward: false));
        }
    }

    private void CommitDraft()
    {
        var card = Selected;
        if (card is null) return;
        if (string.IsNullOrWhiteSpace(EditName)) return;

        var changes = new List<string>();
        if (card.Name != EditName.Trim()) { changes.Add($"имя «{card.Name}»→«{EditName.Trim()}»"); card.Name = EditName.Trim(); }
        var canon = string.IsNullOrWhiteSpace(EditCanonical) ? card.Name : EditCanonical.Trim();
        if (card.Canonical != canon) { changes.Add("канон"); card.Canonical = canon; }

        var aliases = EditAliases.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Prepend(card.Name).Append(canon).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!card.Aliases.SequenceEqual(aliases, StringComparer.OrdinalIgnoreCase))
        {
            changes.Add($"алиасов {card.Aliases.Count}→{aliases.Count}");
            card.Aliases.Clear();
            card.Aliases.AddRange(aliases);
        }

        if (card.Group != EditGroup.Trim()) { changes.Add("группа"); card.Group = EditGroup.Trim(); }

        var tags = EditTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!card.Tags.SequenceEqual(tags, StringComparer.OrdinalIgnoreCase))
        {
            changes.Add("теги");
            card.Tags.Clear();
            card.Tags.AddRange(tags);
        }

        if (card.Description != EditDescription) { changes.Add("описание"); card.Description = EditDescription; }
        if (card.ColorMark != EditColor) { changes.Add("цвет"); card.ColorMark = EditColor; }
        var img = string.IsNullOrWhiteSpace(EditImage) ? null : EditImage;
        if (card.ImagePath != img) { changes.Add("изображение"); card.ImagePath = img; }

        // Dynamic rows write back immediately on edit, but flush here too.
        foreach (var row in FieldRows) row.Flush();

        card.ModifiedAt = DateTime.Now;
        IsDirty = false;
        OnPropertyChanged(nameof(HasSelectionAndDirty));

        if (changes.Count > 0)
        {
            Record("правка", card.Name, string.Join(", ", changes));
            Save();
            RebuildIndex.Execute(null);
        }

        // Полная перестройка списка: замена элемента самим собой не перегенерирует
        // контейнер и сбрасывает выделение через двусторонний биндинг.
        ApplyFilters();
        Selected = card;
    }

    // ─────────────────────── persistence ───────────────────────

    private void Save()
    {
        UserCodexStore.Save(Doc);
    }

    private void Record(string action, string entity, string details)
    {
        _changeLog.Record(action, entity, details);
        Log.Info("codex", $"{action}: {entity} — {details}");
    }

    // ─────────────────────── catalog rebuild ───────────────────────

    public async Task RebuildCatalogAsync()
    {
        var definitions = CodexSearchAdapter.ToDefinitions(Doc).ToList();
        await Task.Run(() => _main.Catalog = new EntityCatalog(definitions, generateVariants: true));
        var total = _main.Catalog.VariantsByEntity.Values.Sum(v => v.Count);
        _main.Dashboard.Log($"⟳ Индекс пересобран: карточек {definitions.Count}, вариантов {total} (морфология включена)");
    }
}

// ═══════════════════════ row VMs ═══════════════════════

/// <summary>One link line in the card profile (forward or reverse).</summary>
public sealed record LinkRowVM(
    string FromId, string FromName, string ToId, string ToName, string RelationId,
    string Label, string Note, bool Forward)
{
    public string Display => Forward
        ? $"→ {ToName}   ({Label})"
        : $"← {FromName}   ({Label}, обратная)";
}

/// <summary>One mention quote inside the card profile.</summary>
public sealed record MentionRowVM(string FilePath, string Entity, string Context, int Line,
    IReadOnlyList<Core.Analysis.Mention> AllMentions)
{
    /// <summary>R1-19: конкретное упоминание строки — источник бейджа достоверности.</summary>
    public required Core.Analysis.Mention Mention { get; init; }

    public string Header
    {
        get
        {
            // R1-19: живой статус достоверности — суффикс пуст для точных совпадений.
            var suffix = ConfidenceBadge.From(Mention).ToSuffix();
            return $"{Path.GetFileNameWithoutExtension(FilePath)} · строка {Line}{suffix}";
        }
    }
}

/// <summary>Base for dynamic schema-field editors.</summary>
public abstract class FieldRowVM : ObservableObject
{
    public FieldSchema Schema { get; }
    protected readonly Card Card;
    protected readonly Action Changed;
    protected readonly CodexDocument DocRef;

    protected FieldRowVM(FieldSchema schema, Card card, CodexDocument doc, Action changed)
    {
        Schema = schema;
        Card = card;
        DocRef = doc;
        Changed = changed;
    }

    public string Label => Schema.Name + (Schema.Required ? " *" : "");
    public abstract string DisplayValue { get; }

    protected void Store(FieldValue v)
    {
        Card.Fields[Schema.Id] = v;
        Card.ModifiedAt = DateTime.Now;
        Changed();
    }

    protected string? CardNameById(string? id) =>
        id is null ? null : DocRef.FindCard(id)?.Name;

    public abstract void Flush();

    public static FieldRowVM Create(FieldSchema schema, FieldValue? value, Card card,
        CodexDocument doc, Action changed)
    {
        return schema.Type switch
        {
            FieldType.MultiLine => new MultiLineFieldRowVM(schema, value, card, doc, changed),
            FieldType.Number => new NumberFieldRowVM(schema, value, card, doc, changed),
            FieldType.Date => new DateFieldRowVM(schema, value, card, doc, changed),
            FieldType.Flag => new FlagFieldRowVM(schema, value, card, doc, changed),
            FieldType.Choice => new ChoiceFieldRowVM(schema, value, card, doc, changed),
            FieldType.Link => new LinkFieldRowVM(schema, value, card, doc, changed),
            FieldType.List => new ListFieldRowVM(schema, value, card, doc, changed),
            _ => new TextFieldRowVM(schema, value, card, doc, changed),
        };
    }
}

public sealed class TextFieldRowVM(FieldSchema s, FieldValue? v, Card c, CodexDocument d, Action ch)
    : FieldRowVM(s, c, d, ch)
{
    private string _value = v?.Text ?? "";
    public string Value { get => _value; set { if (Set(ref _value, value)) Store(FieldValue.FromText(value)); } }
    public override string DisplayValue => _value;
    public override void Flush() { /* stored on write */ }
}

public sealed class MultiLineFieldRowVM(FieldSchema s, FieldValue? v, Card c, CodexDocument d, Action ch)
    : FieldRowVM(s, c, d, ch)
{
    private string _value = v?.Text ?? "";
    public string Value { get => _value; set { if (Set(ref _value, value)) Store(FieldValue.FromText(value)); } }
    public override string DisplayValue => _value.Length > 80 ? _value[..80] + "…" : _value;
    public override void Flush() { /* stored on write */ }
}

public sealed class NumberFieldRowVM(FieldSchema s, FieldValue? v, Card c, CodexDocument d, Action ch)
    : FieldRowVM(s, c, d, ch)
{
    private string _value = v?.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            if (double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var n))
                Store(FieldValue.FromNumber(n));
            else if (value.Trim().Length == 0)
                Store(FieldValue.Empty);
        }
    }

    public override string DisplayValue => _value;
    public override void Flush() { /* stored on write */ }
}

public sealed class DateFieldRowVM(FieldSchema s, FieldValue? v, Card c, CodexDocument d, Action ch)
    : FieldRowVM(s, c, d, ch)
{
    private DateTime? _value = v?.Date;

    public DateTime? Value
    {
        get => _value;
        set { if (Set(ref _value, value)) Store(value is null ? FieldValue.Empty : FieldValue.FromDate(value.Value)); }
    }

    public override string DisplayValue => _value?.ToString("dd.MM.yyyy") ?? "";
    public override void Flush() { /* stored on write */ }
}

public sealed class FlagFieldRowVM(FieldSchema s, FieldValue? v, Card c, CodexDocument d, Action ch)
    : FieldRowVM(s, c, d, ch)
{
    private bool _value = v?.Flag ?? false;

    public bool Value
    {
        get => _value;
        set { if (Set(ref _value, value)) Store(FieldValue.FromFlag(value)); }
    }

    public override string DisplayValue => _value ? "да" : "нет";
    public override void Flush() { /* stored on write */ }
}

public sealed class ChoiceFieldRowVM(FieldSchema s, FieldValue? v, Card c, CodexDocument d, Action ch)
    : FieldRowVM(s, c, d, ch)
{
    public IReadOnlyList<string> Choices => Schema.Choices;

    private string? _value = v?.Choice;

    public string? Value
    {
        get => _value;
        set { if (Set(ref _value, value)) Store(value is null ? FieldValue.Empty : FieldValue.FromChoice(value)); }
    }

    public override string DisplayValue => _value ?? "";
    public override void Flush() { /* stored on write */ }
}

public sealed class LinkFieldRowVM : FieldRowVM
{
    public IReadOnlyList<Card> Candidates => DocRef.Cards.Where(x => x.Id != Card.Id).ToList();

    private Card? _selected;

    public LinkFieldRowVM(FieldSchema s, FieldValue? v, Card c, CodexDocument d, Action ch)
        : base(s, c, d, ch)
    {
        _selected = v?.LinkCardId is null ? null : DocRef.FindCard(v.LinkCardId);
    }

    public Card? SelectedCard
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Store(value is null ? FieldValue.Empty : FieldValue.FromLink(value.Id, value.Name));
        }
    }

    public override string DisplayValue => "→ " + (_selected?.Name ?? "");
    public override void Flush() { /* stored on write */ }
}

public sealed class ListFieldRowVM(FieldSchema s, FieldValue? v, Card c, CodexDocument d, Action ch)
    : FieldRowVM(s, c, d, ch)
{
    private string _value = v?.Items is null ? "" : string.Join(", ", v.Items);

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            var items = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Store(items.Count == 0 ? FieldValue.Empty : FieldValue.FromList(items));
        }
    }

    public override string DisplayValue => _value;
    public override void Flush() { /* stored on write */ }
}
