using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EntityAnalyzer.App.Services;
using EntityAnalyzer.App.ViewModels;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Logging;

namespace EntityAnalyzer.App.Views;

public partial class CodexPage : UserControl
{
    private CodexViewModel Vm => (CodexViewModel)DataContext;

    // R2-7: кэш последней раскладки для hit-test и подсветки.
    private List<LayoutNode> _layout = [];
    private CoOccurrenceGraph? _graph;
    private string? _hovered;

    public CodexPage()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            // Защита от накопления подписок при смене контекста.
            if (args.OldValue is CodexViewModel old) old.GraphInvalidated -= RenderGraph;
            SelectDefaultSort();
            if (Vm is not null)
            {
                Vm.GraphInvalidated += RenderGraph;
                RenderGraph();
            }
        };
        Loaded += (_, _) => RenderGraph();
    }

    /// <summary>Syncs the sort combo with the VM default without firing SelectionChanged.</summary>
    private void SelectDefaultSort()
    {
        if (Vm is null) return;
        SortBox.SelectionChanged -= SortBox_SelectionChanged;
        SortBox.SelectedIndex = 0;
        SortBox.SelectionChanged += SortBox_SelectionChanged;
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null || SortBox.SelectedItem is not ComboBoxItem item || item.Tag is not SortMode mode) return;
        Vm.Sort = mode;
    }

    private void TableView_Checked(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not CheckBox cb) return;
        // Чекбокс «Таблица» включает табличный вид и гасит граф;
        // снятие галочки возвращает список.
        var table = cb.IsChecked == true;
        if (table && Vm.IsGraphView) Vm.IsGraphView = false;
        Vm.IsGalleryView = !table;
    }

    private void GraphView_Checked(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not CheckBox cb) return;
        // R2-7: биндинг OneWay не пишет в VM — состояние берём из чекбокса.
        Vm.IsGraphView = cb.IsChecked == true;
    }

    /// <summary>R1-26: окно библиотеки встроенных паков кодекса.</summary>
    private void Library_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var win = new CodexPackWindow(Vm) { Owner = Window.GetWindow(this) };
        win.ShowDialog();
    }

    /// <summary>Пересобрать и отрисовать граф (переключение вида, фильтры, новая сессия).</summary>
    private void RenderGraph()
    {
        if (Vm is null || GraphCanvas is null) return;
        GraphCanvas.Children.Clear();
        _hovered = null;
        if (!Vm.IsGraphView) return;

        var graph = Vm.GetFilteredGraph();
        if (graph is null || graph.Nodes.Count == 0) return;

        var width = ActualWidth > 10 ? ActualWidth : GraphCanvas.ActualWidth > 10 ? GraphCanvas.ActualWidth : 600;
        var height = ActualHeight > 10 ? ActualHeight : 480;
        _graph = graph;
        _layout = [.. GraphLayout.Calculate(graph, width, height)];

        var edgeBrush = TryFindResource("BrushBorderLight") as Brush ?? Brushes.Gray;
        var textBrush = TryFindResource("BrushTextPrimary") as Brush ?? Brushes.Black;

        // Рёбра: линия 1px; при наведении утолщаются связанные (см. ApplyHighlight).
        foreach (var l in graph.Links)
        {
            var a = _layout.FirstOrDefault(n => n.Name == l.Source);
            var b = _layout.FirstOrDefault(n => n.Name == l.Target);
            if (a is null || b is null) continue;
            var line = new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = edgeBrush,
                StrokeThickness = 1,
                Tag = (l.Source, l.Target), // для подсветки
            };
            Panel.SetZIndex(line, 0);
            GraphCanvas.Children.Add(line);
        }

        // Узлы: точка ветви 6×6 + имя антиквой.
        foreach (var n in _layout)
        {
            var brush = BranchResolver.Resolve(n.Name);
            var dot = new Ellipse
            {
                Width = 6, Height = 6,
                Fill = brush,
                StrokeThickness = 0,
                Tag = n.Name,
            };
            Canvas.SetLeft(dot, n.X - 3);
            Canvas.SetTop(dot, n.Y - 3);
            Panel.SetZIndex(dot, 2);
            GraphCanvas.Children.Add(dot);

            var label = new TextBlock
            {
                Text = n.Name,
                FontFamily = (FontFamily)TryFindResource("SerifFontFamily"),
                FontSize = 12,
                Foreground = textBrush,
                Tag = n.Name,
            };
            Canvas.SetLeft(label, n.X + 6);
            Canvas.SetTop(label, n.Y - 8);
            Panel.SetZIndex(label, 2);
            GraphCanvas.Children.Add(label);
        }

        Log.Info("codex", $"Граф: {_layout.Count} узлов, {graph.Links.Count} рёбер");
    }

    /// <summary>Наведение: утолщить рёбра выбранного узла, выделить соседей.</summary>
    private void GraphCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_graph is null || _layout.Count == 0) return;
        var p = e.GetPosition(GraphCanvas);
        var nearest = HitTest(p);
        if (nearest == _hovered) return;
        _hovered = nearest;
        ApplyHighlight();
    }

    private void GraphCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_hovered is null) return;
        _hovered = null;
        ApplyHighlight();
    }

    /// <summary>Клик по узлу — открыть карточку кодекса.</summary>
    private void GraphCanvas_Click(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        var name = HitTest(e.GetPosition(GraphCanvas));
        if (name is not null)
        {
            Vm.SelectCardByName(name);
            e.Handled = true;
        }
    }

    private string? HitTest(Point p)
    {
        const double radius = 12;
        string? best = null;
        var bestD = radius * radius;
        foreach (var n in _layout)
        {
            var dx = n.X - p.X;
            var dy = n.Y - p.Y;
            var d = dx * dx + dy * dy;
            if (d <= bestD) { bestD = d; best = n.Name; }
        }
        return best;
    }

    private void ApplyHighlight()
    {
        foreach (var child in GraphCanvas.Children)
        {
            switch (child)
            {
                case Line { Tag: (string s, string t) } line:
                    var linked = _hovered is not null && (s == _hovered || t == _hovered);
                    line.StrokeThickness = linked ? 2 : 1;
                    line.Opacity = _hovered is null || linked ? 1 : 0.35;
                    break;
                case TextBlock { Tag: string name } label:
                    var neighbor = _hovered is null || name == _hovered ||
                                   _graph!.Links.Any(l =>
                                       (l.Source == _hovered && l.Target == name) ||
                                       (l.Target == _hovered && l.Source == name));
                    label.FontWeight = name == _hovered ? FontWeights.SemiBold : FontWeights.Normal;
                    label.Opacity = neighbor ? 1 : 0.35;
                    break;
                case Ellipse { Tag: string dotName } dot:
                    var dotNeighbor = _hovered is null || dotName == _hovered ||
                                      _graph!.Links.Any(l =>
                                          (l.Source == _hovered && l.Target == dotName) ||
                                          (l.Target == _hovered && l.Source == dotName));
                    dot.Opacity = dotNeighbor ? 1 : 0.35;
                    break;
            }
        }
    }
}
