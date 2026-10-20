using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EntityAnalyzer.App.Infrastructure;

/// <summary>
/// Конвертер для звёздочки цитатника: true → золотой/акцентный цвет, false → серый.
/// </summary>
public sealed class StarToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isStarred = value is true;
        var key = isStarred ? "BrushBranch3" : "BrushTextDisabled";
        return System.Windows.Application.Current?.TryFindResource(key) as Brush
            ?? (isStarred ? Brushes.Gold : Brushes.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}