using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EntityAnalyzer.App.Infrastructure;

/// <summary>value/max*width → pixel width for hand-rolled progress bars.</summary>
public sealed class RatioConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3 ||
            !double.TryParse(values[0]?.ToString(), out var value) ||
            !double.TryParse(values[1]?.ToString(), out var max) ||
            !double.TryParse(values[2]?.ToString(), out var width) || max <= 0)
            return 0d;

        var ratio = Math.Clamp(value / max, 0, 1);
        return Math.Max(0, ratio * width);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null → false (и наоборот), чтобы блокировать редактор без выбранной сущности.</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Empty/whitespace string → Collapsed; anything else → Visible (readiness hints).</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true → Collapsed, false → Visible (скрытие блоков при пустом состоянии).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Многострочный текст (алиасы) → строка «a, b, c» для метаданных статьи.
/// Параметр-число ограничивает количество показанных алиасов: «a, b, … +N».
/// </summary>
public sealed class MultilineToInlineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var items = (value as string ?? "")
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parameter is string s && int.TryParse(s, out var max) && max > 0 && items.Length > max)
            return string.Join(", ", items.Take(max)) + $", … +{items.Length - max}";

        return string.Join(", ", items);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool → текст кнопки обособленного скана паттернов:
/// true — «Идёт скан…» (кнопка неактивна), false — «Сканировать паттерны».
/// </summary>
public sealed class BoolToScanTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Идёт скан…" : "Сканировать паттерны";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
