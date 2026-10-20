using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EntityAnalyzer.Core.Text;

namespace EntityAnalyzer.App.Views;

/// <summary>
/// R1-7 «Command Palette» (Ctrl+K): нечёткий поиск по командам приложения
/// с выполнением выбранной. Ранжирование — FuzzyFilter из ядра.
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
        int Priority = 0)
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
            var q = query.Trim();
            var texts = _all.Select(c => c.SearchText).ToList();
            var ranked = FuzzyFilter.Rank(texts, q);
            var used = new bool[_all.Count];
            var scored = new List<(PaletteCommand Command, int Score, int Order, bool TitleMatch)>();
            var order = 0;
            foreach (var match in ranked)
            {
                for (var i = 0; i < texts.Count; i++)
                {
                    if (used[i] || texts[i] != match.Item) continue;
                    used[i] = true;
                    var command = _all[i];
                    // Совпадение в заголовке важнее совпадения в длинном
                    // контексте: иначе файлы с коротким путём вытесняют
                    // упоминания сущности из первых 200 строк.
                    var titleScore = FuzzyFilter.Score(command.Title, q);
                    var titleMatch = titleScore > 0;
                    var rankScore = titleMatch
                        ? titleScore + command.Priority
                        : match.Score;
                    scored.Add((command, rankScore, order++, titleMatch));
                    break;
                }
            }

            _current = scored
                .OrderByDescending(x => x.TitleMatch)
                .ThenByDescending(x => x.Score)
                .ThenBy(x => x.Command.Title, StringComparer.Ordinal)
                .ThenBy(x => x.Order)
                .Take(200) // палитра — быстрый переход, не отчёт
                .Select(x => x.Command)
                .ToList();
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
