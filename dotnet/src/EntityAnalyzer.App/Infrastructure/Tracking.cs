using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EntityAnalyzer.App.Infrastructure;

/// <summary>
/// Letter-spacing для WPF (в платформе отсутствует): вставляет тонкие
/// пробелы U+2009 между символами капс-заголовков (Ui-Header,
/// LetterSpacing +1px по спецификации).
///
/// Безопасность: применение откладывается за пределы прохода стилей
/// (Dispatcher.Loaded), собственная запись текста помечается флагом Busy,
/// поэтому рекурсия «изменение → обработчик → изменение» невозможна.
/// Исходная строка хранится в RawText и восстанавливается при Spacing=0.
/// </summary>
public static class Tracking
{
    private const char ThinSpace = '\u2009';

    private static readonly DependencyProperty RawTextProperty =
        DependencyProperty.RegisterAttached("RawText", typeof(string), typeof(Tracking),
            new PropertyMetadata(string.Empty));

    private static readonly DependencyProperty BusyProperty =
        DependencyProperty.RegisterAttached("Busy", typeof(bool), typeof(Tracking),
            new PropertyMetadata(false));

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(Tracking),
            new PropertyMetadata(false));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.RegisterAttached("Spacing", typeof(double), typeof(Tracking),
            new PropertyMetadata(0d, OnSpacingChanged));

    public static double GetSpacing(DependencyObject o) => (double)o.GetValue(SpacingProperty);
    public static void SetSpacing(DependencyObject o, double v) => o.SetValue(SpacingProperty, v);

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        if (!(bool)tb.GetValue(HookedProperty))
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            descriptor?.AddValueChanged(tb, OnTextChanged);
            tb.SetValue(HookedProperty, true);
        }

        tb.SetValue(RawTextProperty, tb.Text);
        Schedule(tb);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock tb) return;
        if ((bool)tb.GetValue(BusyProperty)) return; // это наша собственная запись

        tb.SetValue(RawTextProperty, tb.Text);       // внешний текст — новый оригинал
        Schedule(tb);
    }

    /// <summary>Вне прохода стилей и измерения — чтобы не ломать XAML-загрузку.</summary>
    private static void Schedule(TextBlock tb) =>
        tb.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Apply(tb)));

    private static void Apply(TextBlock tb)
    {
        // Плавающее исключение при старте окна (QA BUG-009): любой сбой
        // трекинга не должен ломать отрисовку — fallback на исходный текст.
        try
        {
            ApplyCore(tb);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Tracking fallback: " + ex.Message);
            try { tb.Text = (string)tb.GetValue(RawTextProperty); } catch { /* крайний случай — оставляем как есть */ }
        }
    }

    private static void ApplyCore(TextBlock tb)
    {
        if ((bool)tb.GetValue(BusyProperty)) return;

        var raw = (string)tb.GetValue(RawTextProperty);
        var target = GetSpacing(tb) > 0 && raw.Length > 0 ? Space(raw) : raw;
        if (tb.Text == target) return;

        tb.SetValue(BusyProperty, true);
        try { tb.Text = target; }
        finally { tb.SetValue(BusyProperty, false); }
    }

    private static string Space(string s)
    {
        if (s.Length <= 1) return s;
        var sb = new System.Text.StringBuilder(s.Length * 2);
        sb.Append(s[0]);
        for (var i = 1; i < s.Length; i++)
        {
            sb.Append(ThinSpace);
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
