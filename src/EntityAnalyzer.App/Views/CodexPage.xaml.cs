using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EntityAnalyzer.App.Services;
using EntityAnalyzer.App.ViewModels;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Genealogy;
using EntityAnalyzer.Core.Logging;

namespace EntityAnalyzer.App.Views;

public partial class CodexPage : UserControl
{
    private CodexViewModel Vm => (CodexViewModel)DataContext;

    // R2-7: кэш последней раскладки для hit-test и подсветки.
    private List<LayoutNode> _layout = [];
    private CoOccurrenceGraph? _graph;
    private string? _hovered;

    // Item 51: размер канвы, под который построена раскладка. Перерисовка на
    // SizeChanged — иначе точки «залипают» у краёв при первом показе/ресайзе.
    private Size _renderedSize;

    // Item 5.1: кэш карточек режима «генеалогическое древо».
    private FamilyTree? _familyTree;
    private readonly Dictionary<string, Border> _familyCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Brush> _familyDefaultBrushes = new(StringComparer.Ordinal);

    public CodexPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            SelectDefaultSort();
            AttachGraph();
        };
        Loaded += (_, _) => AttachGraph();
        Unloaded += (_, _) => DetachGraph();
        GraphCanvas.SizeChanged += (_, e) =>
        {
            // Раскладка строится по ActualWidth/ActualHeight канвы: после
            // ресайза или первой раскладки её нужно пересобрать.
            if (Vm?.IsGraphView != true) return;
            if (Math.Abs(e.NewSize.Width - _renderedSize.Width) < 2 &&
                Math.Abs(e.NewSize.Height - _renderedSize.Height) < 2) return;
            RenderGraph();
        };
    }

    // Одна подписка на граф: страница может переехать в дерево визуалов
    // или получить новый DataContext без выгрузки — старый обработчик
    // тогда «залипал» и переключение режима не перерисовывало канву.
    private CodexViewModel? _hookedVm;

    private void AttachGraph()
    {
        var vm = Vm;
        if (ReferenceEquals(_hookedVm, vm))
        {
            RenderGraph();
            return;
        }
        if (_hookedVm is not null) _hookedVm.GraphInvalidated -= RenderGraph;
        _hookedVm = vm;
        if (vm is not null) vm.GraphInvalidated += RenderGraph;
        RenderGraph();
    }

    private void DetachGraph()
    {
        if (_hookedVm is not null) _hookedVm.GraphInvalidated -= RenderGraph;
        _hookedVm = null;
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

    /// <summary>Item 5.1: выйти из полноэкранного графа к списку и редактору.</summary>
    private void GraphExit_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.IsGraphView = false;
    }

    /// <summary>Item 5.1: открыть выбранный на графе узел в редакторе карточек.</summary>
    private void GraphOpenCard_Click(object sender, RoutedEventArgs e)
    {
        if (Vm?.Selected is null) return;
        Vm.IsGraphView = false;
    }

    /// <summary>Пересобрать и отрисовать граф (переключение вида, фильтры, новая сессия).</summary>
    private void RenderGraph()
    {
        if (Vm is null || GraphCanvas is null) return;
        GraphCanvas.Children.Clear();
        _hovered = null;
        _graph = null;
        _familyTree = null;
        _familyCards.Clear();
        _familyDefaultBrushes.Clear();
        if (!Vm.IsGraphView) return;

        if (Vm.GraphModeIndex == 1)
        {
            RenderFamilyTreeGraph();
            return;
        }

        var graph = Vm.GetFilteredGraph();
        if (graph is null || graph.Nodes.Count == 0) return;

        // Item 51: раскладка строится по размеру самой канвы, а не страницы.
        // Пока канва не измерена (скрытый вид/первая раскладка) — ждём
        // SizeChanged, чтобы не рисовать в 600×480 «по умолчанию».
        var width = GraphCanvas.ActualWidth;
        var height = GraphCanvas.ActualHeight;
        if (width < 50 || height < 50) return;

        _renderedSize = new Size(width, height);
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

        // Item 16: ручная разметка — типизированные связи кодекса поверх графа
        // (пунктир с подписью типа). Без Tag: подсветка узлов их не трогает.
        var overlay = Vm.GetOverlayLinks();
        var overlayBrush = TryFindResource("BrushBranch2") as Brush ?? Brushes.OrangeRed;
        var overlayBg = TryFindResource("BrushBgMain") as Brush;
        foreach (var o in overlay)
        {
            var a = _layout.FirstOrDefault(n => n.Name == o.From);
            var b = _layout.FirstOrDefault(n => n.Name == o.To);
            if (a is null || b is null) continue;

            var marked = new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = overlayBrush,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
            };
            Panel.SetZIndex(marked, 1);
            GraphCanvas.Children.Add(marked);

            var caption = new TextBlock
            {
                Text = o.Label,
                FontSize = 10,
                Foreground = overlayBrush,
                Background = overlayBg,
                Padding = new Thickness(3, 0, 3, 0),
            };
            caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(caption, Math.Clamp((a.X + b.X) / 2 - caption.DesiredSize.Width / 2,
                2, Math.Max(2, width - caption.DesiredSize.Width - 2)));
            Canvas.SetTop(caption, Math.Clamp((a.Y + b.Y) / 2 + 4,
                2, Math.Max(2, height - caption.DesiredSize.Height - 2)));
            Panel.SetZIndex(caption, 3);
            GraphCanvas.Children.Add(caption);
        }

        // Узлы: точка ветви 6×6 + имя антиквой. Item 51: подписи размещаются
        // жадно (справа/слева/сверху/снизу), чтобы не накладываться друг на друга.
        var placedLabels = new List<Rect>();
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
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var pos = PlaceLabel(n, label.DesiredSize, placedLabels, width, height);
            placedLabels.Add(new Rect(pos.X, pos.Y, label.DesiredSize.Width, label.DesiredSize.Height));
            Canvas.SetLeft(label, pos.X);
            Canvas.SetTop(label, pos.Y);
            Panel.SetZIndex(label, 2);
            GraphCanvas.Children.Add(label);
        }

        Log.Info("codex", $"Граф: {_layout.Count} узлов, {graph.Links.Count} рёбер, разметка: {overlay.Count}");
    }

    /// <summary>
    /// Item 5.1: режим «генеалогическое древо» встроенного графа кодекса.
    /// Строится по связям «породил»/«родитель», раскладывается по поколениям
    /// (см. <see cref="FamilyTreeLayout"/>), вписывается в канву масштабом.
    /// Клик по карточке открывает её в редакторе справа.
    /// </summary>
    private void RenderFamilyTreeGraph()
    {
        if (Vm is null || GraphCanvas is null) return;

        var tree = FamilyTreeBuilder.FromCodex(Vm.Doc);
        if (tree.Nodes.Count == 0)
        {
            Log.Info("codex", "Древо: связей «породил»/«родитель» нет");
            return;
        }

        var width = GraphCanvas.ActualWidth;
        var height = GraphCanvas.ActualHeight;
        if (width < 50 || height < 50) return;
        _renderedSize = new Size(width, height);
        _familyTree = tree;

        var options = new TreeLayoutOptions(ColumnWidth: 152, RowHeight: 104, Margin: 36,
            NodeWidth: 132, NodeHeight: 50);
        var layout = FamilyTreeLayout.Calculate(tree, options);

        var scale = Math.Min(1.0, Math.Min((width - 12) / layout.Width, (height - 12) / layout.Height));
        scale = Math.Max(0.35, scale);

        var inner = new Canvas
        {
            Width = layout.Width,
            Height = layout.Height,
            LayoutTransform = new ScaleTransform(scale, scale),
            Background = Brushes.Transparent,
        };
        Canvas.SetLeft(inner, Math.Max(4, (width - layout.Width * scale) / 2));
        Canvas.SetTop(inner, Math.Max(4, (height - layout.Height * scale) / 2));
        GraphCanvas.Children.Add(inner);

        var byId = layout.Placements.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var nodes = tree.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var borderBrush = TryFindResource("BrushBorderLight") as Brush ?? Brushes.Gray;
        var textPrimary = TryFindResource("BrushTextPrimary") as Brush ?? Brushes.Black;
        var textSecondary = TryFindResource("BrushTextSecondary") as Brush ?? Brushes.DimGray;
        var accent = TryFindResource("BrushBranch2") as Brush ?? Brushes.SeaGreen;
        var cardBg = TryFindResource("BrushBgMain") as Brush ?? Brushes.White;

        // Поколения: подписи и пунктирные разделители.
        for (var g = 0; g < tree.GenerationCount; g++)
        {
            var y = layout.RowMargin + g * layout.RowHeight;
            if (g > 0)
            {
                inner.Children.Add(new Line
                {
                    X1 = 0, X2 = layout.Width, Y1 = y - 18, Y2 = y - 18,
                    Stroke = borderBrush, StrokeThickness = 0.7, Opacity = 0.55,
                    StrokeDashArray = [4, 5],
                });
            }
            var caption = new TextBlock
            {
                Text = FamilyTree.GenerationLabel(g),
                FontSize = 9.5,
                Foreground = textSecondary,
                Opacity = 0.8,
            };
            Canvas.SetLeft(caption, 8);
            Canvas.SetTop(caption, y - 32);
            inner.Children.Add(caption);
        }

        // Родительские связи.
        var showLabels = tree.ParentLinks.Count <= 60;
        var labelRow = 0;
        foreach (var link in tree.ParentLinks)
        {
            if (!byId.TryGetValue(link.ParentId, out var pa) || !byId.TryGetValue(link.ChildId, out var ca))
                continue;
            var sx = pa.X + options.NodeWidth / 2;
            var sy = pa.Y + options.NodeHeight;
            var ex = ca.X + options.NodeWidth / 2;
            var ey = ca.Y;
            var midY = (sy + ey) / 2;
            var figure = new PathFigure { StartPoint = new Point(sx, sy), IsFilled = false };
            figure.Segments.Add(new BezierSegment(new Point(sx, midY), new Point(ex, midY), new Point(ex, ey), true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            inner.Children.Add(new Path
            {
                Data = geometry,
                Stroke = FamilyLinkBrush(link.Kind, link.Confidence, borderBrush, accent),
                StrokeThickness = link.Confidence == FamilyConfidence.Disputed ? 1.7 : 1.2,
                StrokeDashArray = FamilyLinkDash(link.Kind),
                Opacity = FamilyLinkOpacity(link.Confidence),
            });

            if (showLabels && link.DisplayLabel.Length > 0)
            {
                var label = new TextBlock
                {
                    Text = link.DisplayLabel + FamilyConfidenceSuffix(link.Confidence),
                    FontSize = 8.5,
                    Foreground = textSecondary,
                    Opacity = link.Confidence == FamilyConfidence.Hypothesis ? 0.6 : 0.9,
                    IsHitTestVisible = false,
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, (sx + ex) / 2 - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, midY - 12 - (labelRow++ % 4) * 11);
                inner.Children.Add(label);
            }
        }

        // Партнёрские связи.
        foreach (var pair in tree.SpouseLinks)
        {
            if (!byId.TryGetValue(pair.A, out var a) || !byId.TryGetValue(pair.B, out var b)) continue;
            var left = a.X <= b.X ? a : b;
            var right = a.X <= b.X ? b : a;
            var y = left.Y + options.NodeHeight / 2;
            inner.Children.Add(new Line
            {
                X1 = left.X + options.NodeWidth, X2 = right.X, Y1 = y, Y2 = y,
                Stroke = FamilyLinkBrush(pair.Kind, pair.Confidence, accent, accent),
                StrokeThickness = pair.Confidence == FamilyConfidence.Disputed ? 1.7 : 1.1,
                StrokeDashArray = FamilyLinkDash(pair.Kind),
                Opacity = FamilyLinkOpacity(pair.Confidence),
            });
        }

        // Карточки кодекса.
        var cardsById = Vm.Doc.Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var typesById = Vm.Doc.Types.ToDictionary(t => t.Id, StringComparer.Ordinal);
        foreach (var placement in layout.Placements)
        {
            if (!nodes.TryGetValue(placement.Id, out var node) ||
                !cardsById.TryGetValue(node.Id, out var card))
                continue;

            var star = typesById.GetValueOrDefault(card.TypeId)?.Color;
            var brush = borderBrush;
            if (star is { Length: > 0 } && TryParseBrush(star) is { } parsed) brush = parsed;

            var box = new Border
            {
                Width = options.NodeWidth,
                Height = options.NodeHeight,
                CornerRadius = new CornerRadius(8),
                Background = cardBg,
                BorderThickness = new Thickness(1.2),
                BorderBrush = brush,
                Tag = card.Id,
                Cursor = Cursors.Hand,
                ToolTip = card.Name + (card.Group.Length > 0 ? Environment.NewLine + card.Group : "") +
                          (node.Confidence == FamilyConfidence.Unknown
                              ? ""
                              : Environment.NewLine + "Уверенность связей: " + FamilyConfidenceScale.Label(node.Confidence)),
                Padding = new Thickness(7, 4, 7, 4),
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = node.Name,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = textPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (node.Subtitle.Length > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = node.Subtitle,
                    FontSize = 9,
                    Foreground = textSecondary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
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
                    Background = cardBg,
                    BorderThickness = new Thickness(1),
                    BorderBrush = FamilyConfidenceBrush(node.Confidence, accent),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(2, 0, 2, 0),
                    ToolTip = "Уверенность связей: " + FamilyConfidenceScale.Label(node.Confidence),
                    IsHitTestVisible = false,
                };
                badge.Child = new TextBlock
                {
                    Text = FamilyConfidenceScale.ShortLabel(node.Confidence),
                    FontSize = 7.8,
                    Foreground = FamilyConfidenceBrush(node.Confidence, accent),
                };
                content.Children.Add(badge);
                panel.Margin = new Thickness(0, 0, 24, 0);
            }
            box.Child = content;
            box.MouseLeftButtonDown += (_, e) =>
            {
                Vm.Selected = card;
                HighlightFamilySelection();
                e.Handled = true;
            };

            Canvas.SetLeft(box, placement.X);
            Canvas.SetTop(box, placement.Y);
            inner.Children.Add(box);
            _familyCards[card.Id] = box;
            _familyDefaultBrushes[card.Id] = brush;
        }

        HighlightFamilySelection();
        Log.Info("codex", $"Древо кодекса: {tree.Nodes.Count} карточек, {tree.ParentLinks.Count} связей, " +
                          $"{tree.SpouseLinks.Count} партнёрств, гипотез: {tree.HypothesisCount}, " +
                          $"поколений: {tree.GenerationCount}");
    }

    private static string FamilyConfidenceSuffix(FamilyConfidence confidence) =>
        confidence == FamilyConfidence.Unknown ? "" : $" · {FamilyConfidenceScale.ShortLabel(confidence)}";

    private static double FamilyLinkOpacity(FamilyConfidence confidence) => confidence switch
    {
        FamilyConfidence.Hypothesis => 0.42,
        FamilyConfidence.Disputed => 0.9,
        FamilyConfidence.Unknown => 0.72,
        _ => 0.86,
    };

    private static DoubleCollection? FamilyLinkDash(FamilyRelationKind kind) => kind switch
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

    private Brush FamilyLinkBrush(FamilyRelationKind kind, FamilyConfidence confidence, Brush fallback, Brush partner)
    {
        if (confidence == FamilyConfidence.Hypothesis)
            return TryFindResource("BrushTextSecondary") as Brush ?? fallback;
        if (confidence == FamilyConfidence.Disputed)
            return TryFindResource("BrushBranch4") as Brush ?? fallback;
        return kind switch
        {
            FamilyRelationKind.Adoptive => TryFindResource("BrushBranch2") as Brush ?? fallback,
            FamilyRelationKind.Foster => TryFindResource("BrushBranch3") as Brush ?? fallback,
            FamilyRelationKind.Guardian => TryFindResource("BrushBranch4") as Brush ?? fallback,
            FamilyRelationKind.Godparent => TryFindResource("BrushThemeMark") as Brush ?? fallback,
            FamilyRelationKind.Named => TryFindResource("BrushBranch5") as Brush ?? fallback,
            FamilyRelationKind.Origin => TryFindResource("BrushBranch1") as Brush ?? fallback,
            FamilyRelationKind.Partner => partner,
            _ => fallback,
        };
    }

    private Brush FamilyConfidenceBrush(FamilyConfidence confidence, Brush fallback) => confidence switch
    {
        FamilyConfidence.Proven => TryFindResource("BrushBranch2") as Brush ?? fallback,
        FamilyConfidence.Likely => TryFindResource("BrushBranch1") as Brush ?? fallback,
        FamilyConfidence.Hypothesis => TryFindResource("BrushThemeMark") as Brush ?? fallback,
        FamilyConfidence.Disputed => TryFindResource("BrushBranch4") as Brush ?? fallback,
        _ => fallback,
    };

    private static Brush? TryParseBrush(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Подсветка выбранной карточки в режиме древа (без полной перерисовки).</summary>
    private void HighlightFamilySelection()
    {
        if (_familyTree is null) return;
        var mark = TryFindResource("BrushThemeMark") as Brush ?? Brushes.Goldenrod;
        var selected = Vm?.Selected?.Id;
        foreach (var (id, card) in _familyCards)
        {
            var isSelected = string.Equals(id, selected, StringComparison.Ordinal);
            card.BorderBrush = isSelected ? mark : _familyDefaultBrushes.GetValueOrDefault(id, Brushes.Gray);
            card.BorderThickness = new Thickness(isSelected ? 2.2 : 1.2);
        }
    }

    /// <summary>
    /// Item 51: выбрать позицию подписи без пересечения с уже размещёнными.
    /// Кандидаты: справа, слева, сверху, снизу; при полном перекрытии — справа
    /// со сдвигом вниз. Все варианты удерживаются в границах канвы.
    /// </summary>
    private static Point PlaceLabel(LayoutNode node, Size size, List<Rect> placed,
        double canvasW, double canvasH)
    {
        const double gap = 6;
        Point[] candidates =
        [
            new(node.X + gap, node.Y - size.Height / 2),          // справа
            new(node.X - gap - size.Width, node.Y - size.Height / 2), // слева
            new(node.X - size.Width / 2, node.Y - gap - size.Height), // сверху
            new(node.X - size.Width / 2, node.Y + gap),               // снизу
        ];

        foreach (var c in candidates)
        {
            var r = ClampRect(new Rect(c.X, c.Y, size.Width, size.Height), canvasW, canvasH);
            if (!placed.Any(p => r.IntersectsWith(p))) return new Point(r.X, r.Y);
        }

        // Все позиции заняты — справа, каскадом вниз от узла.
        var fallback = ClampRect(
            new Rect(node.X + gap, node.Y + gap + placed.Count % 6 * (size.Height + 2), size.Width, size.Height),
            canvasW, canvasH);
        return new Point(fallback.X, fallback.Y);
    }

    private static Rect ClampRect(Rect r, double canvasW, double canvasH)
    {
        var x = Math.Clamp(r.X, 2, Math.Max(2, canvasW - r.Width - 2));
        var y = Math.Clamp(r.Y, 2, Math.Max(2, canvasH - r.Height - 2));
        return new Rect(x, y, r.Width, r.Height);
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
