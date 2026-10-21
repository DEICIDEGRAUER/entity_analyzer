using System.Windows;
using System.Windows.Controls;
using EntityAnalyzer.App.Services;
using EntityAnalyzer.App.ViewModels;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Logging;

namespace EntityAnalyzer.App.Views;

/// <summary>
/// R1-26 «Библиотека кодексов»: выбор встроенного тематического пака
/// и безопасный импорт карточек в пользовательский кодекс (MergeFrom).
/// </summary>
public partial class CodexPackWindow : Window
{
    private readonly CodexViewModel _vm;

    public CodexPackWindow(CodexViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        PackList.ItemsSource = CodexPacks.Builtin();
    }

    private void PackList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ImportButton.IsEnabled = PackList.SelectedItem is not null;

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (PackList.SelectedItem is not CodexPacks.PackInfo pack) return;
        try
        {
            var packDoc = CodexPacks.Load(pack.Id);
            var added = _vm.Doc.MergeFrom(packDoc);
            UserCodexStore.Save(_vm.Doc);
            BranchResolver.Initialize(_vm.Doc); // новые категории → цвета ветвей
            Log.Info("codex", $"Импорт пака «{pack.Id}»: добавлено {added} карточек");
            MessageBox.Show(this,
                added > 0
                    ? $"Добавлено карточек: {added}. Пак «{pack.Title}» импортирован."
                    : "Все карточки пака уже есть в кодексе — ничего не изменено.",
                "Библиотека кодексов", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось импортировать пак: {ex.Message}",
                "Библиотека кодексов", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
