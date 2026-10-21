namespace EntityAnalyzer.Core.Text;

/// <summary>
/// Элемент палитры для ранжирования: заголовок, полный текст поиска и
/// приоритет. Интерфейс намеренно не зависит от WPF, поэтому правила
/// ранжирования проверяются в Core-тестах.
/// </summary>
public interface IPaletteSearchItem
{
    string Title { get; }
    string SearchText { get; }
    int Priority { get; }
}

/// <summary>
/// R1-7 «Command Palette»: ранжирование элементов палитры.
/// Совпадение в заголовке важнее совпадения в длинном контексте: иначе
/// файлы с коротким путём вытесняют упоминания сущности из первых строк.
/// Внутри заголовочных совпадений учитывается приоритет типа элемента;
/// для контекстных совпадений приоритет не применяется.
/// </summary>
public static class PaletteRanker
{
    /// <summary>
    /// Поднять недавние элементы наверх в порядке истории. Неизвестные ключи
    /// игнорируются, остальные элементы сохраняют исходный порядок. Сравнение
    /// ключей — без учёта регистра, чтобы пути Windows не дублировались.
    /// </summary>
    public static IReadOnlyList<T> OrderByRecent<T>(
        IReadOnlyList<T> items,
        Func<T, string?> keySelector,
        IReadOnlyList<string>? recentKeys)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keySelector);
        if (recentKeys is not { Count: > 0 }) return items;

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < recentKeys.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(recentKeys[i]))
                rank.TryAdd(recentKeys[i], i);
        }
        if (rank.Count == 0) return items;

        // OrderBy стабилен: недавние идут по свежести, остальные — как пришли.
        return
        [
            .. items.OrderBy(item =>
                rank.TryGetValue(keySelector(item) ?? "", out var index)
                    ? index
                    : recentKeys.Count)
        ];
    }

    /// <summary>
    /// Возвращает элементы в порядке палитры. Пустой запрос сохраняет
    /// исходный порядок без ограничения; непустой — не более
    /// <paramref name="limit"/> лучших совпадений.
    /// </summary>
    public static IReadOnlyList<T> Rank<T>(
        IReadOnlyList<T> items,
        string query,
        int limit = 200)
        where T : IPaletteSearchItem
    {
        ArgumentNullException.ThrowIfNull(items);

        if (string.IsNullOrWhiteSpace(query))
            return items;

        if (limit <= 0 || items.Count == 0)
            return [];

        var q = query.Trim();
        var scored = new List<(T Item, int Score, int Order, bool TitleMatch)>(items.Count);

        // Нельзя опираться только на FuzzyFilter.Rank: у карточки кодекса
        // SearchText содержит сотни алиасов (несколько килобайт), и штраф
        // за длину делает оценку отрицательной. Такая карточка выпадала из
        // палитры, хотя её заголовок совпадал с запросом. Поэтому кандидат
        // включается при совпадении заголовка ИЛИ контекста.
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var searchScore = FuzzyFilter.Score(item.SearchText, q);
            var titleScore = FuzzyFilter.Score(item.Title, q);
            if (searchScore <= 0 && titleScore <= 0) continue;

            var titleMatch = titleScore > 0;
            var rankScore = titleMatch
                ? titleScore + item.Priority
                : searchScore;
            scored.Add((item, rankScore, i, titleMatch));
        }

        return
        [
            .. scored
                .OrderByDescending(x => x.TitleMatch)
                .ThenByDescending(x => x.Score)
                .ThenBy(x => x.Item.Title, StringComparer.Ordinal)
                .ThenBy(x => x.Order)
                .Take(limit)
                .Select(x => x.Item)
        ];
    }
}
