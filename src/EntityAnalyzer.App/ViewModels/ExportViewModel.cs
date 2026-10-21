using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.App.Services;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Microsoft.Win32;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>Export page: formats, output folder, quality funnel.</summary>
public sealed class ExportViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppSettings _settings;

    public ObservableCollection<FormatItem> Formats { get; } =
    [
        new("txt", "Текстовый отчёт"),
        new("json", "JSON-данные"),
        new("csv", "CSV-таблица"),
        new("html", "HTML-отчёт"),
        new("md", "Markdown-отчёт")
    ];

    public bool HasSession => _main.Session is not null;

    // QA-UX #9: информация о последнем автоматическом экспорте.
    private string _lastAutoExportInfo = "";
    public string LastAutoExportInfo { get => _lastAutoExportInfo; private set => Set(ref _lastAutoExportInfo, value); }
    private bool _hasLastAutoExport;
    public bool HasLastAutoExport { get => _hasLastAutoExport; private set => Set(ref _hasLastAutoExport, value); }

    // Протокол проверки: пишется вместе с отчётами, только если есть вердикты.
    private string _reviewReportInfo = "";
    public string ReviewReportInfo { get => _reviewReportInfo; private set => Set(ref _reviewReportInfo, value); }

    /// <summary>Сводка проверки для подсказки на странице экспорта.</summary>
    internal void RefreshReviewInfo() => RefreshReviewInfo(_main.Session);

    private void RefreshReviewInfo(AnalysisSession? session)
    {
        ReviewReportInfo = session is null
            ? "Протокол проверки появится после анализа и первых вердиктов."
            : ReviewNavigator.Summarize(session.Files) is { } s
                ? s.Reviewed == 0
                    ? $"Проверка: 0 из {s.Total:N0} цитат — протокол review_report не создаётся, пока нет вердиктов."
                    : $"Проверка: {s.Reviewed:N0} из {s.Total:N0} цитат — вместе с отчётами будут записаны review_report.md и review_report.csv."
                : "";
    }

    /// <summary>Вызывается MainViewModel при навигации на страницу.</summary>
    public void RefreshAutoExportInfo()
    {
        RefreshReviewInfo(_main.Session);
        var s = _main.Session;
        if (s is null || string.IsNullOrWhiteSpace(_settings.OutputDir)) { HasLastAutoExport = false; return; }
        try
        {
            var report = Path.Combine(_settings.OutputDir, "analysis_report.txt");
            if (!File.Exists(report)) { HasLastAutoExport = false; return; }
            var written = File.GetLastWriteTime(report);
            LastAutoExportInfo =
                $"Последний анализ уже записал отчёты: {written:dd.MM HH:mm}, папка {_settings.OutputDir}. " +
                "Экспорт ниже перезапишет их выбранными форматами.";
            HasLastAutoExport = true;
        }
        catch { HasLastAutoExport = false; }
    }

    private string _outputDir = "output";
    public string OutputDir
    {
        get => _outputDir;
        set { Set(ref _outputDir, value); _settings.OutputDir = value; SettingsService.Save(_settings); }
    }

    // Quality funnel bars (percent of raw)
    private double _barRaw = 100;
    public double BarRaw { get => _barRaw; private set => Set(ref _barRaw, value); }
    private double _barFinal;
    public double BarFinal { get => _barFinal; private set => Set(ref _barFinal, value); }
    private string _funnelText = "Запустите анализ, чтобы увидеть статистику";
    public string FunnelText { get => _funnelText; private set => Set(ref _funnelText, value); }

    public RelayCommand BrowseOutput { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand OpenFolder { get; }

    public ExportViewModel(MainViewModel main, AppSettings settings)
    {
        _main = main;
        _settings = settings;
        OutputDir = settings.OutputDir;

        // Сначала восстановить флаги из настроек, ПОТОМ подписываться на сохранение —
        // иначе первый чекбокс перезаписывает settings.Formats частичным списком.
        foreach (var f in Formats) f.IsEnabled = settings.Formats.Contains(f.Key);
        foreach (var f in Formats)
            f.PropertyChanged += (_, _) =>
            {
                settings.Formats = Formats.Where(x => x.IsEnabled).Select(x => x.Key).ToList();
                SettingsService.Save(settings);
            };

        BrowseOutput = new RelayCommand(_ =>
        {
            var dlg = new OpenFolderDialog { Title = "Папка для отчётов" };
            if (dlg.ShowDialog() == true) OutputDir = dlg.FolderName;
        });

        ExportCommand = new RelayCommand(_ => DoExport(), _ => HasSession);
        OpenFolder = new RelayCommand(_ =>
        {
            Directory.CreateDirectory(OutputDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetFullPath(OutputDir)}\"") { UseShellExecute = true });
        });
    }

    internal void RefreshFrom(AnalysisSession session)
    {
        OnPropertyChanged(nameof(HasSession));
        RefreshReviewInfo(session);
        var raw = Math.Max(1, session.Quality.RawMatches);
        BarRaw = 100;
        BarFinal = session.Quality.FinalMentions * 100.0 / raw;
        FunnelText = $"Сырых совпадений: {session.Quality.RawMatches:N0}  →  итоговых цитат: {session.Quality.FinalMentions:N0}   " +
                     $"(границы: −{session.Quality.BoundaryRejected + session.Quality.BrokenWordRejected}, " +
                     $"перечни: −{session.Quality.ListingRejected}, титула: −{session.Quality.TitleRejected})";
    }

    private void DoExport()
    {
        if (_main.Session is null) return;
        var chosen = Formats.Where(f => f.IsEnabled).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (chosen.Count == 0) return;

        try
        {
            ReportExporter.ExportAll(_main.Session, Path.GetFullPath(OutputDir), chosen,
                perEntityFolders: true, codex: _main.CodexVm.Doc);
            _main.StatusText = $"Экспорт завершён → {Path.GetFullPath(OutputDir)}";
            OpenFolder.Execute(null);
        }
        catch (Exception ex)
        {
            _main.StatusText = $"Ошибка экспорта: {ex.Message}";
        }
    }
}

public sealed class FormatItem : ObservableObject
{
    public string Key { get; }
    public string Title { get; }

    private bool _isEnabled;
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

    public FormatItem(string key, string title) { Key = key; Title = title; }
}
