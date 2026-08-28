using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.App.Views;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.App.ViewModels;

/// <summary>
/// История прогонов: список запусков + сравнение выбранного с последним.
/// Модуль обособлен: сам владеет и чтением, и записью журнала через
/// HistoryStore.Default — снаружи нужны только данные прогона.
/// </summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly HistoryStore _store = HistoryStore.Default;

    public ObservableCollection<RunRecord> Runs { get; } = [];

    /// <summary>Empty State: журнал пока пуст.</summary>
    public bool IsDataEmpty => Runs.Count == 0;

    private RunRecord? _selected;
    public RunRecord? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                BuildDetails();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private string _details = "";
    public string Details { get => _details; private set => Set(ref _details, value); }

    private string _comparison = "";
    public string Comparison { get => _comparison; private set => Set(ref _comparison, value); }

    /// <summary>UX-тикет «база сравнения»: заголовок диффа с явной базой.</summary>
    public string DiffHeader { get => _diffHeader; private set => Set(ref _diffHeader, value); }
    private string _diffHeader = "";

    /// <summary>R1-9: строка диффа сущностей для двухколоночного вида.</summary>
    public sealed record EntityDeltaRow(string Entity, int Before, int After, int Delta)
    {
        /// <summary>Стрелка-дельта: рост ▲, падение ▼, без изменений ·.</summary>
        public string DeltaText => Delta > 0 ? $"▲ +{Delta}" : Delta < 0 ? $"▼ {Delta}" : "· 0";
    }

    /// <summary>R1-9: позиционный дифф сущностей выбранного прогона против базового.</summary>
    public ObservableCollection<EntityDeltaRow> EntityDiffs { get; } = [];

    /// <summary>Есть ли что показать в дифф-виде.</summary>
    public bool HasEntityDiffs => EntityDiffs.Count > 0;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CompareCommand { get; }
    public RelayCommand ExportReproCommand { get; }
    public RelayCommand ExportAcademicCommand { get; }
    public RelayCommand PrintAcademicCommand { get; }

    public HistoryViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        CompareCommand = new RelayCommand(_ => Compare(), _ => Selected is not null && Runs.Count > 1);
        // R2-12: пакет воспроизводимости для выбранного прогона.
        ExportReproCommand = new RelayCommand(_ => ExportRepro(), _ => Selected is not null);
        // R1-28: научное резюме поверх готового .earepro (файл выбирает пользователь).
        ExportAcademicCommand = new RelayCommand(_ => ExportAcademic());
        // R2-20: печать/PDF научного резюме через предпросмотр.
        PrintAcademicCommand = new RelayCommand(_ => PrintAcademic());

        // R2-14 + R2-6: живое обновление истории по шине событий — прогон,
        // запущенный где угодно (кнопка, watch-режим), сразу виден в списке.
        EntityAnalyzer.Core.Events.EventBus.Default
            .Subscribe<EntityAnalyzer.Core.Events.AnalysisCompletedEvent>(_ =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh));
    }

    /// <summary>Перечитать историю (вызывается при входе на страницу).</summary>
    public void Refresh()
    {
        var runs = _store.ReadLast(200);
        Runs.Clear();
        foreach (var r in runs.AsEnumerable().Reverse()) Runs.Add(r);
        Details = "";
        Comparison = "";
        DiffHeader = "";
        EntityDiffs.Clear();
        OnPropertyChanged(nameof(HasEntityDiffs));
        OnPropertyChanged(nameof(IsDataEmpty));
        if (Runs.Count == 0)
            Details = "История пока пуста — выполните первый анализ.";
    }

    /// <summary>
    /// Зафиксировать завершённый прогон: дописать в журнал и сразу показать.
    /// Единственная точка входа для записи — страница «Обзор» вызывает только её.
    /// </summary>
    public void Record(AnalysisSession session, IReadOnlyList<string> corpusDirs)
    {
        _store.Append(session.ToRunRecord(corpusDirs));
        Refresh();
        // Выделить свежую запись (первая в списке — самая новая).
        if (Runs.Count > 0) Selected = Runs[0];
    }

    /// <summary>
    /// R2-12: пакет воспроизводимости .earepro — прогон, хвост журнала с
    /// проверкой хеш-цепи, снимки конфигурации и SHA-256 инвентаризация корпуса.
    /// </summary>
    private void ExportRepro()
    {
        if (Selected is not { } run) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт пакета воспроизводимости",
            Filter = "Пакет воспроизводимости|*.earepro",
            FileName = $"earepro_{run.StartedAt:yyyyMMdd_HHmmss}.earepro",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var tail = _store.ReadLast(200).ToList();
            var chainOk = JournalIntegrity.Verify(tail).Count == 0;

            var settings = Services.SettingsService.Load();
            var codexPath = Services.UserCodexStore.FilePath;
            var patternsPath = EntityAnalyzer.Core.Semantics.SemanticPatternStore.UserPatternsPath;
            var inventory = EntityAnalyzer.Core.Analysis.FileInventory.Build(
                run.CorpusDirs.Where(Directory.Exists), settings.Extensions,
                // BUG-фикс: собственные отчёты (выходная папка) не попадают в пакет.
                settings.OutputDir);

            ReproPackage.Save(dlg.FileName,
                new ReproManifest(DateTimeOffset.Now, EntityAnalyzer.Core.About.Version,
                    run.Preset, run.TotalMentions, run.FilesTotal, tail.Count, chainOk),
                run, tail,
                settingsJson: Services.SettingsService.Serialize(settings),
                codexJson: File.Exists(codexPath) ? File.ReadAllText(codexPath) : null,
                patternsJson: File.Exists(patternsPath) ? File.ReadAllText(patternsPath) : null,
                inventory: inventory);

            System.Windows.MessageBox.Show(
                $"Пакет воспроизводимости сохранён:\n{dlg.FileName}\n\n" +
                $"Файлов в инвентаризации: {inventory.Count:N0}.\n" +
                $"Цепочка журнала: {(chainOk ? "целостна" : "НАРУШЕНА — журнал менялся мимо приложения")}.",
                "Пакет воспроизводимости", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось сформировать пакет: {ex.Message}",
                "Пакет воспроизводимости", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// R1-28: научное резюме (.md) поверх готового пакета .earepro —
    /// аннотация, методология, результаты и независимая проверка цепочки
    /// целостности. Резюме строится только из содержимого пакета.
    /// </summary>
    private void ExportAcademic()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите пакет воспроизводимости",
            Filter = "Пакет воспроизводимости|*.earepro",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var md = EntityAnalyzer.Core.Persistence.AcademicReport.Write(dlg.FileName);
            System.Windows.MessageBox.Show(
                $"Научное резюме сохранено рядом с пакетом:\n{md}\n\n" +
                "Документ готов к конвейеру публикации (pandoc → PDF/DOCX).",
                "Научное резюме", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось построить резюме: {ex.Message}",
                "Научное резюме", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// R2-20: печать/PDF научного резюме. Резюме строится во временном
    /// каталоге, разбирается на блоки и рендерится в окне предпросмотра;
    /// печать — штатным PrintDialog (включая «Microsoft Print to PDF»). 
    /// </summary>
    private void PrintAcademic()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите пакет воспроизводимости",
            Filter = "Пакет воспроизводимости|*.earepro",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "ea_print_" + Guid.NewGuid().ToString("N"));
            var mdPath = EntityAnalyzer.Core.Persistence.AcademicReport.Write(dlg.FileName, tempDir);
            var blocks = EntityAnalyzer.Core.Persistence.MarkdownBlocks.Parse(File.ReadAllText(mdPath));

            new PrintPreviewWindow($"Научное резюме — {System.IO.Path.GetFileName(dlg.FileName)}", blocks)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            }.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось открыть предпросмотр: {ex.Message}",
                "Печать резюме", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private void BuildDetails()
    {
        if (Selected is not { } r) { Details = ""; return; }
        var sb = new System.Text.StringBuilder();

        // UX-тикет: база диффа видна ДО нажатия «Сравнить» — то же правило,
        // что использует само сравнение (RunComparer.PickBase).
        var baseRun = RunComparer.PickBase(Runs, r);
        sb.AppendLine(baseRun is { } b
            ? $"База сравнения : прогон {b.StartedAt:yyyy-MM-dd HH:mm} ({b.Preset})"
            : "База сравнения : — (для диффа нужно ≥2 прогона)");

        sb.AppendLine($"Пресет          : {r.Preset}");
        sb.AppendLine($"Старт           : {r.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Длительность    : {r.DurationSeconds:0.0} c");
        sb.AppendLine($"Файлов          : {r.FilesTotal:N0} (с находками {r.FilesWithMentions:N0})");
        sb.AppendLine($"Упоминаний      : {r.TotalMentions:N0} (raw {r.RawMatches:N0} → final {r.FinalMentions:N0})");
        sb.AppendLine($"Сущностей       : {r.UniqueEntities}");
        if (r.CorpusDirs.Count > 0)
        {
            sb.AppendLine("Корпуса:");
            foreach (var d in r.CorpusDirs) sb.AppendLine("  " + d);
        }
        if (r.StageMs is { Count: > 0 })
        {
            sb.AppendLine("Тайминги стадий:");
            foreach (var kv in r.StageMs.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"  {kv.Key.PadRight(12)} : {kv.Value,8:0.0} мс");
        }
        Details = sb.ToString();
    }

    private void Compare()
    {
        if (Selected is not { } cur || Runs.Count < 2) return;
        // UX-тикет: правило базы — только RunComparer.PickBase, без локальных копий.
        var last = RunComparer.PickBase(Runs, cur);
        if (last is null) return;

        double D(int now, int before) => before == 0 ? 0 : (now - before) * 100.0 / before;

        // R1-3/R1-9: позиционный дифф по словарям EntityCounts журнала.
        EntityDiffs.Clear();
        var diff = RunComparer.Compare(last.EntityCounts ?? [], cur.EntityCounts ?? []);
        foreach (var e in diff.Entities.Take(30))
            EntityDiffs.Add(new EntityDeltaRow(e.Entity, e.Before, e.After, e.Delta));
        OnPropertyChanged(nameof(HasEntityDiffs));

        // UX-тикет: в шапке диффа названа база, против которой считаны дельты.
        DiffHeader = $"ДИФФ СУЩНОСТЕЙ · база — прогон {last.StartedAt:yyyy-MM-dd HH:mm} ({last.Preset})";

        Comparison =
            $"Сравнение с прогоном {last.StartedAt:yyyy-MM-dd HH:mm} ({last.Preset}):" + Environment.NewLine +
            $"  Упоминания : {cur.TotalMentions:N0} против {last.TotalMentions:N0} ({D(cur.TotalMentions, last.TotalMentions):+0.#;-0.#;0}%)" + Environment.NewLine +
            $"  Файлы с находками: {cur.FilesWithMentions:N0} против {last.FilesWithMentions:N0} ({D(cur.FilesWithMentions, last.FilesWithMentions):+0.#;-0.#;0}%)" + Environment.NewLine +
            $"  Сырые совпадения : {cur.RawMatches:N0} против {last.RawMatches:N0}" + Environment.NewLine +
            $"  Длительность     : {cur.DurationSeconds:0.0} c против {last.DurationSeconds:0.0} c" + Environment.NewLine +
            $"  Появились сущности: {diff.Appeared.Count}; исчезли: {diff.Disappeared.Count}";
    }
}
