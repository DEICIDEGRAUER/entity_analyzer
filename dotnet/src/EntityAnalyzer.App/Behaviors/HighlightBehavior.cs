using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EntityAnalyzer.App.Behaviors;

/// <summary>
/// Оформляет контекст цитаты: каждое вхождение слова-триггера подчёркнуто
/// тонкой линией семантического цвета ветви (без заливки и фона —
/// академическая типографика). Используется карточками упоминаний.
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

    public static string GetContext(DependencyObject o) => (string)o.GetValue(ContextProperty);
    public static void SetContext(DependencyObject o, string v) => o.SetValue(ContextProperty, v);
    public static string GetMark(DependencyObject o) => (string)o.GetValue(MarkProperty);
    public static void SetMark(DependencyObject o, string v) => o.SetValue(MarkProperty, v);
    public static Brush? GetMarkBrush(DependencyObject o) => (Brush?)o.GetValue(MarkBrushProperty);
    public static void SetMarkBrush(DependencyObject o, Brush? v) => o.SetValue(MarkBrushProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();

        var context = GetContext(d) ?? string.Empty;
        var mark = GetMark(d) ?? string.Empty;
        if (context.Length == 0) return;

        if (mark.Length == 0 || mark.StartsWith('[')) // fuzzy tags contain the real window inside brackets
        {
            mark = ExtractWindow(mark);
            if (mark.Length == 0) { tb.Inlines.Add(context); return; }
        }

        var underline = BuildUnderline(GetMarkBrush(d));

        var idx = 0;
        while (idx < context.Length)
        {
            var hit = context.IndexOf(mark, idx, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;
            if (hit > idx) tb.Inlines.Add(context[idx..hit]);

            // Слово-триггер: обычный текст + тонкая цветная линия снизу.
            var run = new Run(context.Substring(hit, mark.Length))
            {
                TextDecorations = underline,
            };
            tb.Inlines.Add(run);

            idx = hit + mark.Length;
        }
        if (idx < context.Length) tb.Inlines.Add(context[idx..]);
    }

    /// <summary>Подчёркивание 1px кистью ветви; без кисти — графитовая линия.</summary>
    private static TextDecorationCollection BuildUnderline(Brush? brush)
    {
        brush ??= Application.Current?.TryFindResource("BrushTextPrimary") as Brush
                  ?? Brushes.DimGray;
        return new TextDecorationCollection(
        [
            new TextDecoration
            {
                Location = TextDecorationLocation.Underline,
                Pen = new Pen(brush, 1d),
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
