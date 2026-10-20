using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EntityAnalyzer.App.Behaviors;

/// <summary>
/// Оформляет контекст цитаты: вхождения слова-триггера подчёркнуты тонкой
/// линией семантического цвета ветви, вхождения слов темы (R3-1, item 52) —
/// точечной линией цвета темы (без заливки и фона — академическая
/// типографика). Используется карточками упоминаний и списком отрывков.
/// </summary>
public static class HighlightBehavior
{
    public static readonly DependencyProperty ContextProperty =
        DependencyProperty.RegisterAttached("Context", typeof(string), typeof(HighlightBehavior),
            new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty MarkProperty =
        DependencyProperty.RegisterAttached("Mark", typeof(string), typeof(HighlightBehavior),
            new PropertyMetadata(string.Empty, OnChanged));

    /// <summary>Кисть линии-подчёркивания (одна из пяти ветвей).</summary>
    public static readonly DependencyProperty MarkBrushProperty =
        DependencyProperty.RegisterAttached("MarkBrush", typeof(Brush), typeof(HighlightBehavior),
            new PropertyMetadata(null, OnChanged));

    /// <summary>Слова темы через «|» (R3-1): подчёркиваются точечной линией.</summary>
    public static readonly DependencyProperty ThemeMarksProperty =
        DependencyProperty.RegisterAttached("ThemeMarks", typeof(string), typeof(HighlightBehavior),
            new PropertyMetadata(string.Empty, OnChanged));

    /// <summary>Кисть точечной линии слов темы.</summary>
    public static readonly DependencyProperty ThemeBrushProperty =
        DependencyProperty.RegisterAttached("ThemeBrush", typeof(Brush), typeof(HighlightBehavior),
            new PropertyMetadata(null, OnChanged));

    public static string GetContext(DependencyObject o) => (string)o.GetValue(ContextProperty);
    public static void SetContext(DependencyObject o, string v) => o.SetValue(ContextProperty, v);
    public static string GetMark(DependencyObject o) => (string)o.GetValue(MarkProperty);
    public static void SetMark(DependencyObject o, string v) => o.SetValue(MarkProperty, v);
    public static Brush? GetMarkBrush(DependencyObject o) => (Brush?)o.GetValue(MarkBrushProperty);
    public static void SetMarkBrush(DependencyObject o, Brush? v) => o.SetValue(MarkBrushProperty, v);
    public static string GetThemeMarks(DependencyObject o) => (string)o.GetValue(ThemeMarksProperty);
    public static void SetThemeMarks(DependencyObject o, string v) => o.SetValue(ThemeMarksProperty, v);
    public static Brush? GetThemeBrush(DependencyObject o) => (Brush?)o.GetValue(ThemeBrushProperty);
    public static void SetThemeBrush(DependencyObject o, Brush? v) => o.SetValue(ThemeBrushProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();

        var context = GetContext(d) ?? string.Empty;
        if (context.Length == 0) return;

        var mark = GetMark(d) ?? string.Empty;
        if (mark.Length == 0 || mark.StartsWith('[')) // fuzzy tags contain the real window inside brackets
            mark = ExtractWindow(mark);

        var spans = new List<(int Start, int End, bool Entity)>();
        if (mark.Length > 0) Collect(spans, context, mark, entity: true);
        foreach (var raw in (GetThemeMarks(d) ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim();
            if (word.Length > 0) Collect(spans, context, word, entity: false);
        }

        if (spans.Count == 0)
        {
            tb.Inlines.Add(context);
            return;
        }

        // Перекрытия: слово-триггер сущности приоритетнее слов темы.
        spans.Sort((a, b) => a.Start != b.Start
            ? a.Start.CompareTo(b.Start)
            : (a.Entity == b.Entity ? 0 : a.Entity ? -1 : 1));
        var clean = new List<(int Start, int End, bool Entity)>(spans.Count);
        foreach (var s in spans)
            if (clean.Count == 0 || s.Start >= clean[^1].End)
                clean.Add(s);

        var entityDecor = BuildUnderline(GetMarkBrush(d), dotted: false);
        var themeDecor = BuildUnderline(GetThemeBrush(d), dotted: true);

        var idx = 0;
        foreach (var (start, end, entity) in clean)
        {
            if (start > idx) tb.Inlines.Add(context[idx..start]);
            tb.Inlines.Add(new Run(context[start..end])
            {
                TextDecorations = entity ? entityDecor : themeDecor,
            });
            idx = end;
        }
        if (idx < context.Length) tb.Inlines.Add(context[idx..]);
    }

    private static void Collect(List<(int Start, int End, bool Entity)> spans, string context, string needle, bool entity)
    {
        var idx = 0;
        while (idx < context.Length)
        {
            var hit = context.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;
            spans.Add((hit, hit + needle.Length, entity));
            idx = hit + needle.Length;
        }
    }

    /// <summary>Подчёркивание 1px кистью ветви; без кисти — графитовая линия; точечный пунктир — слова темы.</summary>
    private static TextDecorationCollection BuildUnderline(Brush? brush, bool dotted)
    {
        brush ??= Application.Current?.TryFindResource("BrushTextPrimary") as Brush
                  ?? Brushes.DimGray;
        var pen = new Pen(brush, 1d);
        if (dotted) pen.DashStyle = DashStyles.Dot;
        return new TextDecorationCollection(
        [
            new TextDecoration
            {
                Location = TextDecorationLocation.Underline,
                Pen = pen,
                PenOffset = 2d,
            },
        ]);
    }

    /// <summary>«слово [fuzzy:89%]» → «слово».</summary>
    private static string ExtractWindow(string variant)
    {
        var b = variant.IndexOf(" [fuzzy:", StringComparison.Ordinal);
        return b > 0 ? variant[..b] : variant.Trim();
    }
}
