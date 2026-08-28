using System.Text.RegularExpressions;

namespace EntityAnalyzer.Core.Text;

/// <summary>
/// R1-13: удаляет строки-колонтитулы с номерами страниц («12», «— 12 —»,
/// «Стр. 42», «Страница 34 из 100», «| 5 |»). Строка считается маркером,
/// только если состоит из него целиком и короче защитного порога —
/// содержательный текст с числами внутри не затрагивается.
/// </summary>
public sealed partial class PageNumberStripper : ITextPreprocessor
{
    private const int MaxMarkerLength = 40;

    // Маркер страницы: необязательные рамки/префиксы, число, опциональный «из N».
    [GeneratedRegex(
        @"^\s*[\p{Pd}|·•]*\s*(?:стр\.?\s*|страница\s+)?(\d{1,4})(?:\s*(?:из|/)\s*\d{1,4})?\s*[\p{Pd}|·•]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();

    public string Name => "page-numbers";

    public string Process(string text, string filePath)
    {
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length <= MaxMarkerLength && MarkerRegex().IsMatch(line))
                continue; // колонтитул — строка выпадает целиком
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }
}

/// <summary>
/// R1-13: схлопывает повторы — 3 и более подряд идущих одинаковых непустых
/// строки заменяются на одну. Двойной повтор сохраняется: рефрен бывает
/// осмысленным. Типичное применение — повторяющиеся шапки страниц в fb2/txt.
/// </summary>
public sealed class RepeatedLineCollapser : ITextPreprocessor
{
    public string Name => "collapse-repeats";

    public string Process(string text, string filePath)
    {
        var lines = text.Split('\n');
        var result = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > 0)
            {
                var run = 1;
                while (i + run < lines.Length &&
                       string.Equals(lines[i + run].TrimEnd('\r'), line, StringComparison.Ordinal))
                    run++;
                if (run >= 3)
                {
                    result.Add(line);
                    i += run - 1; // пропустить остаток серии
                    continue;
                }
            }
            result.Add(line);
        }
        return string.Join('\n', result);
    }
}

/// <summary>
/// R1-13: реестр встроенных препроцессоров. Имена стабильны и входят
/// в подпись кэша — включение препроцессора осознанно инвалидирует кэш.
/// </summary>
public static class TextPreprocessorRegistry
{
    public sealed record ResolveResult(bool IsOk, IReadOnlyList<ITextPreprocessor>? Preprocessors, string? Error)
    {
        public static ResolveResult Ok(List<ITextPreprocessor> list) => new(true, list, null);
        public static ResolveResult Fail(string error) => new(false, null, error);
    }

    private static readonly Dictionary<string, Func<ITextPreprocessor>> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["page-numbers"] = () => new PageNumberStripper(),
            ["collapse-repeats"] = () => new RepeatedLineCollapser(),
        };

    /// <summary>Имена, зарегистрированные извне (скрипты R1-13); удаляются через Remove.</summary>
    private static readonly Dictionary<string, Func<ITextPreprocessor>> External =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Names => Known.Keys.Concat(External.Keys).ToList();

    /// <summary>
    /// Зарегистрировать внешний препроцессор (пользовательский скрипт).
    /// false — имя занято встроенным или другим внешним.
    /// </summary>
    public static bool Register(string name, Func<ITextPreprocessor> factory)
    {
        if (Known.ContainsKey(name) || External.ContainsKey(name)) return false;
        External[name] = factory;
        return true;
    }

    /// <summary>Убрать внешнюю регистрацию. Встроенные имена защищены.</summary>
    public static bool Remove(string name)
    {
        if (Known.ContainsKey(name)) return false;
        return External.Remove(name);
    }

    public static ResolveResult Resolve(IReadOnlyList<string> names)
    {
        var list = new List<ITextPreprocessor>(names.Count);
        foreach (var name in names)
        {
            if (!Known.TryGetValue(name.Trim(), out var factory) &&
                !External.TryGetValue(name.Trim(), out factory))
                return ResolveResult.Fail(
                    $"Неизвестный препроцессор «{name}». Доступны: {string.Join(", ", Names.OrderBy(k => k, StringComparer.Ordinal))}");
            list.Add(factory());
        }
        return ResolveResult.Ok(list);
    }
}
