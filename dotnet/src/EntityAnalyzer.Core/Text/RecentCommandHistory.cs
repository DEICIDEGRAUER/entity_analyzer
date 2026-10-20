namespace EntityAnalyzer.Core.Text;

/// <summary>
/// История недавно выполненных команд палитры: хранит устойчивые ключи,
/// поднимает повторно выполненную команду наверх, убирает дубликаты и
/// ограничивает длину. Вынесена в ядро, чтобы порядок и лимит проверялись
/// без запуска WPF.
/// </summary>
public static class RecentCommandHistory
{
    public const int DefaultLimit = 8;

    /// <summary>
    /// Вернуть новую историю с <paramref name="key"/> в начале. Пустой ключ
    /// только нормализует старый список; <paramref name="limit"/> = 0 очищает.
    /// </summary>
    public static List<string> Touch(
        IEnumerable<string>? recent, string? key, int limit = DefaultLimit)
    {
        limit = Math.Max(0, limit);
        var result = new List<string>(Math.Min(limit, (recent?.Count() ?? 0) + 1));
        if (limit == 0) return result;

        if (!string.IsNullOrWhiteSpace(key))
            result.Add(key.Trim());

        foreach (var item in recent ?? [])
        {
            if (result.Count >= limit) break;
            if (string.IsNullOrWhiteSpace(item)) continue;
            var normalized = item.Trim();
            if (result.Contains(normalized, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(normalized);
        }
        return result;
    }
}
