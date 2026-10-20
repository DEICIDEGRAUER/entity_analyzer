using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.App.Services;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Chronology;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Genealogy;
using EntityAnalyzer.Core.Geography;
using EntityAnalyzer.Core.Hierarchy;
using Microsoft.Win32;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>
/// Группа 4: страница «Мир» — визуализация пространственно-временных и
/// генеалогических данных. Четыре вкладки: семейное древо (GEDCOM и связи
/// кодекса), иерархия, автособираемая хронология и география с картой.
/// </summary>
public sealed class WorldViewModel : ObservableObject
{
    public GenealogyViewModel Tree { get; }
    public HierarchyViewModel Hierarchy { get; }
    public ChronologyViewModel Chronology { get; }
    public GeographyViewModel Geography { get; }

    public WorldViewModel(MainViewModel main, AppSettings settings)
    {
        Tree = new GenealogyViewModel(main, settings);
        Hierarchy = new HierarchyViewModel(main);
        Chronology = new ChronologyViewModel(main);
        Geography = new GeographyViewModel(main, settings);
    }

    /// <summary>Лёгкое обновление при входе на страницу: деревья из кодекса пересобираются.</summary>
    public void Refresh()
    {
        Tree.Refresh();
        Hierarchy.Refresh();
        Chronology.Refresh();
        Geography.Refresh();
    }
}

// ═══════════════════════════ ДРЕВО ═══════════════════════════

/// <summary>Строка списка людей (поиск и выбор узла).</summary>
public sealed class PersonRow(FamilyTreeNode node)
{
    public FamilyTreeNode Node { get; } = node;
    public string Name => Node.Name;
    public string Subtitle => Node.Subtitle;
    public string ConfidenceLabel => Node.Confidence == FamilyConfidence.Unknown
        ? ""
        : FamilyConfidenceScale.Label(Node.Confidence);
    public string Display
    {
        get
        {
            var text = Subtitle.Length == 0 ? Name : $"{Name} · {Subtitle}";
            return ConfidenceLabel.Length == 0 ? text : $"{text} · {ConfidenceLabel}";
        }
    }

    public override string ToString() => Display;
}

/// <summary>Строка аудита древа: проблема, пояснение и первая персона для перехода.</summary>
public sealed class FamilyIssueRow(FamilyTreeIssue issue)
{
    public FamilyTreeIssue Issue { get; } = issue;
    public string Title => Issue.Title;
    public string Detail => Issue.Detail;
    public string? TargetId => Issue.NodeIds.FirstOrDefault();
    public string Display => $"{Title}. {Detail}";
}

