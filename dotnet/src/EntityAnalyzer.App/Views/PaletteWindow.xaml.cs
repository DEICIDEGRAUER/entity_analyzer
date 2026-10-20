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
        int Priority = 0,
        string? Id = null) : IPaletteSearchItem
    {
        /// <summary>Устойчивый ключ для истории недавних команд.</summary>
        public string Key => Id ?? $"{Kind}|{Title}|{Hint}";

        public string SearchText => $"{Title} {Hint} {Keywords}";
    }

    private readonly List<PaletteCommand> _all;
    private List<PaletteCommand> _current = [];

    /// <summary>Признак того, что пользователь выполнил команду, а не просто закрыл палитру.</summary>
    public bool Executed { get; private set; }

    /// <summary>Ключ выполненной команды — для истории недавних.</summary>
    public string? LastExecutedKey { get; private set; }

    public PaletteWindow(
        IEnumerable<PaletteCommand> commands,
        IReadOnlyList<string>? recentKeys = null)
    {
        InitializeComponent();
        var all = commands.ToList();
        _all = [.. PaletteRanker.OrderByRecent(all, c => c.Key, recentKeys)];
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
        EmptyState.Visibility = _current.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExecuteButton.IsEnabled = _current.Count > 0;
        ResultStatus.Text = $"Найдено: {_current.Count}";
        if (ResultList.Items.Count > 0) ResultList.SelectedIndex = 0;
    }

    private void Execute(PaletteCommand? command)
    {
        if (command is null) return;
        LastExecutedKey = command.Key;
        Executed = true;
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

    private void ExecuteSelected_Click(object sender, RoutedEventArgs e) =>
        Execute(ResultList.SelectedItem as PaletteCommand);

    private bool IsMouseOverSelectedItem() => ResultList.SelectedIndex >= 0;

    private void MoveSelection(int delta)
    {
        if (ResultList.Items.Count == 0) return;
        var next = Math.Clamp(ResultList.SelectedIndex + delta, 0, ResultList.Items.Count - 1);
        ResultList.SelectedIndex = next;
        ResultList.ScrollIntoView(ResultList.SelectedItem);
    }
}
