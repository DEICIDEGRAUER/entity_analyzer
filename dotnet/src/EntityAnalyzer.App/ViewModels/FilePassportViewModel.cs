using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Exporting;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>
/// Группа 3 (item 24): «Паспорт файла» — режим исследования одного файла.
/// Агрегирует уже существующие результаты прогона: сущности, сработавшие
/// паттерны, темы, плотность упоминаний по главам и связи с другими
/// файлами через общие сущности. Ничего не пересчитывает по корпусу.
/// </summary>
public sealed class FilePassportViewModel : ObservableObject
{
    private const int MaxEntityRows = 60;

    private readonly MainViewModel _main;
    private List<FilePassportFile> _allFiles = [];
    private FilePassport? _passport;
    private string? _pendingPath;

    public ObservableCollection<FilePassportFile> Files { get; } = [];
    public ObservableCollection<PassportEntityRow> Entities { get; } = [];
    public ObservableCollection<PassportPatternRow> Patterns { get; } = [];
    public ObservableCollection<PassportThemeRow> Themes { get; } = [];
    public ObservableCollection<PassportChapterRow> Chapters { get; } = [];
    public ObservableCollection<PassportNeighborRow> Neighbors { get; } = [];

    public RelayCommand ExportCommand { get; }
    public RelayCommand OpenInResultsCommand { get; }
    public RelayCommand OpenNeighborCommand { get; }

    public FilePassportViewModel(MainViewModel main)
    {
        _main = main;
        ExportCommand = new RelayCommand(_ => Export(), _ => _passport is { Found: true });
        OpenInResultsCommand = new RelayCommand(
            _ => OpenInResults(), _ => SelectedFile is not null);
        OpenNeighborCommand = new RelayCommand(
            p => OpenNeighbor(p as PassportNeighborRow), p => p is PassportNeighborRow);
    }

    // ─── Список файлов ───

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    private FilePassportFile? _selectedFile;
    public FilePassportFile? SelectedFile
    {
        get => _selectedFile;
        set { if (Set(ref _selectedFile, value)) BuildPassport(); }
    }

    public bool HasSession => _main.Session is not null;

    public bool IsDataEmpty => Files.Count == 0;

    public string EmptyHint => _main.Session is null
        ? "Анализ ещё не запускался — выполните анализ на «Обзоре», затем вернитесь к паспорту файла."
        : _allFiles.Count == 0
            ? "В результатах последнего анализа нет файлов."
            : "Под фильтром ничего не нашлось — измените поиск.";

    // ─── Паспорт выбранного файла ───

    public bool HasPassport => _passport is { Found: true };

    private string _title = "Выберите файл";
    public string Title { get => _title; private set => Set(ref _title, value); }

    private string _path = "";
    public string PathText { get => _path; private set => Set(ref _path, value); }

    private string _metrics = "";
    public string MetricsText { get => _metrics; private set => Set(ref _metrics, value); }

    private string _note = "";
    public string Note { get => _note; private set => Set(ref _note, value); }

    public bool HasNote => Note.Length > 0;

    private string _tags = "";
    public string TagsText { get => _tags; private set => Set(ref _tags, value); }

    /// <summary>Item 11: автотеги файла по преобладающим темам (строка для отображения).</summary>
    public bool HasTags => TagsText.Length > 0;

    private string _moreEntitiesHint = "";
    public string MoreEntitiesHint { get => _moreEntitiesHint; private set => Set(ref _moreEntitiesHint, value); }

    public bool HasMoreEntities => MoreEntitiesHint.Length > 0;

    public bool HasEntities => Entities.Count > 0;
    public bool HasPatterns => Patterns.Count > 0;
    public bool HasThemes => Themes.Count > 0;
    public bool HasChapters => Chapters.Count > 0;
    public bool HasNeighbors => Neighbors.Count > 0;

