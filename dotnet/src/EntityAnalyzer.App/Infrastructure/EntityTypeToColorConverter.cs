using System.Globalization;
using System.Windows.Data;
using EntityAnalyzer.App.Services;

namespace EntityAnalyzer.App.Infrastructure;

/// <summary>
/// Тип/группа/имя сущности → кисть одной из пяти семантических ветвей.
/// Применяется ТОЛЬКО к точкам-маркерам 6×6 и подчёркиванию слов-триггеров
/// (см. спецификацию цветового кодирования).
/// </summary>
public sealed class EntityTypeToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BranchResolver.Resolve(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
