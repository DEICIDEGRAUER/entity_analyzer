using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EntityAnalyzer.Core.Text;

namespace EntityAnalyzer.App.Views;

/// <summary>
/// R1-7 «Command Palette» (Ctrl+K): нечёткий поиск по командам приложения
/// с выполнением выбранной. Ранжирование — PaletteRanker из ядра.
/// </summary>
public partial class PaletteWindow : Window
{
    /// <summary>
    /// Элемент палитры: команда, страница, карточка кодекса, упоминание или файл.
    /// <paramref name="Keywords"/> расширяет поиск, не засоряя видимую подпись.
    /// </summary>
    public sealed record PaletteCommand(
        string Title,
        string Hint,
        Action Run,
        string Kind = "команда",
        string Keywords = "",
        int Priority = 0) : IPaletteSearchItem
    {
        public string SearchText => $"{Title} {Hint} {Keywords}";
    }

    private readonly List<PaletteCommand> _all;
    private List<PaletteCommand> _current = [];

    public PaletteWindow(IEnumerable<PaletteCommand> commands)
    {
        InitializeComponent();
        _all = [.. commands];
        ApplyFilter("");
        Loaded += (_, _) => { QueryBox.Focus(); QueryBox.SelectAll(); };
    }

    private void ApplyFilter(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _current = _all; // пустой запрос — исходный порядок
        }
        else
        {
            // 200 — палитра для быстрого перехода, а не полный отчёт.
            _current = [.. PaletteRanker.Rank(_all, query, limit: 200)];
        }

        ResultList.ItemsSource = _current;
        if (ResultList.Items.Count > 0) ResultList.SelectedIndex = 0;
    }

    private void Execute(PaletteCommand? command)
    {
        if (command is null) return;
        Close();
        command.Run();
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyFilter(QueryBox.Text);

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                Execute(ResultList.SelectedItem as PaletteCommand);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void ResultList_Click(object sender, MouseButtonEventArgs e)
    {
        // Клик по строке — выполнить (если клик пришёлся на элемент).
        if (ResultList.SelectedItem is PaletteCommand cmd && IsMouseOverSelectedItem())
            Execute(cmd);
    }

    private bool IsMouseOverSelectedItem() => ResultList.SelectedIndex >= 0;

    private void MoveSelection(int delta)
    {
        if (ResultList.Items.Count == 0) return;
        var next = Math.Clamp(ResultList.SelectedIndex + delta, 0, ResultList.Items.Count - 1);
        ResultList.SelectedIndex = next;
        ResultList.ScrollIntoView(ResultList.SelectedItem);
    }
}
