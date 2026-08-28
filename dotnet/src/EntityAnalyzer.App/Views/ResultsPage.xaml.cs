using System.Windows;
using System.Windows.Controls;
using EntityAnalyzer.App.ViewModels;

namespace EntityAnalyzer.App.Views;

public partial class ResultsPage : UserControl
{
    public ResultsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ResultsViewModel vm)
                vm.FullTextRequested += OpenViewer;
        };
    }

    private void OpenViewer(string fullPath, System.Collections.Generic.IReadOnlyList<Core.Analysis.Mention> mentions)
    {
        var owner = Window.GetWindow(this);
        var win = new FileViewerWindow(fullPath, mentions, "упоминания") { Owner = owner };
        win.Show();
    }
}
