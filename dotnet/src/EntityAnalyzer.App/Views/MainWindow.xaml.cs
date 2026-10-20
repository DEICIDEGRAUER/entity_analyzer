using System.Windows;
using System.Windows.Controls;
using EntityAnalyzer.App.ViewModels;

namespace EntityAnalyzer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        DataContext = new MainViewModel();
        // Восстановить свёрнутость рейки из настроек.
        if (DataContext is MainViewModel vm && vm.RailCollapsed)
            ApplyRailState(collapsed: true);
    }

    // ─── QA-UX #19: горячие клавиши Ctrl+F (поиск страницы) и F5 (обновить) ───

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // R1-7: Ctrl+K — палитра команд.
        if (e.Key == System.Windows.Input.Key.K
            && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            OpenPalette(vm);
            e.Handled = true;
            return;
        }

        // R2-9: F11 — режим презентации (полный экран + скрытая рейка + крупный шрифт).
        if (e.Key == System.Windows.Input.Key.F11)
        {
            TogglePresentationMode(vm);
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.F5)
        {
            switch (vm.CurrentPage)
            {
                case ReportsViewModel rp: rp.Refresh(); e.Handled = true; break;
                case HistoryViewModel hh: hh.Refresh(); e.Handled = true; break;
            }
        }
        else if (e.Key == System.Windows.Input.Key.F
                 && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            var box = FindSearchBox(this);
            if (box is not null)
            {
                box.Focus();
                box.SelectAll();
                e.Handled = true;
            }
        }
    }

    private void OpenPalette_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) OpenPalette(vm);
    }

    /// <summary>R1-7: палитра команд — навигация и ключевые действия.</summary>
    private void OpenPalette(MainViewModel vm)
    {
        var previousFocus = System.Windows.Input.Keyboard.FocusedElement;
        var commands = new List<PaletteWindow.PaletteCommand>
        {
            new("Обзор", "страница", () => vm.Navigate(vm.Dashboard), "страница", Priority: 10),
            new("Результаты", "страница", () => vm.Navigate(vm.Results), "страница", Priority: 10),
            new("Кодекс", "страница", () => vm.Navigate(vm.CodexVm), "страница", Priority: 10),
            new("Мир", "страница", () => vm.Navigate(vm.World), "страница", Priority: 10),
            new("Схемы", "страница", () => vm.Navigate(vm.SchemaVm), "страница", Priority: 10),
            new("Отчёты", "страница", () => vm.Navigate(vm.Reports), "страница", Priority: 10),
            new("История", "страница", () => vm.Navigate(vm.History), "страница", Priority: 10),
            new("Цитатник", "страница", () => vm.Navigate(vm.QuoteBook), "страница", Priority: 10),
            new("Дневник", "страница", () => vm.Navigate(vm.Diary), "страница", Priority: 10),
            new("Паспорт файла", "страница", () => vm.Navigate(vm.Passport), "страница", Priority: 10),
            new("Словари", "страница", () => vm.Navigate(vm.Dictionaries), "страница", Priority: 10),
            new("Корпус", "страница", () => vm.Navigate(vm.Corpus), "страница", Priority: 10),
            new("Справка", "страница", () => vm.Navigate(vm.Help), "страница", Priority: 10),
            // Вкладка «Поиск и паттерны» живёт на «Обзоре» — отдельного пункта
            // меню нет (QA/UX #40), палитра лишь открывает саму вкладку.
            new("Совместные упоминания", "вкладка «Обзор»",
                () => vm.OpenSearchTab(), "вкладка", Priority: 10),
            new("Запустить анализ", "действие", () =>
            {
                vm.Navigate(vm.Dashboard);
                if (vm.Dashboard.RunCommand.CanExecute(null)) vm.Dashboard.RunCommand.Execute(null);
            }, Priority: 15),
            new("Демо-корпус: попробовать за 30 секунд", "действие",
                () => { vm.Navigate(vm.Dashboard); vm.Dashboard.RunDemoCommand.Execute(null); }, Priority: 15),
        };

        // Глобальный поиск: кодекс, упоминания и файлы текущей сессии.
        // Палитра — быстрый переход, поэтому кладём только осмысленные записи.
        foreach (var card in vm.Codex.Cards)
        {
            var cardName = card.Name;
            commands.Add(new PaletteWindow.PaletteCommand(
                $"Кодекс · {cardName}",
                string.IsNullOrWhiteSpace(card.Canonical) ? card.TypeId : card.Canonical,
                () =>
                {
                    vm.Navigate(vm.CodexVm);
                    vm.CodexVm.SelectCardByName(cardName);
                },
                "кодекс",
                $"{card.Canonical} {string.Join(' ', card.Aliases)} {string.Join(' ', card.Tags)}",
                Priority: 40,
                Id: $"codex:{cardName}"));
        }

        // Цитатник — тоже данные, а не только страница: по звёздочке можно
        // перейти к источнику, не вспоминая, в каком файле она стояла.
        vm.QuoteBook.Refresh();
        foreach (var quote in vm.QuoteBook.Items)
        {
            var row = quote;
            commands.Add(new PaletteWindow.PaletteCommand(
                $"Цитата · {row.Entity}",
                $"{row.Variant} · {row.Rec.FileName}:{row.Rec.Line}",
                () =>
                {
                    vm.Navigate(vm.QuoteBook);
                    vm.QuoteBook.Search = row.Entity;
                },
                "цитата",
                row.Context,
                Priority: 20,
                Id: $"quote:{row.Rec.Key}"));
        }

        // Отчёты: быстрый доступ к уже сгенерированным файлам.
        vm.Reports.Refresh();
        foreach (var report in FlattenReports(vm.Reports.Roots))
        {
            var node = report;
            commands.Add(new PaletteWindow.PaletteCommand(
                $"Отчёт · {node.Name}",
                node.FullPath,
                () =>
                {
                    vm.Navigate(vm.Reports);
                    vm.Reports.Filter = "";
                    vm.Reports.Selected = node;
                },
                "отчёт",
                node.FullPath,
                Priority: 5,
                Id: $"report:{node.FullPath}"));
        }

        if (vm.Session is { } session)
        {
            foreach (var file in session.Files)
            {
                // Файл без находок в «Результатах» открывается пустым —
                // не засоряем поиск десятком тысяч таких строк.
                if (file.Mentions.Count == 0) continue;
                var filePath = file.FullPath;
                commands.Add(new PaletteWindow.PaletteCommand(
                    $"Файл · {file.FileName}",
                    $"{file.Mentions.Count} цитат · {file.TotalFound} найдено",
                    () =>
                    {
                        vm.Navigate(vm.Results);
                        vm.Results.IsolateFileByPath(filePath);
                    },
                    "файл",
                    file.FileName,
                    Id: $"file:{file.FullPath}"));

                foreach (var mention in file.Mentions)
                {
                    var m = mention;
                    commands.Add(new PaletteWindow.PaletteCommand(
                        $"Упоминание · {m.Entity}",
                        $"{m.Variant} · {file.FileName}:{m.Line}",
                        () =>
                        {
                            vm.Navigate(vm.Results);
                            vm.Results.FocusMention(file, m);
                        },
                        "упоминание",
                        $"{m.Variant} {m.Theme} {m.Context}",
                        Priority: 35,
                        Id: $"mention:{file.FullPath}:{m.Offset}:{m.Line}:{m.Variant}"));
                }
            }
        }

        var win = new PaletteWindow(commands, vm.RecentPaletteCommands) { Owner = this };
        win.ShowDialog();
        if (win.Executed && win.LastExecutedKey is { } executedKey)
            vm.RememberPaletteCommand(executedKey);
        // Возвращаем фокус туда, откуда палитру открыли, если команда не
        // сменила страницу. Это важно для клавиатуры и экранных дикторов.
        if (!win.Executed
            && previousFocus is UIElement { IsVisible: true, IsEnabled: true } target)
            System.Windows.Input.Keyboard.Focus(target);
    }

    private static IEnumerable<ReportNode> FlattenReports(IEnumerable<ReportNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (!node.IsFolder) yield return node;
            else if (node.Children is not null)
                foreach (var child in FlattenReports(node.Children))
                    yield return child;
        }
    }

    /// <summary>Ищет первое видимое поле поиска на текущей странице.</summary>
    private static TextBox? FindSearchBox(DependencyObject root)
    {
        if (root is TextBox { Visibility: Visibility.Visible, IsEnabled: true } tb
            && string.IsNullOrEmpty(tb.Name) is false
            && (tb.Name.EndsWith("SearchBox") || tb.Name.EndsWith("Box") && tb.Name.Contains("Search")))
            return tb;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindSearchBox(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)
            && DataContext is MainViewModel vm)
        {
            vm.ZoomStep(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }
    }

    // ─── Регулируемые границы: сворачивание/разворот навигационной рейки ───

    private void CollapseRail_Click(object sender, RoutedEventArgs e) => ApplyRailState(true);
    private void ExpandRail_Click(object sender, RoutedEventArgs e) => ApplyRailState(false);

    // ─── R2-9: режим презентации ───

    private bool _presentationMode;
    private bool _railWasCollapsed;
    private double _zoomBeforePresentation;
    private WindowState _stateBeforePresentation;

    /// <summary>
    /// F11: полный экран, скрытая рейка и увеличенный масштаб — для лекций
    /// и докладов. Повторное F11 возвращает прежнее состояние (настройки
    /// пользователя не перезаписываются: Zoom восстанавливается из памяти).
    /// </summary>
    private void TogglePresentationMode(MainViewModel vm)
    {
        if (!_presentationMode)
        {
            _presentationMode = true;
            _railWasCollapsed = vm.RailCollapsed;
            _zoomBeforePresentation = vm.Zoom;
            _stateBeforePresentation = WindowState;

            ApplyRailState(collapsed: true);
            vm.Zoom = Math.Min(1.6, Math.Round(_zoomBeforePresentation + 0.25, 1));
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            vm.StatusText = "Режим презентации — F11 для выхода";
        }
        else
        {
            _presentationMode = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforePresentation;
            vm.Zoom = _zoomBeforePresentation;
            ApplyRailState(collapsed: _railWasCollapsed);
            vm.StatusText = "Готов к работе";
        }
    }

    private void ApplyRailState(bool collapsed)
    {
        var col = (ColumnDefinition)FindName("RailColumn")!;
        if (collapsed)
        {
            col.MinWidth = 0;
            col.Width = new GridLength(0);
            RailBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            // QA/UX #4: раньше разворот задавал 2* — рейка прилипала к 2/3 экрана.
            // Возвращаем прежнюю фиксированную ширину 212 (как в исходной разметке).
            col.MinWidth = 212;
            col.Width = new GridLength(212);
            RailBorder.Visibility = Visibility.Visible;
        }
        ExpandRailButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        if (DataContext is MainViewModel vm && vm.RailCollapsed != collapsed)
            vm.RailCollapsed = collapsed;
    }
}
