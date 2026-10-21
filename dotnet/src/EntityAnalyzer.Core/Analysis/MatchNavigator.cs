namespace EntityAnalyzer.Core.Analysis;

/// <summary>Диапазон совпадения в тексте — полуинтервал [Start, End).</summary>
public sealed record MatchRange(int Start, int End);

/// <summary>
/// R1-18 «Режим вычитки»: циклическая навигация по находкам относительно
/// позиции в тексте (не индекса) — окно файла передаёт начало текущего
/// подсвеченного прогона, -1 = ничего не выбрано. Диапазоны предполагаются
/// отсортированными по Start и непересекающимися, как после построения
/// подсветки в окне файла; чистые функции без состояния — тестируются ядром.
/// </summary>
public static class MatchNavigator
{
    /// <summary>
    /// Первое совпадение с началом строго правее fromPosition;
    /// если такого нет — цикл к первому. -1 для пустого списка.
    /// </summary>
    public static int Next(IReadOnlyList<MatchRange> sortedRanges, int fromPosition)
    {
        for (var i = 0; i < sortedRanges.Count; i++)
            if (sortedRanges[i].Start > fromPosition)
                return i;
        return sortedRanges.Count == 0 ? -1 : 0;
    }

    /// <summary>
    /// Последнее совпадение с началом строго левее fromPosition;
    /// если такого нет — цикл к последнему. -1 для пустого списка.
    /// </summary>
    public static int Prev(IReadOnlyList<MatchRange> sortedRanges, int fromPosition)
    {
        for (var i = sortedRanges.Count - 1; i >= 0; i--)
            if (sortedRanges[i].Start < fromPosition)
                return i;
        return sortedRanges.Count == 0 ? -1 : sortedRanges.Count - 1;
    }
}
