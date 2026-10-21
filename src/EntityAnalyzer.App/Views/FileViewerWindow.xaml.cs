using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.IO;
using EntityAnalyzer.Core.Matching;

namespace EntityAnalyzer.App.Views;

/// <summary>
/// Drill-down: полный текст исходного файла со подсветкой всех упоминаний.
/// R1-18 «Режим вычитки»: навигация ↑↓/кнопками между находками со счётчиком;
/// текст разбит на абзацы по строкам — BringIntoView позиционирует на находку.
/// </summary>
public partial class FileViewerWindow : Window
{
    // Навигация по находкам (R1-18): параллельные списки в порядке возрастания позиций.
    private readonly List<Run> _navRuns = [];
    private readonly List<MatchRange> _ranges = [];
    private int _currentIndex = -1;
    private TextDecorationCollection? _normalDecoration;
    private TextDecorationCollection? _focusDecoration;

    // Мини-карта (R2-8): данные строк документа + координаты точек находок.
    private readonly List<int> _lineLengths = [];
    private readonly List<int> _lineOffsets = [];
    private readonly List<int> _rangeLines = [];
    private readonly List<int> _rangeCols = [];
    private MinimapLayout.Layout? _minimap;
    private readonly List<Point> _dotPoints = [];
    private System.Windows.Shapes.Rectangle? _viewportRect;
    private System.Windows.Shapes.Ellipse? _currentDotMarker;
    private Brush? _branch;
    private System.Windows.Controls.ScrollViewer? _innerScroll;

