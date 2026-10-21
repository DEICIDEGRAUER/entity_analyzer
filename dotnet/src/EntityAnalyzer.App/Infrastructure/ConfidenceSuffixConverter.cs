using System.Globalization;
using System.Windows.Data;
using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.App.Infrastructure;

/// <summary>
/// R1-19: упоминание → суффикс достоверности для строки интерфейса.
/// Делегирует правило чистому ConfidenceBadge (ядро, покрыто тестами);
/// высокая достоверность возвращает пустую строку — не шумит.
/// </summary>
public sealed class ConfidenceSuffixConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Mention mention ? ConfidenceBadge.From(mention).ToSuffix() : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
