using System.Globalization;
using System.Windows.Data;
using EntityAnalyzer.Core.Themes;

namespace EntityAnalyzer.App.Infrastructure;

/// <summary>
/// R3-1 (item 52): слова темы, найденные в контексте цитаты, через «|» —
/// для точечной подсветки в <see cref="Behaviors.HighlightBehavior"/>.
/// Пустой фильтр темы или отсутствие вхождений дают пустую строку.
/// </summary>
public sealed class ThemeMarksConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string context || context.Length == 0)
            return "";
        if (values[1] is not IReadOnlyList<string> words || words.Count == 0)
            return "";
        return string.Join("|", ThemeMatcher.MatchWords(context, words));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
