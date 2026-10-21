using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace EntityAnalyzer.App.Infrastructure;

/// <summary>
/// R1-6 «Heat map корпуса»: конвертеры плиток. Индекс ветви → кисть палитры
/// (те же пять цветов, что и везде); объём текста → размер плитки (sqrt-
/// масштаб); плотность → насыщенность (прозрачность заливки).
/// </summary>
public sealed class BranchIndexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var index = value is int i ? Math.Clamp(i, 0, 4) : 0;
        var key = $"BrushBranch{index + 1}";
        return System.Windows.Application.Current?.TryFindResource(key) as Brush
            ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>Объём файла (символы) → сторона квадратной плитки, sqrt-масштаб.</summary>
public sealed class CharsToTileSizeConverter : IValueConverter
{
    private const double Min = 56;
    private const double Max = 152;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var chars = value is long l ? l : 0;
        // 500 символов → минимум; 250k → максимум; между — корень.
        var t = Math.Sqrt(Math.Max(0, chars) / 250000.0);
        var size = Min + (Max - Min) * Math.Min(1.0, t);
        return Math.Round(size / 8) * 8; // сетка 8px
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>Плотность упоминаний на 10k символов → непрозрачность заливки 0.3..0.85.</summary>
public sealed class DensityToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var density = value is double d ? d : 0;
        return Math.Clamp(0.3 + density / 200.0, 0.3, 0.85);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
