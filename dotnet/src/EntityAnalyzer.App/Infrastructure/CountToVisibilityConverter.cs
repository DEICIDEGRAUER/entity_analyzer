using System.Globalization;
using System.Windows.Data;

namespace EntityAnalyzer.App.Infrastructure;

/// <summary>
/// Конвертер: число > 0 → Visible, иначе Collapsed.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0
        };
        return count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}