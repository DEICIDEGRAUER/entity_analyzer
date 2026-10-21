using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EntityAnalyzer.App.ViewModels;

namespace EntityAnalyzer.App.Views;

public partial class ReportsPage : UserControl
{
    private ReportsViewModel? _vm;

    public ReportsPage()
    {
        EnableIe11Rendering();
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm = DataContext as ReportsViewModel;
            if (_vm is not null) _vm.PropertyChanged += Vm_PropertyChanged;
        };
    }

    /// <summary>WebBrowser не биндится через MVVM — навигация из code-behind.</summary>
    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportsViewModel.HtmlUri) && _vm?.HtmlUri is not null)
            HtmlView.Navigate(_vm.HtmlUri);
    }

    /// <summary>
    /// UX: встроенный WebBrowser рендерит по движку IE7 по умолчанию — у части
    /// пользователей HTML-отчёты выглядели сломанными. Эмуляция IE11 лечит
    /// артефакты без внешних зависимостей (WebView2 требует отдельный runtime).
    /// </summary>
    private static void EnableIe11Rendering()
    {
        try
        {
            const string key = "SOFTWARE\\Microsoft\\Internet Explorer\\Main\\FeatureControl\\FEATURE_BROWSER_EMULATION";
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key);
            var exe = Path.GetFileName(Environment.ProcessPath ?? "EntityAnalyzer.exe");
            k.SetValue(exe, 11001, Microsoft.Win32.RegistryValueKind.DWord); // 11001 = IE11 standards
        }
        catch { /* реестр недоступен — работаем как раньше */ }
    }

    /// <summary>UX: надёжная альтернатива встроенному просмотру — системный браузер.</summary>
    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.HtmlUri is not null)
            Process.Start(new ProcessStartInfo(_vm.HtmlUri.ToString()) { UseShellExecute = true });
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ReportsViewModel vm)
            vm.Selected = e.NewValue as ReportNode;
    }

    private static string? FindReport(string name)
    {
        var vm = App.Current?.MainWindow?.DataContext as MainViewModel;
        var root = vm?.Reports.RootPath;
        if (root is null || !Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    private void OpenGraph_Click(object sender, RoutedEventArgs e)
    {
        var path = FindReport("cooccurrence_graph.html");
        if (path is null) { MessageBox.Show("Граф появится после первого анализа."); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenCloud_Click(object sender, RoutedEventArgs e)
    {
        var path = FindReport("variant_cloud.html");
        if (path is null) { MessageBox.Show("Облако появится после первого анализа."); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

