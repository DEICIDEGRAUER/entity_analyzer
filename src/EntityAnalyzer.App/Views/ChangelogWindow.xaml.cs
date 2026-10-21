using System.Windows;
using System.Windows.Controls;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.App.Views;

public partial class ChangelogWindow : Window
{
    public ChangelogWindow(System.Collections.Generic.IReadOnlyList<CatalogChangeEntry> entries)
    {
        InitializeComponent();
        foreach (var e in entries) List.Items.Add(e);
        if (List.Items.Count == 0)
            List.Items.Add("Журнал пока пуст — внесите изменения в каталог сущностей.");
    }
}