/// <summary>
/// Item 5/5.1: семейное древо. Источники — кодекс (связи «породил» и
/// «родитель») или файл GEDCOM. GEDCOM сразу превращается в читаемый текст;
/// дерево раскладывается по поколениям и выгружается в интерактивный HTML.
/// </summary>
public sealed class GenealogyViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppSettings _settings;

    private FamilyTree? _gedcomTree;
    private string _gedcomText = "";
    private FamilyTree? _tree;
    private FamilyTree? _fullTree;
    private string _canvasNotice = "";
    private readonly List<int> _componentIds = [];
    private int _componentIndex;
    private const int MaxCanvasNodes = 900;
    private TreeLayoutResult? _layout;
    private FamilyTreeNode? _selected;
    private string _gedcomPath = "";
    private string _readableText = "";
    private string _search = "";
    private string _status = "Древо собирается из связей кодекса: «породил», «родитель», «усыновляет», «опекун», «крёстный», «партнёр».";
    private int _sourceIndex;
    private bool _showHypotheses = true;
    private bool _focusActive;
    private string _focusId = "";
    private int _focusDepth = 2;

    public GenealogyViewModel(MainViewModel main, AppSettings settings)
    {
        _main = main;
        _settings = settings;
        LoadGedcomCommand = new AsyncRelayCommand(LoadGedcomAsync);
        RebuildCommand = new RelayCommand(Refresh);
        ExportHtmlCommand = new RelayCommand(_ => ExportHtml(), _ => Tree is not null);
        ExportTextCommand = new RelayCommand(_ => ExportText(), _ => Tree is not null);
        FocusCommand = new RelayCommand(_ => FocusSelected(), _ => SelectedNode is not null);
        ClearFocusCommand = new RelayCommand(_ => ClearFocus(), _ => FocusActive);
        SelectIssueCommand = new RelayCommand(param =>
        {
            if (param is FamilyIssueRow row && row.TargetId is { Length: > 0 } id)
                SelectNode(id);
        });
        Refresh();
    }

    public string[] Sources { get; } = ["Кодекс мира", "Файл GEDCOM"];

    public int SourceIndex
    {
        get => _sourceIndex;
        set
        {
            if (!Set(ref _sourceIndex, Math.Clamp(value, 0, 1))) return;
            Refresh();
        }
    }

    public FamilyTree? Tree
    {
        get => _tree;
        private set
        {
            _tree = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTree));
            OnPropertyChanged(nameof(TreeEmpty));
        }
    }

    public TreeLayoutResult? Layout
    {
        get => _layout;
        private set
        {
            _layout = value;
            OnPropertyChanged();
        }
    }

    public bool HasTree => Tree is not null && Tree.Nodes.Count > 0;

    public bool TreeEmpty => !HasTree;

    public string CanvasNotice
    {
        get => _canvasNotice;
        private set
        {
            if (!Set(ref _canvasNotice, value)) return;
            OnPropertyChanged(nameof(HasCanvasNotice));
        }
    }

    public bool HasCanvasNotice => CanvasNotice.Length > 0;

    public ObservableCollection<string> ComponentOptions { get; } = [];

    public bool HasMultipleComponents => ComponentOptions.Count > 1;

    public int ComponentIndex
    {
        get => _componentIndex;
        set
        {
            if (!Set(ref _componentIndex, Math.Clamp(value, 0, Math.Max(0, ComponentOptions.Count - 1)))) return;
            ApplyView();
            SelectedNode = null;
            TreeChanged?.Invoke();
            var total = _fullTree?.Nodes.Count ?? 0;
            var shown = Tree?.Nodes.Count ?? 0;
            var branch = ComponentIndex >= 0 && ComponentIndex < ComponentOptions.Count
                ? ComponentOptions[ComponentIndex]
                : "ветвь не выбрана";
            Status = Layout is null && Tree is not null
                ? CanvasNotice
                : shown == 0
                    ? "Древо пусто."
                    : $"Показано: {shown} чел. из {total} · {branch}.";
        }
    }

    public FamilyTreeNode? SelectedNode
    {
        get => _selected;
        private set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedSubtitle));
            OnPropertyChanged(nameof(SelectedDetail));
            SelectionChanged?.Invoke();
        }
    }

    public string SelectedTitle => SelectedNode?.Name ?? "";

    public string SelectedSubtitle => SelectedNode is null
        ? ""
        : (SelectedNode.Subtitle.Length > 0 ? SelectedNode.Subtitle + " · " : "") +
          FamilyTree.GenerationLabel(SelectedNode.Generation) +
          (SelectedNode.Confidence == FamilyConfidence.Unknown
              ? ""
              : $" · {FamilyConfidenceScale.Label(SelectedNode.Confidence)}");

    public string SelectedDetail
    {
        get
        {
            if (SelectedNode is null) return "Выберите человека на древе или в списке.";
            var text = SelectedNode.Detail;
            if (SourceIndex == 1 || Tree is null) return text;

            var byId = Tree.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            var rows = new List<string>();
            foreach (var link in Tree.ParentLinks.Where(l =>
                         l.ParentId == SelectedNode.Id || l.ChildId == SelectedNode.Id))
            {
                var parent = byId.TryGetValue(link.ParentId, out var p) ? p.Name : link.ParentId;
                var child = byId.TryGetValue(link.ChildId, out var c) ? c.Name : link.ChildId;
                rows.Add($"{parent} — {link.DisplayLabel} → {child}" + Evidence(link.Confidence));
            }
            foreach (var pair in Tree.SpouseLinks.Where(p =>
                         p.A == SelectedNode.Id || p.B == SelectedNode.Id))
            {
                var a = byId.TryGetValue(pair.A, out var left) ? left.Name : pair.A;
                var b = byId.TryGetValue(pair.B, out var right) ? right.Name : pair.B;
                rows.Add($"{a} — {pair.DisplayLabel} — {b}" + Evidence(pair.Confidence));
            }

            if (rows.Count == 0) return text;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("СВЯЗИ");
            foreach (var row in rows) sb.AppendLine("• " + row);
            if (text.Trim().Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine(text.Trim());
            }
            return sb.ToString().TrimEnd();

            static string Evidence(FamilyConfidence confidence) =>
                confidence == FamilyConfidence.Unknown
                    ? ""
                    : $" ({FamilyConfidenceScale.Label(confidence)})";
        }
    }

    /// <summary>Наблюдения аудита: дубли имён, сироты, несколько наборов родителей, гипотезы.</summary>
    public ObservableCollection<FamilyIssueRow> Issues { get; } = [];

    public bool HasIssues => Issues.Count > 0;

    public string IssuesHeader => Issues.Count == 0
        ? "Проверка древа · проблем не найдено"
        : $"Требует внимания · {Issues.Count}";

    /// <summary>Показывать предполагаемые связи. Выключение не меняет данные.</summary>
    public bool ShowHypotheses
    {
        get => _showHypotheses;
        set
        {
            if (!Set(ref _showHypotheses, value)) return;
            ApplyView();
            TreeChanged?.Invoke();
            Status = value
                ? "Слой гипотез включён."
                : $"Слой гипотез скрыт (связей: {_fullTree?.HypothesisCount ?? 0}).";
        }
    }

    public string HypothesesLabel => (_fullTree?.HypothesisCount ?? 0) > 0
        ? $"Гипотезы · {_fullTree!.HypothesisCount}"
        : "Гипотезы";

    /// <summary>Глубина эго-сети в режиме фокуса.</summary>
    public int FocusDepth
    {
        get => _focusDepth;
        set
        {
            if (!Set(ref _focusDepth, Math.Clamp(value, 1, 4))) return;
            if (!FocusActive) return;
            ApplyView();
            TreeChanged?.Invoke();
        }
    }

    public int[] FocusDepths { get; } = [1, 2, 3, 4];

    public bool FocusActive
    {
        get => _focusActive;
        private set
        {
            if (!Set(ref _focusActive, value)) return;
            OnPropertyChanged(nameof(FocusHint));
        }
    }

    public string FocusHint => FocusActive
        ? $"Фокус: {Tree?.Nodes.FirstOrDefault(n => n.Id == _focusId)?.Name ?? "персона"} · {FocusDepth} ур."
        : "Фокус: выберите персону и нажмите «Фокус».";

    public ObservableCollection<PersonRow> People { get; } = [];

    private List<PersonRow> _allPeople = [];

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            ApplyFilter();
        }
    }

    public string GedcomPath
    {
        get => _gedcomPath;
        private set => Set(ref _gedcomPath, value);
    }

    public string ReadableText
    {
        get => _readableText;
        private set
        {
            _readableText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReadableText));
        }
    }

    public bool HasReadableText => ReadableText.Length > 0;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public AsyncRelayCommand LoadGedcomCommand { get; }
    public RelayCommand RebuildCommand { get; }
    public RelayCommand ExportHtmlCommand { get; }
    public RelayCommand ExportTextCommand { get; }
    public RelayCommand FocusCommand { get; }
    public RelayCommand ClearFocusCommand { get; }
    public RelayCommand SelectIssueCommand { get; }

    /// <summary>Запрошен пересчёт канвы (подписка в code-behind).</summary>
    public event Action? TreeChanged;

    /// <summary>Сменился выбранный узел — подсветить без полной перерисовки.</summary>
    public event Action? SelectionChanged;

    /// <summary>Пересобрать текущий источник (вызывается при входе на страницу).</summary>
    public void Refresh()
    {
        if (SourceIndex == 1)
        {
            if (_gedcomTree is not null)
            {
                ApplyTree(_gedcomTree, _gedcomText);
                return;
            }

            _fullTree = null;
            Tree = null;
            Layout = null;
            CanvasNotice = "GEDCOM не загружен — нажмите «Загрузить GEDCOM…».";
            _allPeople = [];
            ApplyFilter();
            ClearIssues();
            FocusActive = false;
            _focusId = "";
            SelectedNode = null;
            TreeChanged?.Invoke();
            Status = "GEDCOM не загружен — нажмите «Загрузить GEDCOM…».";
            return;
        }

        try
        {
            var built = FamilyTreeBuilder.FromCodex(_main.Codex);
            ApplyTree(built, CodexNarrative(built));
            var spouse = built.SpouseLinks.Count;
            var typed = built.ParentLinks.Count(l => l.Kind != FamilyRelationKind.Biological);
            Status = built.Nodes.Count == 0
                ? "В кодексе нет родственных связей — древо пусто. Добавьте «родитель», «усыновляет», «опекун» или «партнёр» в карточках кодекса."
                : $"Древо кодекса: {built.Nodes.Count} карточек · связей: {built.ParentLinks.Count + spouse} " +
                  $"({typed} особых) · поколений: {built.GenerationCount} · ветвей: {Math.Max(0, ComponentOptions.Count - 1)}" +
                  (built.HypothesisCount > 0 ? $" · гипотез: {built.HypothesisCount}" : "") +
                  (HasIssues ? $" · внимание: {Issues.Count}" : "") + ".";
        }
        catch (Exception ex)
        {
            Status = "Не удалось собрать древо кодекса: " + ex.Message;
        }
    }

    private async Task LoadGedcomAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Открыть файл GEDCOM",
            Filter = "GEDCOM|*.ged;*.gedcom|Все файлы|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        Status = "Читаю GEDCOM…";
        try
        {
            var path = dlg.FileName;
            var (doc, tree, text) = await Task.Run(() =>
            {
                var parsed = GedcomParser.ParseFile(path);
                var built = FamilyTreeBuilder.FromGedcom(parsed);
                var readable = GedcomNarrator.ToReadableText(parsed);
                return (parsed, built, readable);
            });

            _gedcomTree = tree;
            _gedcomText = text;
            GedcomPath = path;
            _sourceIndex = 1;
            OnPropertyChanged(nameof(SourceIndex));
            ApplyTree(tree, text);
            var preview = text.Length > 200_000
                ? text[..200_000] + Environment.NewLine + Environment.NewLine +
                  $"… Предпросмотр обрезан ({text.Length:N0} символов). Полный текст — в экспорте."
                : text;
            ReadableText = preview;
            var typed = tree.ParentLinks.Count(l => l.Kind != FamilyRelationKind.Biological);
            var aliases = doc.Persons.Sum(p => p.AlternateNames.Count);
            Status = $"GEDCOM «{Path.GetFileName(path)}»: {doc.Persons.Count} человек · {doc.Families.Count} семей · " +
                     $"поколений: {tree.GenerationCount}" +
                     (typed > 0 ? $" · особых связей: {typed}" : "") +
                     (aliases > 0 ? $" · альтернативных имён: {aliases}" : "") +
                     (doc.Warnings.Count > 0 ? $" · замечаний: {doc.Warnings.Count}" : "") + ".";
        }
        catch (Exception ex)
        {
            Status = "Ошибка чтения GEDCOM: " + ex.Message;
        }
    }

    private void ApplyTree(FamilyTree tree, string readableText)
    {
        _fullTree = tree.Nodes.Count == 0 ? null : tree;
        if (_focusId.Length > 0 && (tree.Nodes.Count == 0 || !tree.Nodes.Any(n => n.Id == _focusId)))
        {
            FocusActive = false;
            _focusId = "";
        }
        ReadableText = readableText;
        RebuildComponentOptions();
        ApplyView();
        SelectedNode = null;
        TreeChanged?.Invoke();
    }

    /// <summary>
    /// Список ветвей (компонентов связности). Для больших разрозненных GEDCOM
    /// по умолчанию открывается крупнейшая ветвь — иначе канва рисовала бы
    /// тысячи карточек в одну строку.
    /// </summary>
    private void RebuildComponentOptions()
    {
        ComponentOptions.Clear();
        _componentIds.Clear();
        if (_fullTree is null)
        {
            _componentIndex = 0;
            OnPropertyChanged(nameof(ComponentIndex));
            OnPropertyChanged(nameof(HasMultipleComponents));
            return;
        }

        var groups = _fullTree.Nodes
            .GroupBy(n => n.Component)
            .Select(g => new
            {
                Component = g.Key,
                Count = g.Count(),
                MinGeneration = g.Min(n => n.Generation),
                Order = g.Min(n => n.Order),
            })
            .OrderBy(g => g.MinGeneration)
            .ThenBy(g => g.Order)
            .ThenBy(g => g.Component)
            .ToList();

        ComponentOptions.Add($"Все ветви · {_fullTree.Nodes.Count}");
        foreach (var group in groups)
        {
            _componentIds.Add(group.Component);
            ComponentOptions.Add($"Ветвь {_componentIds.Count} · {group.Count} чел.");
        }

        var index = 0;
        if (_fullTree.Nodes.Count > MaxCanvasNodes && groups.Count > 1)
        {
            var largest = groups.OrderByDescending(g => g.Count).First();
            index = _componentIds.IndexOf(largest.Component) + 1;
        }
        _componentIndex = index;
        OnPropertyChanged(nameof(ComponentIndex));
        OnPropertyChanged(nameof(HasMultipleComponents));
    }

    private void ApplyView()
    {
        var selectedId = SelectedNode?.Id;
        FamilyTree? visible = null;
        if (_fullTree is not null)
        {
            visible = ComponentIndex > 0 && ComponentIndex - 1 < _componentIds.Count
                ? _fullTree.Subset(_componentIds[ComponentIndex - 1])
                : _fullTree;

            if (FocusActive && _focusId.Length > 0)
            {
                if (visible.Nodes.Any(n => n.Id == _focusId))
                {
                    visible = visible.SubsetAround(_focusId, FocusDepth);
                }
                else
                {
                    FocusActive = false;
                    _focusId = "";
                }
            }

            if (!ShowHypotheses)
                visible = visible.WithoutHypotheses();
        }

        var tooLarge = visible is not null && visible.Nodes.Count > MaxCanvasNodes;
        Tree = visible is null || visible.Nodes.Count == 0 ? null : visible;
        Layout = Tree is null || tooLarge ? null : FamilyTreeLayout.Calculate(Tree);
        CanvasNotice = Tree is null
            ? "Древо пусто. Загрузите GEDCOM или добавьте в кодекс родственные связи."
            : tooLarge
                ? $"В выбранной ветви {visible!.Nodes.Count} человек — больше {MaxCanvasNodes}. " +
                  "Выберите ветвь в списке или выгрузите HTML-древо: браузер справится с большим объёмом."
                : "";
        _allPeople = [.. (Tree?.Nodes ?? []).Select(n => new PersonRow(n))];
        ApplyFilter();
        RefreshIssues();
        OnPropertyChanged(nameof(HypothesesLabel));
        OnPropertyChanged(nameof(FocusHint));

        SelectedNode = selectedId is null
            ? null
            : Tree?.Nodes.FirstOrDefault(n => n.Id == selectedId);
        OnPropertyChanged(nameof(SelectedSubtitle));
        OnPropertyChanged(nameof(SelectedDetail));
    }

    private void ApplyFilter()
    {
        People.Clear();
        var query = Search.Trim();
        foreach (var row in _allPeople)
        {
            if (query.Length > 0 &&
                !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !row.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !row.ConfidenceLabel.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            People.Add(row);
        }
    }

    private void RefreshIssues()
    {
        Issues.Clear();
        if (Tree is not null)
            foreach (var issue in FamilyTreeAudit.Analyze(Tree))
                Issues.Add(new FamilyIssueRow(issue));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(IssuesHeader));
    }

    private void ClearIssues()
    {
        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(IssuesHeader));
    }

    private void FocusSelected()
    {
        if (SelectedNode is null) return;
        var name = SelectedNode.Name;
        _focusId = SelectedNode.Id;
        FocusActive = true;
        ApplyView();
        TreeChanged?.Invoke();
        Status = $"Фокус на «{name}»: {Tree?.Nodes.Count ?? 0} персон в радиусе {FocusDepth}.";
    }

    private void ClearFocus()
    {
        if (!FocusActive && _focusId.Length == 0) return;
        FocusActive = false;
        _focusId = "";
        ApplyView();
        TreeChanged?.Invoke();
        Status = "Фокус снят — показана выбранная ветвь целиком.";
    }

    public void SelectNode(string id)
    {
        var node = Tree?.Nodes.FirstOrDefault(n => n.Id == id);
        if (node is null && FocusActive && _fullTree is not null)
        {
            FocusActive = false;
            _focusId = "";
            ApplyView();
            node = Tree?.Nodes.FirstOrDefault(n => n.Id == id);
        }
        if (node is not null) SelectedNode = node;
    }

    public void SelectNode(PersonRow? row) => SelectedNode = row?.Node;

    /// <summary>Читаемое описание древа кодекса (для GEDCOM текст строится нарратором).</summary>
    private static string CodexNarrative(FamilyTree tree)
    {
        if (tree.Nodes.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ДРЕВО КОДЕКСА — ЧИТАЕМОЕ ОПИСАНИЕ");
        sb.AppendLine($"Карточек: {tree.Nodes.Count} · родительских связей: {tree.ParentLinks.Count} · " +
                      $"партнёрских: {tree.SpouseLinks.Count} · поколений: {tree.GenerationCount}" +
                      (tree.HypothesisCount > 0 ? $" · гипотез: {tree.HypothesisCount}" : "") + ".");
        sb.AppendLine("Тип связи указан у ребра; «гипотеза»/«противоречиво» — статус уверенности.");
        sb.AppendLine();
        var byId = tree.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var parents = new HashSet<string>(tree.ParentLinks.Select(l => l.ChildId), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Append(string id, int depth, string incoming = "")
        {
            if (!visited.Add(id) || depth > 64) return;
            var node = byId[id];
            var title = node.Subtitle.Length > 0 ? $"{node.Name} ({node.Subtitle})" : node.Name;
            if (incoming.Length > 0) title += $" — {incoming}";
            sb.Append(new string(' ', depth * 4)).Append("• ").AppendLine(title);
            foreach (var link in tree.ParentLinks.Where(l => l.ParentId == id))
                Append(link.ChildId, depth + 1,
                    link.DisplayLabel + Evidence(link.Confidence));
        }

        foreach (var node in tree.Nodes)
            if (!parents.Contains(node.Id))
                Append(node.Id, 0);
        foreach (var node in tree.Nodes)
            Append(node.Id, 0);

        if (tree.SpouseLinks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("ПАРТНЁРСТВА");
            foreach (var pair in tree.SpouseLinks)
            {
                var a = byId.TryGetValue(pair.A, out var left) ? left.Name : pair.A;
                var b = byId.TryGetValue(pair.B, out var right) ? right.Name : pair.B;
                sb.AppendLine($"• {a} — {b}: {pair.DisplayLabel}{Evidence(pair.Confidence)}");
            }
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;

        static string Evidence(FamilyConfidence confidence) => confidence == FamilyConfidence.Unknown
            ? ""
            : $" ({FamilyConfidenceScale.Label(confidence)})";
    }

    /// <summary>
    /// Полный читаемый текст для экспорта: у GEDCOM в интерфейсе хранится
    /// обрезанный предпросмотр, а <c>_gedcomText</c> — весь текст нарратора.
    /// </summary>
    private string FullReadableText =>
        SourceIndex == 1 && _gedcomText.Length > 0 ? _gedcomText : ReadableText;

    internal string OutputDirectory()
    {
        var dir = Path.IsPathRooted(_settings.OutputDir)
            ? _settings.OutputDir
            : Path.Combine(AppContext.BaseDirectory, _settings.OutputDir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void ExportHtml()
    {
        if (Tree is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Экспорт древа в HTML",
            Filter = "HTML|*.html",
            FileName = "family_tree.html",
            InitialDirectory = OutputDirectory(),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var title = SourceIndex == 1 ? "Семейное древо — GEDCOM" : "Генеалогическое древо кодекса";
            File.WriteAllText(dlg.FileName, FamilyTreeHtml.Render(Tree, FullReadableText, title));
            Status = "HTML-древо сохранено: " + dlg.FileName;
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Ошибка экспорта HTML: " + ex.Message;
        }
    }

    private void ExportText()
    {
        if (Tree is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Экспорт читаемого текста",
            Filter = "Markdown|*.md|Текст|*.txt",
            FileName = "family_tree.md",
            InitialDirectory = OutputDirectory(),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, FullReadableText);
            Status = "Текст сохранён: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            Status = "Ошибка экспорта текста: " + ex.Message;
        }
    }
}

// ═══════════════════════════ ИЕРАРХИЯ ═══════════════════════════

/// <summary>Флажок связи, участвующей в построении иерархии.</summary>
public sealed class HierarchyRelationChoice : ObservableObject
{
    private bool _isChecked = true;

    public HierarchyRelationChoice(string id, string label)
    {
        Id = id;
        Label = label;
    }

    public string Id { get; }
    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }
}

/// <summary>Обёртка узла иерархии для TreeView.</summary>
public sealed class HierarchyNodeVm(HierarchyNode node)
{
    public HierarchyNode Node { get; } = node;
    public string Title => Node.Title;
    public string Subtitle => Node.Subtitle;
    public string Kind => Node.Kind;
    public string Marker => Node.Marker;
    public string Color => Node.Color;
    public string Detail => Node.Detail;
    public bool IsReference => Node.IsReference;
    public string? ReferenceNote => Node.IsReference ? "↻ уже показано выше" : null;
    public ObservableCollection<HierarchyNodeVm> Children { get; } =
        new(node.Children.Select(c => new HierarchyNodeVm(c)));
}

/// <summary>
/// «Древо Иерархии»: автосборка из связей кодекса (служит/находится/
/// породил/родитель/владеет), выбор связей, поиск, экспорт Markdown и JSON.
/// </summary>
public sealed class HierarchyViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private HierarchyNodeVm? _selected;
    private string _search = "";
    private string _status = "Иерархия собирается из связей кодекса.";

    public HierarchyViewModel(MainViewModel main)
    {
        _main = main;
        RebuildCommand = new RelayCommand(Refresh);
        ExportMarkdownCommand = new RelayCommand(_ => ExportMarkdown(), _ => Roots.Count > 0);
        ExportJsonCommand = new RelayCommand(_ => ExportJson(), _ => Roots.Count > 0);
        BuildChoices();
        Refresh();
    }

    public ObservableCollection<HierarchyRelationChoice> RelationChoices { get; } = [];
    public ObservableCollection<HierarchyNodeVm> Roots { get; } = [];
    public ObservableCollection<HierarchyNodeVm> SearchResults { get; } = [];

    public HierarchyNodeVm? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedSubtitle));
            OnPropertyChanged(nameof(SelectedDetail));
        }
    }

    public bool HasSelection => Selected is not null;
    public string SelectedTitle => Selected?.Title ?? "";
    public string SelectedSubtitle => Selected is null
        ? ""
        : string.Join(" · ", new[]
        {
            Selected.Kind,
            Selected.Subtitle,
            Selected.IsReference ? "повторная ссылка" : "",
        }.Where(s => s.Length > 0));

    public string SelectedDetail => Selected?.Detail ?? "Выберите узел древа иерархии.";

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            ApplySearch();
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public RelayCommand RebuildCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }
    public RelayCommand ExportJsonCommand { get; }

    private void BuildChoices()
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["serves"] = "служит",
            ["located"] = "находится в",
            ["spawn"] = "породил",
            ["parent"] = "родитель",
            ["owns"] = "владеет",
        };
        RelationChoices.Clear();
        foreach (var id in HierarchyTreeBuilder.DefaultRelations)
        {
            var relation = _main.Codex.Relations.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var label = relation?.ForwardLabel is { Length: > 0 } l ? l : labels.GetValueOrDefault(id, id);
            RelationChoices.Add(new HierarchyRelationChoice(id, label));
        }
    }

    public void Refresh()
    {
        try
        {
            if (RelationChoices.Count == 0) BuildChoices();
            var ids = RelationChoices.Where(c => c.IsChecked).Select(c => c.Id).ToList();
            var tree = HierarchyTreeBuilder.Build(_main.Codex, ids);
            Roots.Clear();
            foreach (var root in tree.Roots)
                Roots.Add(new HierarchyNodeVm(root));
            ApplySearch();
            Selected = null;
            Status = tree.Cards == 0
                ? "В кодексе нет связей, пригодных для иерархии. Включите связи и добавьте их в карточках."
                : $"Иерархия: карточек — {tree.Cards}, узлов — {tree.Placements}, ссылок на повтор — {tree.References}, корней — {tree.Roots.Count}.";
        }
        catch (Exception ex)
        {
            Status = "Ошибка построения иерархии: " + ex.Message;
        }
    }

    private void ApplySearch()
    {
        SearchResults.Clear();
        var query = Search.Trim();
        if (query.Length == 0) return;
        foreach (var root in Roots)
        foreach (var node in Flatten(root))
        {
            if (node.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                node.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                node.Kind.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                SearchResults.Add(node);
                if (SearchResults.Count >= 100) return;
            }
        }
    }

    private static IEnumerable<HierarchyNodeVm> Flatten(HierarchyNodeVm root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var nested in Flatten(child))
            yield return nested;
    }

    public void Select(HierarchyNodeVm? node) => Selected = node;

    private HierarchyTree? CurrentTree()
    {
        var ids = RelationChoices.Where(c => c.IsChecked).Select(c => c.Id).ToList();
        var tree = HierarchyTreeBuilder.Build(_main.Codex, ids);
        return tree.Roots.Count == 0 ? null : tree;
    }

    private void ExportMarkdown()
    {
        var tree = CurrentTree();
        if (tree is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Экспорт иерархии в Markdown",
            Filter = "Markdown|*.md",
            FileName = "hierarchy.md",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, HierarchyTreeBuilder.ToMarkdown(tree));
            Status = "Markdown сохранён: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            Status = "Ошибка экспорта: " + ex.Message;
        }
    }

    private void ExportJson()
    {
        var tree = CurrentTree();
        if (tree is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Экспорт иерархии в JSON",
            Filter = "JSON|*.json",
            FileName = "hierarchy.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, HierarchyTreeBuilder.ToJson(tree));
            Status = "JSON сохранён: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            Status = "Ошибка экспорта: " + ex.Message;
        }
    }
}

