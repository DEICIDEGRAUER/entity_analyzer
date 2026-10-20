using System.Collections.ObjectModel;
using System.IO;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.App.Services;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>Узел дерева отчётов: папка или файл.</summary>
public sealed class ReportNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsFolder { get; init; }
    public ObservableCollection<ReportNode>? Children { get; init; }

    public override string ToString() => Name;

    public static ReportNode ForFolder(string path) => new()
    {
        Name = Path.GetFileName(path),
        FullPath = path,
        IsFolder = true,
        Children = [],
    };

    public static ReportNode ForFile(string path) => new()
    {
        Name = Path.GetFileName(path),
        FullPath = path,
        IsFolder = false,
    };
}

/// <summary>Просмотр отчётов целиком, повторяя структуру папок выходного каталога.</summary>
public sealed class ReportsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public ObservableCollection<ReportNode> Roots { get; } = [];

    /// <summary>Item 42: отфильтрованное дерево (Ctrl+F / поле «Фильтр отчётов»).</summary>
    public ObservableCollection<ReportNode> RootsView { get; } = [];

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) ApplyFilter(); }
    }

    /// <summary>Item 42: включён ли фильтр (для пустого состояния).</summary>
    public bool IsFilterEmpty => _filter.Trim().Length > 0 && RootsView.Count == 0 && Roots.Count > 0;

    private ReportNode? _selected;
    public ReportNode? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) Load();
        }
    }

    private string _previewText = "";
    public string PreviewText { get => _previewText; private set => Set(ref _previewText, value); }

    private bool _isHtml;
    public bool IsHtml { get => _isHtml; private set { if (Set(ref _isHtml, value)) OnPropertyChanged(nameof(TextVisible)); } }

    public bool TextVisible => !IsHtml && Selected is not null && !Selected.IsFolder;

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public string RootPath =>
        Path.IsPathRooted(_settings.OutputDir)
            ? _settings.OutputDir
            : Path.Combine(AppContext.BaseDirectory, _settings.OutputDir);

    /// <summary>Empty State: отчётов ещё нет.</summary>
    public bool IsDataEmpty => Roots.Count == 0;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenExternallyCommand { get; }

    public ReportsViewModel(AppSettings settings)
    {
        _settings = settings;
        RefreshCommand = new RelayCommand(_ => Refresh());
        OpenExternallyCommand = new RelayCommand(
            _ => { if (Selected is { IsFolder: false }) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Selected.FullPath}\""); },
            _ => Selected is { IsFolder: false });
    }

    /// <summary>Перечитать дерево с диска (после анализа или по кнопке).</summary>
    public void Refresh()
    {
        Roots.Clear();

        var root = RootPath;
        if (!Directory.Exists(root))
        {
            Status = $"Каталог отчётов пока не создан: {root}";
            ApplyFilter();
            OnPropertyChanged(nameof(IsDataEmpty));
            return;
        }

        foreach (var child in LoadDir(root))
            Roots.Add(child);

        Status = root;
        ApplyFilter();
        OnPropertyChanged(nameof(IsDataEmpty));
    }

    /// <summary>
    /// Item 42: перестроить отфильтрованное дерево. Совпавшие по имени папки
    /// показываются целиком; файлы — по имени; пустые ветки скрываются.
    /// </summary>
    private void ApplyFilter()
    {
        RootsView.Clear();
        var q = _filter.Trim();
        if (q.Length == 0)
        {
            foreach (var r in Roots) RootsView.Add(r);
        }
        else
        {
            foreach (var r in Roots)
                if (FilterNode(r, q) is { } match)
                    RootsView.Add(match);
        }
        OnPropertyChanged(nameof(IsFilterEmpty));
    }

    private static ReportNode? FilterNode(ReportNode node, string q)
    {
        var nameHit = node.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
        if (!node.IsFolder)
            return nameHit ? node : null;

        if (nameHit)
            return new ReportNode
            {
                Name = node.Name,
                FullPath = node.FullPath,
                IsFolder = true,
                Children = new ObservableCollection<ReportNode>(node.Children ?? []),
            };

        var children = (node.Children ?? [])
            .Select(c => FilterNode(c, q))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
        if (children.Count == 0) return null;

        return new ReportNode
        {
            Name = node.Name,
            FullPath = node.FullPath,
            IsFolder = true,
            Children = new ObservableCollection<ReportNode>(children),
        };
    }

    private IEnumerable<ReportNode> LoadDir(string dir)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var folder = ReportNode.ForFolder(sub);
            foreach (var child in LoadDir(sub))
                folder.Children!.Add(child);
            yield return folder;
        }

        foreach (var file in Directory.EnumerateFiles(dir)
                     .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            yield return ReportNode.ForFile(file);
        }
    }

    private void Load()
    {
        OnPropertyChanged(nameof(TextVisible));
        OnPropertyChanged(nameof(OpenExternallyCommand));
        if (Selected is null || Selected.IsFolder)
        {
            PreviewText = "";
            IsHtml = false;
            return;
        }

        try
        {
            var ext = Path.GetExtension(Selected.FullPath).ToLowerInvariant();
            if (ext == ".html")
            {
                IsHtml = true;
                PreviewText = "";
                OnPropertyChanged(nameof(HtmlUri));
                return;
            }

            IsHtml = false;
            using var reader = new StreamReader(Selected.FullPath);
            var buffer = new char[512 * 1024];
            var read = reader.Read(buffer, 0, buffer.Length);
            PreviewText = new string(buffer, 0, read);
            Status = $"{Selected.Name} · {new FileInfo(Selected.FullPath).Length:N0} байт";
        }
        catch (Exception ex)
        {
            PreviewText = $"Не удалось прочитать файл: {ex.Message}";
            IsHtml = false;
        }
    }

    /// <summary>URI для встроенного браузера (html-отчёты).</summary>
    public Uri? HtmlUri =>
        Selected is { IsFolder: false } && IsHtml
            ? new Uri(Selected.FullPath)
            : null;
}
