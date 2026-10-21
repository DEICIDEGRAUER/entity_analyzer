using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

        // Горячие клавиши проверки: Y / N / U + Ctrl+Z. Работают только вне
        // полей ввода, чтобы русские «н», «у» и цифры в поиске не превращались
        // в вердикты.
        PreviewKeyDown += OnReviewKeyDown;
        MentionsList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.Count > 0) MentionsList.ScrollIntoView(e.AddedItems[0]);
        };
    }

    private void OnReviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ResultsViewModel vm || IsTypingTarget(Keyboard.FocusedElement)) return;

        var command = (e.Key, Keyboard.Modifiers) switch
        {
            (Key.Y, ModifierKeys.None) or (Key.D1, ModifierKeys.None) => vm.ConfirmMentionCommand,
            (Key.N, ModifierKeys.None) or (Key.D2, ModifierKeys.None) => vm.RejectMentionCommand,
            (Key.U, ModifierKeys.None) or (Key.D3, ModifierKeys.None) => vm.UncertainMentionCommand,
            (Key.Z, ModifierKeys.Control) => vm.UndoVerdictCommand,
            (Key.Enter, ModifierKeys.None) => vm.OpenMentionSourceCommand,
            _ => null,
        };
        if (command is null) return;

        if (command.CanExecute(vm.SelectedMention))
        {
            command.Execute(vm.SelectedMention);
            e.Handled = true;
        }
    }

    private static bool IsTypingTarget(IInputElement? element) =>
        element is TextBoxBase or ComboBox or PasswordBox;

    private void OpenViewer(string fullPath, System.Collections.Generic.IReadOnlyList<Core.Analysis.Mention> mentions)
    {
        var owner = Window.GetWindow(this);
        var win = new FileViewerWindow(fullPath, mentions, "упоминания") { Owner = owner };
        win.Show();
    }
}