    public FileViewerWindow(string fullPath, IReadOnlyList<Mention> mentions, string entityHint)
    {
        InitializeComponent();
        Title = $"{Path.GetFileName(fullPath)} — {entityHint}";
        PreviewKeyDown += OnNavigationKey;
        // Метрики прокрутки живут на внутреннем ScrollViewer'е шаблона.
        Loaded += (_, _) =>
        {
            _innerScroll = FindScrollViewer(Viewer);
            if (_innerScroll is not null) _innerScroll.ScrollChanged += OnScrollChanged;
            UpdateViewportIndicator();
        };

        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            var text = DocumentReader.Extract(fullPath, bytes);
            BuildDocument(text, mentions.Select(m => m.Variant).Where(v => !v.Contains("[fuzzy")).Distinct().ToList(), entityHint);
        }
        catch (Exception ex)
        {
            Viewer.Document = new FlowDocument(new Paragraph(new Run("Не удалось открыть файл: " + ex.Message)));
        }
    }

    private void BuildDocument(string text, IReadOnlyList<string> variants, string entityHint)
    {
        // Цвет ветви сущности — для подчёркивания совпадений.
        var branch = EntityAnalyzer.App.Services.BranchResolver.Resolve(entityHint);
        _branch = branch;

        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("JetBrains Mono, Cascadia Mono, Consolas"),
            FontSize = 12,
            Foreground = FindResource("BrushTextPrimary") as Brush ?? Brushes.DimGray,
            Background = FindResource("BrushBgMain") as Brush ?? Brushes.White,
            PagePadding = new Thickness(32),
        };

        // Найти все вхождения вариантов (границы слов) и построить чередование Run/Highlight.
        var marks = new List<(int Start, int End)>();
        foreach (var v in variants)
        {
            if (v.Length < 3) continue;
            var idx = 0;
            while ((idx = text.IndexOf(v, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var beforeOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                var afterIdx = idx + v.Length;
                var afterOk = afterIdx >= text.Length || !char.IsLetterOrDigit(text[afterIdx]);
                if (beforeOk && afterOk) marks.Add((idx, afterIdx));
                idx += 1;
            }
        }
        marks.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Подчёркивание 1px цветом ветви вместо заливки (академический стиль);
        // текущая находка режима вычитки — 2px и Bold.
        _normalDecoration = new TextDecorationCollection(
        [
            new TextDecoration { Location = TextDecorationLocation.Underline, Pen = new Pen(branch, 1d), PenOffset = 2d },
        ]);
        _focusDecoration = new TextDecorationCollection(
        [
            new TextDecoration { Location = TextDecorationLocation.Underline, Pen = new Pen(branch, 2d), PenOffset = 2d },
        ]);

        // R1-18: текст разбивается на абзацы по строкам — иначе переносы
        // теряются в сплошном потоке, а BringIntoView не может позиционировать
        // внутри одного гигантского Paragraph.
        var mi = 0;
        var offset = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var para = new Paragraph { Margin = new Thickness(0) };
            var lineStart = offset;
            var lineEnd = offset + line.Length;

            // Данные строки для мини-карты (R2-8).
            _lineLengths.Add(line.Length);
            _lineOffsets.Add(lineStart);

            while (mi < marks.Count && marks[mi].End <= lineStart) mi++; // вне строки
            var mj = mi;
            var pos = lineStart;
            while (mj < marks.Count && marks[mj].Start < lineEnd)
            {
                var (start, end) = marks[mj];
                mj++;
                // Клип слева/справа по строке: пересекающиеся варианты и
                // находки, перенесённые с предыдущей строки.
                var segStart = Math.Max(start, pos);
                var segEnd = Math.Min(end, lineEnd);
                if (segEnd <= segStart) continue;

                if (segStart > pos)
                    para.Inlines.Add(new Run(text[pos..segStart]));

                var run = new Run(text[segStart..segEnd])
                {
                    TextDecorations = _normalDecoration,
                    FontWeight = FontWeights.SemiBold,
                };
                para.Inlines.Add(run);

                // В навигацию — один раз, на строке, где находка начинается.
                if (start >= lineStart)
                {
                    _navRuns.Add(run);
                    _ranges.Add(new MatchRange(start, end));
                    _rangeLines.Add(_lineLengths.Count - 1);
                    _rangeCols.Add(start - lineStart);
                }
                pos = segEnd;
            }
            if (pos < lineEnd) para.Inlines.Add(new Run(text[pos..lineEnd]));

            doc.Blocks.Add(para);
            offset = lineEnd + 1; // +1 за '\n'
        }

        NavCounter.Text = $"0 / {_ranges.Count}";
        Viewer.Document = doc;

        RebuildMinimap(branch);
    }

    // ─── R2-8: мини-карта файла ───

    /// <summary>Пересчёт и отрисовка миниатюры (при изменении размера канвы).</summary>
    private void RebuildMinimap(Brush branch)
    {
        Minimap.Children.Clear();
        _dotPoints.Clear();
        _viewportRect = null;
        _currentDotMarker = null;
        if (_lineLengths.Count == 0 || Minimap.ActualHeight <= 0) return;

        var dots = new List<(int Line, int Column)>();
        for (var i = 0; i < _rangeLines.Count; i++)
            dots.Add((_rangeLines[i], _rangeCols[i]));

        _minimap = MinimapLayout.Calculate(_lineLengths, dots, Minimap.ActualHeight);

        var rowBrush = FindResource("BrushBorderLight") as Brush ?? Brushes.LightGray;
        var w = Minimap.ActualWidth;

        foreach (var row in _minimap.Rows)
        {
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, row.WidthFraction * w),
                Height = Math.Max(1, row.Height),
                Fill = rowBrush,
            };
            System.Windows.Controls.Canvas.SetLeft(rect, 0);
            System.Windows.Controls.Canvas.SetTop(rect, row.Y);
            Minimap.Children.Add(rect);
        }

        foreach (var dot in _minimap.Dots)
        {
            var p = new Point(dot.XFraction * w, dot.Y);
            _dotPoints.Add(p);
            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = branch,
            };
            System.Windows.Controls.Canvas.SetLeft(ellipse, p.X - 3);
            System.Windows.Controls.Canvas.SetTop(ellipse, p.Y - 3);
            Minimap.Children.Add(ellipse);
        }

        // Маркер текущей находки — кольцо поверх точки.
        _currentDotMarker = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Stroke = branch,
            StrokeThickness = 1.5,
        };
        Minimap.Children.Add(_currentDotMarker);

        // Индикатор видимой области — волосяная рамка без заливки.
        _viewportRect = new System.Windows.Shapes.Rectangle
        {
            Stroke = FindResource("BrushTextSecondary") as Brush ?? Brushes.Gray,
            StrokeThickness = 1,
        };
        Minimap.Children.Add(_viewportRect);

        UpdateViewportIndicator();
        if (_currentIndex >= 0 && _currentIndex < _dotPoints.Count)
            MoveCurrentMarker(_currentIndex);

        // Проверяемый факт отрисовки миниатюры в журнале приложения.
        Core.Logging.Log.Info("viewer", "Мини-карта: строк {0}, находок {1}, рядов {2}",
            _lineLengths.Count, _ranges.Count, _minimap?.Rows.Count ?? 0);
    }

    /// <summary>Клик по миниатюре → ближайшая следующая находка от этой строки.</summary>
    private void Minimap_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_minimap is null || _ranges.Count == 0) return;
        var y = e.GetPosition(Minimap).Y;

        foreach (var row in _minimap.Rows)
        {
            if (y < row.Y || y > row.Y + row.Height) continue;
            var pos = _lineOffsets[row.FirstLine];
            var idx = MatchNavigator.Next(_ranges, pos - 1); // включительно от начала строки
            SetCurrent(idx);
            return;
        }
    }

    private void Minimap_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_branch is not null) RebuildMinimap(_branch);
    }

    private void OnScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (_viewportRect is not null) UpdateViewportIndicator();
    }

    /// <summary>Волосяная рамка видимой области поверх миниатюры.</summary>
    private void UpdateViewportIndicator()
    {
        if (_viewportRect is null || _minimap is null || _innerScroll is null) return;
        var extent = _innerScroll.ExtentHeight;
        if (extent <= 0 || _innerScroll.ViewportHeight <= 0 || extent <= _innerScroll.ViewportHeight)
        {
            _viewportRect.Visibility = Visibility.Collapsed;
            return;
        }
        _viewportRect.Visibility = Visibility.Visible;
        var scale = Minimap.ActualHeight / extent;
        _viewportRect.Width = Minimap.ActualWidth;
        _viewportRect.Height = Math.Max(4, _innerScroll.ViewportHeight * scale);
        System.Windows.Controls.Canvas.SetLeft(_viewportRect, 0);
        System.Windows.Controls.Canvas.SetTop(_viewportRect, _innerScroll.VerticalOffset * scale);
    }

    private void MoveCurrentMarker(int idx)
    {
        if (_currentDotMarker is null || idx < 0 || idx >= _dotPoints.Count) return;
        var p = _dotPoints[idx];
        System.Windows.Controls.Canvas.SetLeft(_currentDotMarker, p.X - 5);
        System.Windows.Controls.Canvas.SetTop(_currentDotMarker, p.Y - 5);
    }

    /// <summary>Внутренний ScrollViewer шаблона FlowDocumentScrollViewer.</summary>
    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is System.Windows.Controls.ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    // ─── R1-18: навигация между находками ───

    private void GoNextMatch(object sender, RoutedEventArgs e) => Step(+1);

    private void GoPrevMatch(object sender, RoutedEventArgs e) => Step(-1);

    private void OnNavigationKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Down) { Step(+1); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Up) { Step(-1); e.Handled = true; }
    }

    /// <summary>Циклический шаг по находкам; правило перехода — Core.MatchNavigator.</summary>
    private void Step(int direction)
    {
        if (_ranges.Count == 0) return;
        var pos = _currentIndex >= 0 ? _ranges[_currentIndex].Start : -1;
        var idx = direction > 0
            ? MatchNavigator.Next(_ranges, pos)
            : MatchNavigator.Prev(_ranges, pos);
        SetCurrent(idx);
    }

    private void SetCurrent(int idx)
    {
        if (_currentIndex >= 0 && _currentIndex < _navRuns.Count)
            ApplyFocus(_navRuns[_currentIndex], focused: false);

        _currentIndex = idx;
        var run = _navRuns[idx];
        ApplyFocus(run, focused: true);

        // Прокрутка к абзацу с находкой (гранулярность — строка исходника).
        if (run.Parent is Paragraph para) para.BringIntoView();

        NavCounter.Text = $"{idx + 1} / {_ranges.Count}";
        MoveCurrentMarker(idx);
    }

    private void ApplyFocus(Run run, bool focused)
    {
        run.FontWeight = focused ? FontWeights.Bold : FontWeights.SemiBold;
        run.TextDecorations = focused ? _focusDecoration : _normalDecoration;
    }

    private void OpenInExplorer(object sender, RoutedEventArgs e)
    {
        if (Tag is string path && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}