// ═══════════════════════════ ХРОНОЛОГИЯ ═══════════════════════════

/// <summary>Строка шкалы времени.</summary>
public sealed class ChronologyRow(ChronologyEntry entry)
{
    public ChronologyEntry Entry { get; } = entry;
    public string Label => Entry.Label;
    public string KindLabel => Entry.Kind switch
    {
        ChronoKind.YearsAgo => "давность",
        ChronoKind.Year => "год",
        ChronoKind.Era => "эра/эпоха",
        _ => "маркер",
    };

    public string SortLabel => Entry.Kind switch
    {
        ChronoKind.Year when Entry.SortKey is { } year => WorldChronology.CenturyLabel((int)year),
        ChronoKind.YearsAgo => "до нашей эпохи",
        _ => "без датировки",
    };

    public string Source => $"{Entry.File}:{Entry.Line}";
    public string Snippet => Entry.Snippet;
    public string Entities => Entry.Entities.Count == 0 ? "" : "сущности: " + string.Join(", ", Entry.Entities);
    public string Path => Entry.Path;
    public string FileName => Entry.File;
    public int Line => Entry.Line;
}

/// <summary>
/// Item 6: автособираемая хронология мира Мифов. Собирается по паттернам из
/// текстов последнего прогона: годы, «N лет назад», эры/эпохи, маркеры.
/// </summary>
public sealed class ChronologyViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _status = "Хронология собирается из текстов последнего прогона.";
    private string _search = "";
    private int _kindIndex;
    private List<ChronologyRow> _all = [];

    public ChronologyViewModel(MainViewModel main)
    {
        _main = main;
        BuildCommand = new AsyncRelayCommand(BuildAsync);
        OpenFileCommand = new RelayCommand(p => OpenFile(p as ChronologyRow), p => p is ChronologyRow);
        ExportMarkdownCommand = new RelayCommand(_ => ExportMarkdown(), _ => Rows.Count > 0);
    }

    public ObservableCollection<ChronologyRow> Rows { get; } = [];

    public string[] KindOptions { get; } = ["Все виды", "Годы", "Давность", "Эры и эпохи", "Маркеры"];

    public int KindIndex
    {
        get => _kindIndex;
        set
        {
            if (Set(ref _kindIndex, value)) ApplyFilter();
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value)) ApplyFilter();
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public AsyncRelayCommand BuildCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }

    public void Refresh()
    {
        if (_main.Session is null && Rows.Count == 0)
            Status = "Нет прогона — сначала выполните анализ, затем соберите хронологию.";
    }

    private async Task BuildAsync()
    {
        var session = _main.Session;
        if (session is null)
        {
            Status = "Нет данных прогона — сначала выполните анализ.";
            return;
        }

        Status = "Читаю тексты корпуса…";
        try
        {
            var names = session.Files
                .SelectMany(f => f.Mentions)
                .Select(m => m.Entity)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var texts = await Task.Run(() => CorpusViewModel.ReadCorpus(session));
            Status = "Ищу годы, давность, эры и временные маркеры…";
            var entries = await Task.Run(() => WorldChronology.Build(texts, names));
            _all = [.. entries.Select(e => new ChronologyRow(e))];
            ApplyFilter();
            var years = entries.Count(e => e.Kind == ChronoKind.Year);
            var ago = entries.Count(e => e.Kind == ChronoKind.YearsAgo);
            var eras = entries.Count(e => e.Kind == ChronoKind.Era);
            var markers = entries.Count(e => e.Kind == ChronoKind.Marker);
            Status = entries.Count == 0
                ? $"В {texts.Count} файлах не найдено датировок."
                : $"Хронология: {entries.Count} записей из {texts.Count} файлов — годы {years}, давность {ago}, эры {eras}, маркеры {markers}.";
        }
        catch (Exception ex)
        {
            Status = "Ошибка сборки хронологии: " + ex.Message;
        }
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        var kind = KindIndex switch
        {
            1 => ChronoKind.Year,
            2 => ChronoKind.YearsAgo,
            3 => ChronoKind.Era,
            4 => ChronoKind.Marker,
            _ => (ChronoKind?)null,
        };
        var query = Search.Trim();
        foreach (var row in _all)
        {
            if (kind is not null && row.Entry.Kind != kind) continue;
            if (query.Length > 0 &&
                !row.Label.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !row.Snippet.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !row.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            Rows.Add(row);
        }
    }

    private void OpenFile(ChronologyRow? row)
    {
        if (row is null) return;
        try
        {
            if (File.Exists(row.Path))
            {
                Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
                Status = $"Открыт файл: {row.FileName}, строка {row.Line}";
            }
            else
            {
                Status = "Файл не найден: " + row.Path;
            }
        }
        catch (Exception ex)
        {
            Status = "Не удалось открыть файл: " + ex.Message;
        }
    }

    private void ExportMarkdown()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Экспорт хронологии",
            Filter = "Markdown|*.md",
            FileName = "mythos_chronology.md",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Хронология мира Мифов");
            sb.AppendLine();
            sb.AppendLine(Status);
            sb.AppendLine();
            foreach (var row in Rows)
            {
                sb.AppendLine($"## {row.Label} · {row.KindLabel}");
                if (row.SortLabel.Length > 0) sb.AppendLine($"*{row.SortLabel}*");
                sb.AppendLine();
                sb.AppendLine($"Источник: {row.Source}");
                if (row.Entities.Length > 0) sb.AppendLine(row.Entities);
                sb.AppendLine();
                sb.AppendLine("> " + row.Snippet.Replace("\n", " "));
                sb.AppendLine();
            }
            File.WriteAllText(dlg.FileName, sb.ToString());
            Status = "Хронология сохранена: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            Status = "Ошибка экспорта: " + ex.Message;
        }
    }
}

