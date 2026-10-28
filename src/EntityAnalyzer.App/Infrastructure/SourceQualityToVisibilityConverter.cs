using System.Globalization;
using System.Windows;
using System.Windows.Data;
using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.App.Infrastructure;

/// <summary>
/// Source quality is mostly usable; only unknown/degraded need a visible badge.
/// This keeps the file list quiet while making the 267-tail caveat explicit.
/// </summary>
public sealed class SourceQualityToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var quality = value as string;
        return string.Equals(quality, SourceQuality.Unknown, StringComparison.Ordinal) ||
               string.Equals(quality, SourceQuality.DegradedLexical, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
