using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Logging;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>Results browser: files → mentions with highlighted contexts.</summary>
public sealed class ResultsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, string> _hashByPath = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<FileResult> Files { get; } = [];
    public ObservableCollection<Mention> Mentions { get; } = [];

    /// <summary>R1-6: плитки heat map корпуса.</summary>
    public ObservableCollection<HeatTile> HeatTiles { get; } = [];
    public bool HasHeatTiles => HeatTiles.Count > 0;

    private FileResult? _selectedFile;
    public FileResult? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (Set(ref _selectedFile, value))
            {
                Mentions.Clear();
                foreach (var m in value?.Mentions ?? []) Mentions.Add(m);
            }
        }
    }

    private Mention? _selectedMention;
    public Mention? SelectedMention { get => _selectedMention; set => Set(ref _selectedMention, value); }

    private string _search = "";
    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            RefreshFileFilter();
            ReloadSelectedFileMentions();
        }
    }

    private bool _onlyWithResults = true;
    public bool OnlyWithResults
    {
        get => _onlyWithResults;
        set { if (Set(ref _onlyWithResults, value)) RefreshFileFilter(); }
    }

    // ─── R2-21: изоляция кликом ───
    // Клик по имени файла → показывается только этот файл; хлебная крошка снимает.

    private FileResult? _isolatedFile;
    /// <summary>Изолированный файл (null — фильтр снят).</summary>
    public FileResult? IsolatedFile
    {
        get => _isolatedFile;
        private set
        {
            if (!Set(ref _isolatedFile, value)) return;
            OnPropertyChanged(nameof(IsFileIsolated));
            OnPropertyChanged(nameof(IsolationBreadcrumb));
            RefreshFileFilter();
        }
    }
    public bool IsFileIsolated => IsolatedFile is not null;
    public string IsolationBreadcrumb => IsolatedFile is null ? "" : $"Только файл: {IsolatedFile.FileName}";

    public RelayCommand IsolateFileCommand { get; }
    public RelayCommand ClearIsolationCommand { get; }

    // ─── R2-13: вердикты пользователя ───

    public RelayCommand ConfirmMentionCommand { get; }
    public RelayCommand RejectMentionCommand { get; }
    public RelayCommand UncertainMentionCommand { get; }

    /// <summary>Режим фильтра упоминаний: все / только точные / только fuzzy / по теме.</summary>
    public IReadOnlyList<string> FilterModes { get; } = ["Все", "Точные", "Fuzzy", "Тема…"];

    private string _selectedMode = "Все";
    public string SelectedMode
    {
        get => _selectedMode;
        set { if (Set(ref _selectedMode, value)) ReloadSelectedFileMentions(); }
    }

    public ICollectionView FilesView { get; }

    /// <summary>Empty State: анализ не запускался или не дал файлов.</summary>
    public bool IsDataEmpty => Files.Count == 0;

    public RelayCommand CopyContext { get; }
    public RelayCommand OpenFullText { get; }
    public event Action<string, IReadOnlyList<Mention>>? FullTextRequested;

    public ResultsViewModel(MainViewModel main)
    {
        _main = main;
        FilesView = System.Windows.Data.CollectionViewSource.GetDefaultView(Files);
        CopyContext = new RelayCommand(_ =>
        {
            if (SelectedMention is { } m)
                Clipboard.SetText($"{m.Entity} ({m.Variant}) L{m.Line}\n{m.Context}");
        }, _ => SelectedMention is not null);
        OpenFullText = new RelayCommand(_ =>
        {
            if (SelectedFile is { } f)
                FullTextRequested?.Invoke(f.FullPath, f.Mentions);
        }, _ => SelectedFile is not null);

        IsolateFileCommand = new RelayCommand(p =>
        {
            if (p is FileResult f) IsolatedFile = f;
        }, p => p is FileResult);
        ClearIsolationCommand = new RelayCommand(_ => IsolatedFile = null);

        ConfirmMentionCommand = new RelayCommand(
            p => SaveVerdict(p, VerdictKind.Confirmed), CanSaveVerdict);
        RejectMentionCommand = new RelayCommand(
            p => SaveVerdict(p, VerdictKind.Rejected), CanSaveVerdict);
        UncertainMentionCommand = new RelayCommand(
            p => SaveVerdict(p, VerdictKind.Uncertain), CanSaveVerdict);
    }

    internal void LoadSession(AnalysisSession session)
    {
        Files.Clear();
        Mentions.Clear();
        IsolatedFile = null; // новая сессия — старые фильтры не имеют смысла
        foreach (var f in session.Files) Files.Add(f);
        SelectedFile = session.Files.FirstOrDefault(f => f.Mentions.Count > 0) ?? session.Files.FirstOrDefault();
        RefreshFileFilter();
        OnPropertyChanged(nameof(IsDataEmpty));

        // R1-6: heat map корпуса из той же сессии.
        HeatTiles.Clear();
        foreach (var t in HeatmapModel.Build(session, Services.BranchResolver.ResolveIndex))
            HeatTiles.Add(t);
        OnPropertyChanged(nameof(HasHeatTiles));
    }

    private void RefreshFileFilter()
    {
        if (FilesView is null) return;
        var q = Search?.Trim() ?? "";

        FilesView.Filter = o =>
        {
            if (o is not FileResult f) return false;
            // R2-21: изоляция кликом главнее остальных условий.
            if (IsolatedFile is not null) return ReferenceEquals(f, IsolatedFile);
            if (OnlyWithResults && f.Mentions.Count == 0 && string.IsNullOrEmpty(q)) return false;
            if (q.Length == 0) return true;
            return f.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || f.Mentions.Any(m =>
                    m.Context.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.Variant.Contains(q, StringComparison.OrdinalIgnoreCase));
        };
    }

    private void ReloadSelectedFileMentions()
    {
        var file = _selectedFile;
        Mentions.Clear();
        var q = Search?.Trim() ?? "";
        foreach (var m in file?.Mentions ?? [])
        {
            if (SelectedMode == "Точные" && m.IsFuzzy) continue;
            if (SelectedMode == "Fuzzy" && !m.IsFuzzy) continue;
            if (q.Length > 0 &&
                !m.Context.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !m.Variant.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            Mentions.Add(m);
        }
    }

    // ─── R2-13 helpers ───

    private bool CanSaveVerdict(object? p) => p is Mention && SelectedFile is not null;

    private void SaveVerdict(object? p, VerdictKind kind)
    {
        if (p is not Mention { } m || SelectedFile is not { } file) return;
        try
        {
            // Ключ идентичности: хеш контента файла + офсет находки. Для записей
            // из старого кэша (офсет не сохранялся) деградируем до номера строки.
            var offset = m.Offset > 0 ? m.Offset : m.Line;
            VerdictStore.Default.Append(new VerdictRecord(
                DateTimeOffset.Now,
                GetFileHash(file.FullPath),
                offset,
                m.Variant,
                m.Entity,
                kind));
            Log.Info("verdicts", "{0}: {1} ({2}) строка {3}", kind, m.Entity, m.Variant, m.Line);
        }
        catch (Exception ex)
        {
            Log.Error("verdicts", $"Не удалось сохранить вердикт: {ex.Message}");
        }
    }

    /// <summary>SHA-256 содержимого файла с кэшем на время жизни страницы.</summary>
    private string GetFileHash(string fullPath)
    {
        if (_hashByPath.TryGetValue(fullPath, out var hash)) return hash;
        try
        {
            using var fs = File.OpenRead(fullPath);
            hash = Convert.ToHexString(SHA256.HashData(fs));
        }
        catch
        {
            hash = "unreadable:" + fullPath; // файл исчез — ключ всё равно стабильный
        }
        _hashByPath[fullPath] = hash;
        return hash;
    }
}