// ═══════════════════════════ ГЕОГРАФИЯ ═══════════════════════════

/// <summary>Цитата места в списке подробностей.</summary>
public sealed class PlaceQuoteRow(PlaceQuote quote)
{
    public PlaceQuote Quote { get; } = quote;
    public string Source => $"{Quote.File}:{Quote.Line}";
    public string Snippet => Quote.Snippet;
    public string Path => Quote.Path;
    public string FileName => Quote.File;
    public int Line => Quote.Line;
}

/// <summary>Строка географии: место, класс, координаты и цитаты.</summary>
public sealed class PlaceRow : ObservableObject
{
    private GeoPoint? _point;
    private string _note;

    public PlaceRow(PlaceEntry entry)
    {
        Entry = entry;
        _point = entry.Point;
        _note = entry.GazetteerNote;
        Quotes = new ObservableCollection<PlaceQuoteRow>(entry.Quotes.Select(q => new PlaceQuoteRow(q)));
    }

    public PlaceEntry Entry { get; }
    public string Name => Entry.Name;
    public PlaceClass Class => Entry.Class;
    public string ClassLabel => PlaceClasses.Label(Entry.Class);
    public string KindLabel => Entry.KindLabel;
    public bool Fictional => Entry.Fictional;
    public int Mentions => Entry.Mentions;
    public int FileCount => Entry.FileCount;
    public string VariantsText => Entry.Variants.Count <= 1 ? "" : "написания: " + string.Join(", ", Entry.Variants);
    public ObservableCollection<PlaceQuoteRow> Quotes { get; }

