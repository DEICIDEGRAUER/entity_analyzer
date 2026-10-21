using System.Text.RegularExpressions;
using Log = EntityAnalyzer.Core.Logging.Log;

namespace EntityAnalyzer.Core.Filtering;

/// <summary>
/// Пользовательские regex-исключения: контекст, совпавший хотя бы с одним
/// паттерном, отбрасывается. Невалидные паттерны пропускаются молча
/// (факт фиксируется в логе), чтобы одна опечатка не ломала анализ.
/// </summary>
public sealed class UserExclusionFilter
{
    private readonly List<Regex> _regexes = [];

    public UserExclusionFilter(IReadOnlyList<string> patterns)
    {
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                _regexes.Add(new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200)));
            }
            catch (ArgumentException ex)
            {
                Log.Warn("user-filters", $"Regex «{p}» невалиден и пропущен: {ex.Message}");
            }
        }
    }

    public int PatternCount => _regexes.Count;

    public bool IsExcluded(string context)
    {
        if (_regexes.Count == 0 || string.IsNullOrEmpty(context)) return false;
        foreach (var re in _regexes)
        {
            try
            {
                if (re.IsMatch(context)) return true;
            }
            catch (RegexMatchTimeoutException) { /* слишком жадный паттерн — пропустить */ }
        }
        return false;
    }
}
