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

    /// <summary>R1-7: палитра команд — навигация и ключевые действия.</summary>
    private void OpenPalette(MainViewModel vm)
    {
        var commands = new List<PaletteWindow.PaletteCommand>
        {
            new("Обзор", "страница", () => vm.Navigate(vm.Dashboard)),
            new("Результаты", "страница", () => vm.Navigate(vm.Results)),
            new("Кодекс", "страница", () => vm.Navigate(vm.CodexVm)),
            new("Схемы", "страница", () => vm.Navigate(vm.SchemaVm)),
            new("Отчёты", "страница", () => vm.Navigate(vm.Reports)),
            new("История", "страница", () => vm.Navigate(vm.History)),
            new("Поиск и паттерны", "страница", () => vm.Navigate(vm.AdvancedSearch)),
            new("Запустить анализ", "действие", () =>
            {
                vm.Navigate(vm.Dashboard);
                if (vm.Dashboard.RunCommand.CanExecute(null)) vm.Dashboard.RunCommand.Execute(null);
            }),
            new("Демо-корпус: попробовать за 30 секунд", "действие",
                () => { vm.Navigate(vm.Dashboard); vm.Dashboard.RunDemoCommand.Execute(null); }),
        };
        var win = new PaletteWindow(commands) { Owner = this };
        win.ShowDialog();
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
        col.Width = collapsed ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        RailBorder.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ExpandRailButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        if (DataContext is MainViewModel vm && vm.RailCollapsed != collapsed)
            vm.RailCollapsed = collapsed;
    }
}
