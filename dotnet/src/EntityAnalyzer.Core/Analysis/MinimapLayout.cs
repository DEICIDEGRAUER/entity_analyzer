namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// R2-8 «Мини-карта файла в вычитке»: чистая геометрия вертикальной миниатюры.
/// Ядро считает раскладку (строки → пиксельные ряды, находки → координаты),
/// окно файла только рисует готовые фигуры на Canvas — WPF сюда не протекает.
///
/// Правила раскладки:
/// • если высота позволяет (≥1px на строку) — ряд на каждую строку;
/// • иначе строки группируются в ряды по groupSize = ceil(lines/height),
///   ширина ряда — максимум длины строки в группе;
/// • точка находки центрируется в ряду своей строки, X — доля колонки
///   от самой длинной строки (умножается на ширину канвы при отрисовке).
/// </summary>
public static class MinimapLayout
{
    /// <summary>Пиксельный ряд миниатюры: диапазон строк + прямоугольник.</summary>
    public sealed record Row(int FirstLine, int LastLine, double Y, double Height, double WidthFraction);

    /// <summary>Точка находки: индекс в списке диапазонов окна + координаты.</summary>
    public sealed record Dot(int RangeIndex, double Y, double XFraction);

    public sealed record Layout(IReadOnlyList<Row> Rows, IReadOnlyList<Dot> Dots);

    /// <param name="lineLengths">Длина каждой строки документа в символах.</param>
    /// <param name="dots">(строка, колонка) каждой находки; порядок = порядок RangeIndex.</param>
    /// <param name="height">Высота канвы мини-карты в пикселях.</param>
    public static Layout Calculate(
        IReadOnlyList<int> lineLengths,
        IReadOnlyList<(int Line, int Column)> dots,
        double height)
    {
        var rows = new List<Row>();
        var outDots = new List<Dot>();
        if (lineLengths.Count == 0 || height <= 0)
            return new Layout(rows, outDots);

        // Защита от деления на ноль для пустых строк.
        var maxLen = 1;
        foreach (var len in lineLengths)
            if (len > maxLen) maxLen = len;

        var rowH = height / lineLengths.Count;
        if (rowH >= 1.0)
        {
            // Ряд на каждую строку.
            for (var i = 0; i < lineLengths.Count; i++)
                rows.Add(new Row(i, i, i * rowH, rowH, lineLengths[i] / (double)maxLen));
        }
        else
        {
            // Агрегация: группы строк сливаются в один пиксельный ряд.
            var groupSize = (int)Math.Ceiling(lineLengths.Count / height);
            var rowCount = (int)Math.Ceiling(lineLengths.Count / (double)groupSize);
            var groupH = height / rowCount;
            for (var g = 0; g < rowCount; g++)
            {
                var first = g * groupSize;
                var last = Math.Min(first + groupSize - 1, lineLengths.Count - 1);
                var maxInGroup = 0;
                for (var i = first; i <= last; i++)
                    if (lineLengths[i] > maxInGroup) maxInGroup = lineLengths[i];
                rows.Add(new Row(first, last, g * groupH, groupH, maxInGroup / (double)maxLen));
            }
        }

        for (var d = 0; d < dots.Count; d++)
        {
            var (line, col) = dots[d];
            if (line < 0 || line >= lineLengths.Count) continue;
            var row = FindRow(rows, line);
            if (row is null) continue;
            outDots.Add(new Dot(d, row.Y + row.Height / 2.0, col / (double)maxLen));
        }

        return new Layout(rows, outDots);
    }

    private static Row? FindRow(IReadOnlyList<Row> rows, int line)
    {
        foreach (var r in rows)
            if (line >= r.FirstLine && line <= r.LastLine)
                return r;
        return null;
    }
}