    private void NotifyPassportState()
    {
        OnPropertyChanged(nameof(HasPassport));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasMoreEntities));
        OnPropertyChanged(nameof(HasEntities));
        OnPropertyChanged(nameof(HasPatterns));
        OnPropertyChanged(nameof(HasThemes));
        OnPropertyChanged(nameof(HasChapters));
        OnPropertyChanged(nameof(HasNeighbors));
    }

    /// <summary>Перечитать список файлов (вызывается при входе в раздел).</summary>
    public void Refresh()
    {
        var previousPath = SelectedFile?.FullPath;
        _allFiles = [.. (_main.Session?.Files ?? [])
            .Select(f => new FilePassportFile(f.FileName, f.FullPath, f.Mentions.Count))
            .OrderBy(f => f.FileName, StringComparer.CurrentCulture)];

        ApplyFilter();

        if (_pendingPath is not null)
        {
            var pending = _pendingPath;
            _pendingPath = null;
            SelectFileByPath(pending);
            return;
        }

        var keep = previousPath is null
            ? null
            : Files.FirstOrDefault(f => string.Equals(f.FullPath, previousPath, StringComparison.OrdinalIgnoreCase));
        if (keep is not null)
        {
            SelectedFile = keep;
        }
        else if (SelectedFile is null && Files.Count > 0)
        {
            SelectedFile = Files[0];
        }
        else if (SelectedFile is not null && !Files.Contains(SelectedFile))
        {
            SelectedFile = Files.FirstOrDefault();
        }
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(IsDataEmpty));
        OnPropertyChanged(nameof(EmptyHint));
    }

    /// <summary>Открыть паспорт конкретного файла (переход из «Результатов»).</summary>
    internal void RequestFile(string fullPath) => _pendingPath = fullPath;

    private void SelectFileByPath(string fullPath)
    {
        var target = _allFiles.FirstOrDefault(f =>
            string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            _main.StatusText = "Файла нет в результатах последнего анализа";
            return;
        }
        if (!Files.Contains(target))
        {
            Search = ""; // снимет фильтр и пересоберёт список
        }
        SelectedFile = Files.FirstOrDefault(f =>
            string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) ?? target;
    }

    private void ApplyFilter()
    {
        var q = _search.Trim();
        Files.Clear();
        foreach (var f in _allFiles)
        {
            if (q.Length > 0 &&
                !f.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !f.FullPath.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            Files.Add(f);
        }
        OnPropertyChanged(nameof(IsDataEmpty));
        OnPropertyChanged(nameof(EmptyHint));
        CommandManager.InvalidateRequerySuggested();
    }

    private void BuildPassport()
    {
        Entities.Clear();
        Patterns.Clear();
        Themes.Clear();
        Chapters.Clear();
        Neighbors.Clear();
        MoreEntitiesHint = "";

        if (SelectedFile is null)
        {
            _passport = null;
            Title = "Выберите файл";
            PathText = "";
            MetricsText = "";
            Note = "";
            TagsText = "";
            NotifyPassportState();
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        var file = SelectedFile;
        var text = FilePassportBuilder.TryReadNormalizedText(file.FullPath);
        _passport = FilePassportBuilder.Build(_main.Session, file.FullPath, text);

        Title = _passport.Found ? _passport.FileName : file.FileName;
        PathText = file.FullPath;
        MetricsText = _passport.Found
            ? $"упоминаний: {_passport.TotalMentions} · сущностей: {_passport.EntityCount} · " +
              $"знаков: {_passport.CharCount:N0} · плотность: {_passport.DensityPer10k:0.##} на 10 тыс."
            : "";
        Note = _passport.Note;
        TagsText = _passport.Found && _passport.Tags.Count > 0
            ? string.Join(" · ", _passport.Tags)
            : "";

        if (_passport.Found)
        {
            var shown = 0;
            foreach (var e in _passport.Entities)
            {
                if (shown++ >= MaxEntityRows) break;
                Entities.Add(new PassportEntityRow(e));
            }
            var hidden = _passport.Entities.Count - Entities.Count;
            MoreEntitiesHint = hidden > 0 ? $"…и ещё {hidden} сущностей (в экспорте — все)" : "";

            foreach (var p in _passport.Patterns)
                Patterns.Add(new PassportPatternRow(p));
            foreach (var t in _passport.Themes)
                Themes.Add(new PassportThemeRow(t));

            var maxDensity = _passport.Chapters.Count == 0 ? 0 : _passport.Chapters.Max(c => c.DensityPer10k);
            foreach (var c in _passport.Chapters)
                Chapters.Add(new PassportChapterRow(c, maxDensity));

            foreach (var n in _passport.Neighbors)
                Neighbors.Add(new PassportNeighborRow(n));
        }

        NotifyPassportState();
        if (_passport.Found)
            _main.StatusText = $"Паспорт файла: {Title}";
        CommandManager.InvalidateRequerySuggested();
    }

    private void OpenInResults()
    {
        if (SelectedFile is null) return;
        if (!_main.Results.IsolateFileByPath(SelectedFile.FullPath)) return;
        _main.Navigate(_main.Results);
    }

    private void OpenNeighbor(PassportNeighborRow? row)
    {
        if (row is null) return;
        SelectFileByPath(row.FullPath);
    }

    private void Export()
    {
        if (_passport is not { Found: true } p) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт паспорта файла в Markdown",
            Filter = "Markdown|*.md",
            FileName = $"passport_{SafeName(System.IO.Path.GetFileNameWithoutExtension(p.FileName))}_{DateTime.Now:yyyyMMdd_HHmm}.md",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, FilePassportMarkdown.Build(p), System.Text.Encoding.UTF8);
            _main.StatusText = $"Паспорт экспортирован: {dlg.FileName}";
            _main.Dashboard.Log($"🧭 Паспорт файла «{p.FileName}» экспортирован в {dlg.FileName}");
        }
        catch (Exception ex)
        {
            _main.StatusText = $"Ошибка экспорта паспорта: {ex.Message}";
        }
    }

    private static string SafeName(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}

