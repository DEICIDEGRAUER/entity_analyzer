using System.Windows;
using System.Windows.Media;

namespace EntityAnalyzer.App.Services;

/// <summary>
/// Item 14: менеджер оформления. Светлая тема — «Themes/Academic.xaml»
/// (стили + палитра). Тёмная — словарь «Themes/Dark.xaml», накладываемый
/// поверх и перекрывающий только цветовые кисти. Поверх активной темы
/// ложатся пользовательские цвета ветвей из настроек (<see cref="AppSettings"/>).
/// </summary>
public static class ThemeManager
{
    public const string LightKey = "light";
    public const string DarkKey = "dark";

    /// <summary>Список тем для выпадающего списка в настройках.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ThemeItems { get; } =
    [
        new(LightKey, "Светлая («Академическая»)"),
        new(DarkKey, "Тёмная («Северный ветер»)"),
    ];

    private static ResourceDictionary? _themeOverlay;
    private static ResourceDictionary? _branchOverlay;

    /// <summary>Нормализовать ключ темы из настроек (неизвестные → светлая).</summary>
    public static string Normalize(string? key) =>
        string.Equals(key, DarkKey, StringComparison.OrdinalIgnoreCase) ? DarkKey : LightKey;

    /// <summary>
    /// Применить тему и пользовательские цвета ветвей. Вызывается при старте
    /// и при каждой правке в настройках — приложение перерисовывается сразу.
    /// </summary>
    public static void Apply(AppSettings settings)
    {
        var app = Application.Current;
        if (app is null) return;

        var dict = app.Resources.MergedDictionaries;

        // 1) Тема: снять прошлый оверлей, добавить новый при «тёмной».
        if (_themeOverlay is not null)
        {
            dict.Remove(_themeOverlay);
            _themeOverlay = null;
        }
        if (Normalize(settings.ThemeKey) == DarkKey)
        {
            _themeOverlay = new ResourceDictionary
            {
                // Имя сборки — AssemblyName=EntityAnalyzer (см. csproj), не namespace.
                Source = new Uri("/EntityAnalyzer;component/Themes/Dark.xaml", UriKind.Relative),
            };
            dict.Add(_themeOverlay);
        }

        // 2) Персональные цвета ветвей — поверх темы.
        if (_branchOverlay is not null)
        {
            dict.Remove(_branchOverlay);
            _branchOverlay = null;
        }
        string?[] colors =
        [
            settings.Branch1Color, settings.Branch2Color, settings.Branch3Color,
            settings.Branch4Color, settings.Branch5Color,
        ];
        var branch = new ResourceDictionary();
        for (var i = 0; i < colors.Length; i++)
            if (ParseColor(colors[i]) is { } c)
                branch["BrushBranch" + (i + 1)] = new SolidColorBrush(c);
        if (branch.Count > 0)
        {
            _branchOverlay = branch;
            dict.Add(branch);
        }

        // 3) Кисти ветвей кэшируются резолвером — сбросить кэш.
        BranchResolver.InvalidatePalette();
    }

    /// <summary>
    /// Пользовательский цвет («#RRGGBB», «#AARRGGBB» или именованный).
    /// null — строка пуста или некорректна (тогда действует цвет темы).
    /// </summary>
    public static Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return ColorConverter.ConvertFromString(text.Trim()) is Color c ? c : null;
        }
        catch
        {
            return null; // свободный ввод: некорректная строка не ломает настройки
        }
    }

    /// <summary>Строка «#RRGGBB» для кисти ветви из активных ресурсов (превью в настройках).</summary>
    public static string DescribeBranchColor(int index)
    {
        var brush = Application.Current?.TryFindResource("BrushBranch" + index) as SolidColorBrush;
        return brush is null ? "" : $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
    }
}
