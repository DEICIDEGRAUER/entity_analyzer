using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EntityAnalyzer.App.ViewModels;
using EntityAnalyzer.Core.Genealogy;
using EntityAnalyzer.Core.Hierarchy;

namespace EntityAnalyzer.App.Views;

/// <summary>
/// Группа 4: страница «Мир». Код-behind рисует семейное древо на Canvas
/// (поколения, родительские и супружеские связи) и связывает выбор узла с VM.
/// </summary>
public partial class WorldPage : UserControl
{
    private const double NodeWidth = 156;
    private const double NodeHeight = 62;

    private WorldViewModel? _vm;
    private readonly Dictionary<string, Border> _nodeBorders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Brush> _nodeDefaultBrushes = new(StringComparer.Ordinal);

    public WorldPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Hook();
            RenderTree();
        };
        Unloaded += (_, _) => Unhook();
        DataContextChanged += (_, _) =>
        {
            Hook();
            RenderTree();
        };
    }

    private void Hook()
    {
        Unhook();
        _vm = DataContext as WorldViewModel;
        if (_vm is null) return;
        _vm.Tree.TreeChanged += RenderTree;
        _vm.Tree.SelectionChanged += HighlightSelection;
    }

    private void Unhook()
    {
        if (_vm is null) return;
        _vm.Tree.TreeChanged -= RenderTree;
        _vm.Tree.SelectionChanged -= HighlightSelection;
        _vm = null;
    }

    private void TreeZoomIn_Click(object sender, RoutedEventArgs e) =>
        ZoomSlider.Value = Math.Min(ZoomSlider.Maximum, ZoomSlider.Value + 0.15);

    private void TreeZoomOut_Click(object sender, RoutedEventArgs e) =>
        ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, ZoomSlider.Value - 0.15);

    private void TreePeople_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TreePeopleList.SelectedItem is PersonRow row)
            _vm?.Tree.SelectNode(row);
    }

    private void HierarchyTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _vm?.Hierarchy.Select(e.NewValue as HierarchyNodeVm);
    }

    private void HierarchySearch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: HierarchyNodeVm node })
            _vm?.Hierarchy.Select(node);
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void RenderTree()
    {
        if (TreeCanvas is null) return;

        TreeCanvas.Children.Clear();
        _nodeBorders.Clear();
        _nodeDefaultBrushes.Clear();

        var tree = _vm?.Tree.Tree;
        var layout = _vm?.Tree.Layout;
        if (tree is null || layout is null || tree.Nodes.Count == 0)
        {
            TreeCanvas.Width = 0;
            TreeCanvas.Height = 0;
            return;
        }

        var borderBrush = Res("BrushBorderLight", Brushes.Gray);
        var textPrimary = Res("BrushTextPrimary", Brushes.Black);
        var textSecondary = Res("BrushTextSecondary", Brushes.DimGray);
        var accent = Res("BrushBranch1", Brushes.SteelBlue);
        var nodeBg = Res("BrushBgMain", Brushes.WhiteSmoke);

        var byId = layout.Placements.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var nodes = tree.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        var width = Math.Max(layout.Width, 400);
        var height = Math.Max(layout.Height, 300);
        TreeCanvas.Width = width + 40;
        TreeCanvas.Height = height + 40;

        // Поколения: тонкие линии и подписи слева.
        for (var g = 0; g < tree.GenerationCount; g++)
        {
            var y = layout.RowMargin + g * layout.RowHeight;
            if (g > 0)
            {
                var line = new Line
                {
                    X1 = 0,
                    X2 = width,
                    Y1 = y - 22,
                    Y2 = y - 22,
                    Stroke = borderBrush,
                    StrokeThickness = 0.7,
                    Opacity = 0.55,
                    StrokeDashArray = [4, 5],
                };
                TreeCanvas.Children.Add(line);
            }

            var label = new TextBlock
            {
                Text = FamilyTree.GenerationLabel(g),
                FontSize = 10,
                Foreground = textSecondary,
                Opacity = 0.8,
            };
            Canvas.SetLeft(label, 10);
            Canvas.SetTop(label, y - 36);
            TreeCanvas.Children.Add(label);
        }

        // Родительские связи: плавные кривые от низа родителя к верху ребёнка.
        var showLinkLabels = tree.ParentLinks.Count <= 90;
        var labelRow = 0;
        foreach (var link in tree.ParentLinks)
        {
            if (!byId.TryGetValue(link.ParentId, out var pa) || !byId.TryGetValue(link.ChildId, out var ca))
                continue;

            var sx = pa.X + NodeWidth / 2;
            var sy = pa.Y + NodeHeight;
            var ex = ca.X + NodeWidth / 2;
            var ey = ca.Y;
            var midY = (sy + ey) / 2;

            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(sx, sy), IsFilled = false };
            figure.Segments.Add(new BezierSegment(new Point(sx, midY), new Point(ex, midY), new Point(ex, ey), true));
            geometry.Figures.Add(figure);

            TreeCanvas.Children.Add(new Path
            {
                Data = geometry,
                Stroke = LinkBrush(link.Kind, link.Confidence, borderBrush, accent),
                StrokeThickness = link.Confidence == FamilyConfidence.Disputed ? 1.8 : 1.3,
                StrokeDashArray = LinkDash(link.Kind),
                Opacity = LinkOpacity(link.Confidence),
            });

            if (showLinkLabels && link.DisplayLabel.Length > 0)
            {
                var caption = new TextBlock
                {
                    Text = link.DisplayLabel + ConfidenceSuffix(link.Confidence),
                    FontSize = 9.5,
                    Foreground = link.Confidence == FamilyConfidence.Hypothesis ? textSecondary : textPrimary,
                    Opacity = link.Confidence == FamilyConfidence.Hypothesis ? 0.62 : 0.9,
                    IsHitTestVisible = false,
                };
                caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(caption, (sx + ex) / 2 - caption.DesiredSize.Width / 2);
                Canvas.SetTop(caption, midY - 14 - (labelRow++ % 4) * 12);
                TreeCanvas.Children.Add(caption);
            }
        }

        // Партнёрские связи: собственный штрих и подпись, чтобы не путать с родством.
        foreach (var pair in tree.SpouseLinks)
        {
            if (!byId.TryGetValue(pair.A, out var a) || !byId.TryGetValue(pair.B, out var b))
                continue;
            var left = a.X <= b.X ? a : b;
            var right = a.X <= b.X ? b : a;
            var y = left.Y + NodeHeight / 2;
            TreeCanvas.Children.Add(new Line
            {
                X1 = left.X + NodeWidth,
                X2 = right.X,
                Y1 = y,
                Y2 = y,
                Stroke = LinkBrush(pair.Kind, pair.Confidence, accent, accent),
                StrokeThickness = pair.Confidence == FamilyConfidence.Disputed ? 1.7 : 1.2,
                StrokeDashArray = LinkDash(pair.Kind),
                Opacity = LinkOpacity(pair.Confidence),
            });
        }

        // Карточки людей.
        foreach (var placement in layout.Placements)
        {
            if (!nodes.TryGetValue(placement.Id, out var node)) continue;

            var card = new Border
            {
                Width = NodeWidth,
                Height = NodeHeight,
                CornerRadius = new CornerRadius(9),
                Background = nodeBg,
                BorderThickness = new Thickness(1.2),
                BorderBrush = NodeBrush(node.Sex, borderBrush),
                Tag = node.Id,
                Cursor = Cursors.Hand,
                ToolTip = BuildTooltip(node),
                Padding = new Thickness(9, 5, 9, 5),
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = node.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                Foreground = textPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (node.Subtitle.Length > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = node.Subtitle,
                    FontSize = 10.5,
                    Foreground = textSecondary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }

            var content = new Grid();
            content.Children.Add(panel);
            if (node.Confidence != FamilyConfidence.Unknown)
            {
                var badge = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = nodeBg,
                    BorderThickness = new Thickness(1),
                    BorderBrush = ConfidenceBrush(node.Confidence, accent),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(3, 0, 3, 0),
                    ToolTip = "Уверенность связей: " + FamilyConfidenceScale.Label(node.Confidence),
                    IsHitTestVisible = false,
                };
                badge.Child = new TextBlock
                {
                    Text = FamilyConfidenceScale.ShortLabel(node.Confidence),
                    FontSize = 8.5,
                    Foreground = ConfidenceBrush(node.Confidence, accent),
                };
                content.Children.Add(badge);
                panel.Margin = new Thickness(0, 0, 28, 0);
            }

            card.Child = content;
            card.MouseLeftButtonDown += (_, _) => _vm?.Tree.SelectNode(node.Id);

            Canvas.SetLeft(card, placement.X);
            Canvas.SetTop(card, placement.Y);
            TreeCanvas.Children.Add(card);

            _nodeBorders[node.Id] = card;
            _nodeDefaultBrushes[node.Id] = card.BorderBrush;
        }

        HighlightSelection();
        TreeScroll.ScrollToHome();
    }

    private static string BuildTooltip(FamilyTreeNode node)
    {
        var text = node.Name;
        if (node.Subtitle.Length > 0) text += Environment.NewLine + node.Subtitle;
        if (node.Confidence != FamilyConfidence.Unknown)
            text += Environment.NewLine + "Уверенность связей: " + FamilyConfidenceScale.Label(node.Confidence);
        if (node.Detail.Length > 0) text += Environment.NewLine + Environment.NewLine + node.Detail;
        return text;
    }

    private static string ConfidenceSuffix(FamilyConfidence confidence) =>
        confidence == FamilyConfidence.Unknown ? "" : $" · {FamilyConfidenceScale.ShortLabel(confidence)}";

    private static double LinkOpacity(FamilyConfidence confidence) => confidence switch
    {
        FamilyConfidence.Hypothesis => 0.42,
        FamilyConfidence.Disputed => 0.9,
        FamilyConfidence.Unknown => 0.72,
        _ => 0.86,
    };

    private static DoubleCollection? LinkDash(FamilyRelationKind kind) => kind switch
    {
        FamilyRelationKind.Adoptive => [6, 3],
        FamilyRelationKind.Foster => [2, 3],
        FamilyRelationKind.Guardian => [6, 2, 2, 2],
        FamilyRelationKind.Godparent => [1, 3],
        FamilyRelationKind.Named => [4, 2],
        FamilyRelationKind.Origin => [2, 2],
        FamilyRelationKind.Partner => [3, 3],
        _ => null,
    };

    private Brush LinkBrush(FamilyRelationKind kind, FamilyConfidence confidence, Brush fallback, Brush partner)
    {
        if (confidence == FamilyConfidence.Hypothesis) return Res("BrushTextSecondary", fallback);
        if (confidence == FamilyConfidence.Disputed) return Res("BrushBranch4", fallback);
        return kind switch
        {
            FamilyRelationKind.Adoptive => Res("BrushBranch2", fallback),
            FamilyRelationKind.Foster => Res("BrushBranch3", fallback),
            FamilyRelationKind.Guardian => Res("BrushBranch4", fallback),
            FamilyRelationKind.Godparent => Res("BrushThemeMark", fallback),
            FamilyRelationKind.Named => Res("BrushBranch5", fallback),
            FamilyRelationKind.Origin => Res("BrushBranch1", fallback),
            FamilyRelationKind.Partner => partner,
            _ => fallback,
        };
    }

    private Brush ConfidenceBrush(FamilyConfidence confidence, Brush fallback) => confidence switch
    {
        FamilyConfidence.Proven => Res("BrushBranch2", fallback),
        FamilyConfidence.Likely => Res("BrushBranch1", fallback),
        FamilyConfidence.Hypothesis => Res("BrushThemeMark", fallback),
        FamilyConfidence.Disputed => Res("BrushBranch4", fallback),
        _ => fallback,
    };

    private Brush NodeBrush(string sex, Brush fallback) => sex.ToUpperInvariant() switch
    {
        "M" => new SolidColorBrush(Color.FromRgb(0x5B, 0x8F, 0xC4)),
        "F" => new SolidColorBrush(Color.FromRgb(0xC9, 0x7F, 0x9E)),
        _ => fallback,
    };

    private void HighlightSelection()
    {
        if (_vm is null) return;
        var selectedId = _vm.Tree.SelectedNode?.Id;
        var mark = Res("BrushThemeMark", Brushes.Goldenrod);
        foreach (var (id, card) in _nodeBorders)
        {
            var selected = string.Equals(id, selectedId, StringComparison.Ordinal);
            card.BorderBrush = selected ? mark : _nodeDefaultBrushes.GetValueOrDefault(id, Brushes.Gray);
            card.BorderThickness = new Thickness(selected ? 2.4 : 1.2);
            if (selected) card.BringIntoView();
        }
        SyncPeopleSelection();
    }

    /// <summary>После пересборки списка возвращает подсветку выбранной персоны.</summary>
    private void SyncPeopleSelection()
    {
        if (_vm is null || TreePeopleList is null) return;
        var selected = _vm.Tree.SelectedNode;
        if (selected is null)
        {
            if (TreePeopleList.SelectedItem is not null) TreePeopleList.SelectedItem = null;
            return;
        }
        var row = TreePeopleList.Items.OfType<PersonRow>()
            .FirstOrDefault(r => r.Node.Id == selected.Id);
        if (!ReferenceEquals(TreePeopleList.SelectedItem, row))
        {
            TreePeopleList.SelectedItem = row;
            if (row is not null) TreePeopleList.ScrollIntoView(row);
        }
    }
}