/// <summary>Строка списка файлов для выбора паспорта.</summary>
public sealed class FilePassportFile(string fileName, string fullPath, int mentions)
{
    public string FileName { get; } = fileName;
    public string FullPath { get; } = fullPath;
    public int Mentions { get; } = mentions;
    public string MentionsText => Mentions == 0 ? "упоминаний нет" : $"упоминаний: {Mentions}";
}

/// <summary>Строка таблицы сущностей паспорта.</summary>
public sealed class PassportEntityRow
{
    public PassportEntityRow(PassportEntity e)
    {
        Entity = e.Entity;
        CountText = e.Count.ToString();
        ExactFuzzyText = e.Fuzzy == 0 ? $"точных: {e.Exact}" : $"точных: {e.Exact} · fuzzy: {e.Fuzzy}";
        VariantsText = e.Variants.Count == 0 ? "—" : string.Join(" · ", e.Variants);
    }

    public string Entity { get; }
    public string CountText { get; }
    public string ExactFuzzyText { get; }
    public string VariantsText { get; }
}

/// <summary>Строка таблицы паттернов паспорта.</summary>
public sealed class PassportPatternRow(PassportPattern p)
{
    public string PatternName { get; } = p.PatternName;
    public string CountText { get; } = $"{p.Count}×";
    public string Example { get; } = p.Example;
}

/// <summary>Строка таблицы тем паспорта.</summary>
public sealed class PassportThemeRow(PassportTheme t)
{
    public string Theme { get; } = t.Theme;
    public string CountText { get; } = t.Count.ToString();
    public string ShareText { get; } = $"{t.Share:0.#}%";
    public double BarWidth { get; } = Math.Clamp(t.Share, 0, 100);
}

/// <summary>Строка плотности по главам (BarWidth нормирован к максимуму).</summary>
public sealed class PassportChapterRow
{
    public PassportChapterRow(PassportChapter c, double maxDensity)
    {
        Label = c.Label;
        MentionsText = c.Mentions == 0 ? "—" : c.Mentions.ToString();
        CharsText = c.Chars >= 1000 ? $"{c.Chars / 1000.0:0.#} тыс." : c.Chars.ToString();
        DensityText = $"{c.DensityPer10k:0.##}";
        BarWidth = maxDensity > 0 ? Math.Clamp(c.DensityPer10k / maxDensity * 140.0, 1.0, 140.0) : 0;
    }

    public string Label { get; }
    public string MentionsText { get; }
    public string CharsText { get; }
    public string DensityText { get; }
    public double BarWidth { get; }
}

/// <summary>Строка связей с другими файлами паспорта.</summary>
public sealed class PassportNeighborRow(PassportNeighbor n)
{
    public string FileName { get; } = n.FileName;
    public string FullPath { get; } = n.FullPath;
    public string SharedText { get; } = $"общих сущностей: {n.SharedEntities}";
    public string EntitiesText { get; } = n.Entities.Count == 0 ? "—" : string.Join(" · ", n.Entities);
}