    public GeoPoint? Point
    {
        get => _point;
        set
        {
            if (!Set(ref _point, value)) return;
            OnPropertyChanged(nameof(CoordsText));
            OnPropertyChanged(nameof(HasPoint));
            OnPropertyChanged(nameof(PointLabel));
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            if (!Set(ref _note, value)) return;
            OnPropertyChanged(nameof(NoteText));
        }
    }

    public bool HasPoint => Point is not null;
    public string CoordsText => Point?.Text ?? "координаты не заданы";
    public string PointLabel => Point is null ? "нет координат" : "координаты есть";
    public string NoteText => Note.Length == 0 ? "" : Note;
    public string SourcesText => $"упоминаний: {Mentions} · файлов: {FileCount}";

    public PlaceEntry ToEntry() => Entry with
    {
        Point = Point,
        GazetteerNote = Note,
    };
}

/// <summary>
/// Item 19: автосборка географии Мифов — места по классам, собственные имена
/// и цитаты, координаты из внутренней базы (реальные и вымышленные), карта
/// Leaflet в HTML и опциональное уточнение координат через Nominatim.
/// </summary>
public sealed class GeographyViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppSettings _settings;
    private string _status = "География собирается из текстов последнего прогона.";
    private string _search = "";
    private int _classIndex;
    private bool _onlyWithCoordinates;
    private PlaceRow? _selected;
    private List<PlaceRow> _all = [];

    public GeographyViewModel(MainViewModel main, AppSettings settings)
    {
        _main = main;
        _settings = settings;
        BuildCommand = new AsyncRelayCommand(BuildAsync);
        ExportMapCommand = new RelayCommand(_ => ExportMap(), _ => _all.Count > 0);
        EnrichCommand = new AsyncRelayCommand(EnrichSelectedAsync,
            () => Selected is { HasPoint: false });
        OpenQuoteCommand = new RelayCommand(p => OpenQuote(p as PlaceQuoteRow), p => p is PlaceQuoteRow);
    }

    public ObservableCollection<PlaceRow> Places { get; } = [];
    public ObservableCollection<string> ClassOptions { get; } = ["Все классы"];

    public int ClassIndex
    {
        get => _classIndex;
        set
        {
            if (Set(ref _classIndex, value)) ApplyFilter();
        }
    }

    public bool OnlyWithCoordinates
    {
        get => _onlyWithCoordinates;
        set
        {
            if (Set(ref _onlyWithCoordinates, value)) ApplyFilter();
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value)) ApplyFilter();
        }
    }

    public PlaceRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedKind));
            OnPropertyChanged(nameof(SelectedCoords));
            OnPropertyChanged(nameof(SelectedNote));
            OnPropertyChanged(nameof(SelectedVariants));
            OnPropertyChanged(nameof(SelectedSources));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelection => Selected is not null;
    public string SelectedTitle => Selected?.Name ?? "";
    public string SelectedKind => Selected is null
        ? ""
        : $"{Selected.ClassLabel} · {(Selected.Fictional ? "вымышленное" : "реальное")} · {Selected.SourcesText}";
    public string SelectedCoords => Selected?.CoordsText ?? "";
    public string SelectedNote => Selected?.NoteText ?? "";
    public string SelectedVariants => Selected?.VariantsText ?? "";
    public string SelectedSources => Selected?.SourcesText ?? "";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public AsyncRelayCommand BuildCommand { get; }
    public RelayCommand ExportMapCommand { get; }
    public AsyncRelayCommand EnrichCommand { get; }
    public RelayCommand OpenQuoteCommand { get; }

    public void Refresh()
    {
        if (_main.Session is null && Places.Count == 0)
            Status = "Нет прогона — сначала выполните анализ, затем соберите географию.";
    }

    private async Task BuildAsync()
    {
        var session = _main.Session;
        if (session is null)
        {
            Status = "Нет данных прогона — сначала выполните анализ.";
            return;
        }

        Status = "Читаю тексты корпуса…";
        try
        {
            var texts = await Task.Run(() => CorpusViewModel.ReadCorpus(session));
            Status = "Ищу классы мест, имена и цитаты…";
            var entries = await Task.Run(() =>
            {
                var heads = PlaceHeads.Load();
                var gazetteer = PlaceGazetteer.Load();
                var mentions = PlaceExtractor.Extract(texts, heads, gazetteer);
                return PlaceExtractor.Aggregate(mentions, gazetteer);
            });

            _all = [.. entries.Select(e => new PlaceRow(e))];
            RebuildClassOptions();
            ApplyFilter();
            Selected = null;
            var placed = entries.Count(e => e.Point is not null);
            Status = entries.Count == 0
                ? $"В {texts.Count} файлах места не найдены. Дополните словари классов в user_place_heads.json."
                : $"География: {entries.Count} мест из {texts.Count} файлов · с координатами: {placed} · без координат: {entries.Count - placed}.";
        }
        catch (Exception ex)
        {
            Status = "Ошибка сборки географии: " + ex.Message;
        }
    }

    private void RebuildClassOptions()
    {
        var selected = ClassIndex <= 0 ? null : ClassOptions[Math.Min(ClassIndex, ClassOptions.Count - 1)];
        ClassOptions.Clear();
        ClassOptions.Add("Все классы");
        foreach (var label in _all
                     .GroupBy(p => p.ClassLabel)
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key, StringComparer.Ordinal)
                     .Select(g => g.Key))
            ClassOptions.Add(label);
        var index = selected is null ? 0 : ClassOptions.IndexOf(selected);
        _classIndex = Math.Max(0, index);
        OnPropertyChanged(nameof(ClassIndex));
    }

    private void ApplyFilter()
    {
        Places.Clear();
        var classFilter = ClassIndex > 0 && ClassIndex < ClassOptions.Count ? ClassOptions[ClassIndex] : null;
        var query = Search.Trim();
        foreach (var row in _all)
        {
            if (classFilter is not null && !row.ClassLabel.Equals(classFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (OnlyWithCoordinates && !row.HasPoint) continue;
            if (query.Length > 0 &&
                !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !row.Entry.Variants.Any(v => v.Contains(query, StringComparison.OrdinalIgnoreCase)))
                continue;
            Places.Add(row);
        }
    }

    private void OpenQuote(PlaceQuoteRow? quote)
    {
        if (quote is null) return;
        try
        {
            if (File.Exists(quote.Path))
            {
                Process.Start(new ProcessStartInfo(quote.Path) { UseShellExecute = true });
                Status = $"Открыт файл: {quote.FileName}, строка {quote.Line}";
            }
            else
            {
                Status = "Файл не найден: " + quote.Path;
            }
        }
        catch (Exception ex)
        {
            Status = "Не удалось открыть файл: " + ex.Message;
        }
    }

    private void ExportMap()
    {
        if (_all.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Title = "Экспорт карты в HTML",
            Filter = "HTML|*.html",
            FileName = "mythos_map.html",
            InitialDirectory = OutputDirectory(),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var entries = _all.Select(p => p.ToEntry()).ToList();
            File.WriteAllText(dlg.FileName, GeographyMapHtml.Render(entries));
            Status = "Карта сохранена: " + dlg.FileName;
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Ошибка экспорта карты: " + ex.Message;
        }
    }

    private async Task EnrichSelectedAsync()
    {
        if (Selected is null || Selected.HasPoint) return;
        Status = $"Запрос координат для «{Selected.Name}» через Nominatim…";
        try
        {
            var geocoder = NominatimGeocoder.CreateDefault();
            var point = await geocoder.LookupAsync(Selected.Name);
            if (point is null)
            {
                Status = $"Nominatim не нашёл координаты для «{Selected.Name}» (ответ закэширован).";
                return;
            }
            Selected.Point = point;
            if (Selected.Note.Length == 0)
                Selected.Note = "Координаты: Nominatim / OpenStreetMap.";
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            Status = $"Координаты «{Selected.Name}»: {point.Text} (кэш: {geocoder.CachedCount}).";
        }
        catch (Exception ex)
        {
            Status = "Ошибка онлайн-геокодирования: " + ex.Message;
        }
    }

    private string OutputDirectory()
    {
        var dir = Path.IsPathRooted(_settings.OutputDir)
            ? _settings.OutputDir
            : Path.Combine(AppContext.BaseDirectory, _settings.OutputDir);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
