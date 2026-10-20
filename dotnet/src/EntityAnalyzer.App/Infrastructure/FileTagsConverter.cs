using System.Globalization;
using System.Windows.Data;

namespace EntityAnalyzer.App.Infrastructure;

/// <summary>
/// Группа 3 (item 11): реестр автотегов файлов для списка «Результатов» —
/// страница кладёт карту «путь → строка тегов», конвертер достаёт значение
/// по FullPath. Карта заменяется целиком при каждой новой сессии анализа.
/// </summary>
public static class FileTagsRegistry
{
    private static volatile IReadOnlyDictionary<string, string> _map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static void Set(IReadOnlyDictionary<string, string> map) => _map = map;

    public static string TextFor(string? fullPath) =>
        fullPath is not null && _map.TryGetValue(fullPath, out var text) ? text : "";
}

/// <summary>Конвертер FullPath → строка автотегов файла (item 11).</summary>
public sealed class FileTagsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => FileTagsRegistry.TextFor(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
