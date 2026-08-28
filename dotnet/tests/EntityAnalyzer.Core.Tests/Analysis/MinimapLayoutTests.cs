using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

// ═══════════════ R2-8: геометрия мини-карты файла (без WPF) ═══════════════
// Мини-карта — вертикальная миниатюра строк справа от текста с точками находок.
// Ядро считает раскладку (строки → пиксельные ряды, точки → координаты),
// App только рисует готовые прямоугольники и эллипсы на Canvas.
// Контракт: если высота позволяет — ряд на каждую строку; иначе строки
// группируются в ряды по pixelRows, ширина ряда = максимум длины группы.

public class MinimapLayoutTests
{
    [Fact]
    public void Empty_input_yields_empty_layout()
    {
        var layout = MinimapLayout.Calculate([], [], height: 100);
        Assert.Empty(layout.Rows);
        Assert.Empty(layout.Dots);
    }

    [Fact]
    public void One_row_per_line_when_height_allows()
    {
        // 3 строки по 20px: ряды Y=0/20/40, высота ряда 20.
        var layout = MinimapLayout.Calculate([10, 20, 30], [], height: 60);

        Assert.Equal(3, layout.Rows.Count);
        Assert.Equal((0, 0), (layout.Rows[0].FirstLine, layout.Rows[0].LastLine));
        Assert.Equal(0, layout.Rows[0].Y, 5);
        Assert.Equal(20, layout.Rows[1].Y, 5);
        Assert.Equal(40, layout.Rows[2].Y, 5);
        Assert.Equal(20, layout.Rows[0].Height, 5);
        // Ширина — доля от самой длинной строки.
        Assert.Equal(10 / 30.0, layout.Rows[0].WidthFraction, 5);
        Assert.Equal(1.0, layout.Rows[2].WidthFraction, 5);
    }

    [Fact]
    public void Huge_document_is_aggregated_into_pixel_rows()
    {
        const int lines = 10_000;
        var lengths = Enumerable.Repeat(50, lines).ToList();

        var layout = MinimapLayout.Calculate(lengths, [], height: 400);

        // groupSize = ceil(10000/400) = 25 → ровно 400 рядов.
        Assert.True(layout.Rows.Count <= 400);
        Assert.Equal(400, layout.Rows.Count);
        Assert.Equal(0, layout.Rows[0].FirstLine);
        Assert.Equal(24, layout.Rows[0].LastLine);
        Assert.Equal(lines - 1, layout.Rows[^1].LastLine);
        // Покрытие непрерывное: каждый следующий ряд начинается со следующей строки.
        for (var i = 1; i < layout.Rows.Count; i++)
            Assert.Equal(layout.Rows[i - 1].LastLine + 1, layout.Rows[i].FirstLine);
    }

    [Fact]
    public void Dot_is_centered_on_its_row_and_scaled_by_column()
    {
        // 4 строки по 20px; точка на строке 2, колонка 5 из 10.
        var layout = MinimapLayout.Calculate([10, 10, 10, 10], [(Line: 2, Column: 5)], height: 80);

        var dot = Assert.Single(layout.Dots);
        Assert.Equal(0, dot.RangeIndex);
        Assert.Equal(2 * 20 + 10, dot.Y, 5);      // центр ряда строки 2
        Assert.Equal(5 / 10.0, dot.XFraction, 5); // доля колонки от максимальной длины
    }

    [Fact]
    public void Dot_lands_in_correct_group_row_on_aggregated_map()
    {
        // 100 строк в высоту 10 → группы по 10 строк; строка 35 → ряд 3.
        var lengths = Enumerable.Repeat(8, 100).ToList();
        var layout = MinimapLayout.Calculate(lengths, [(Line: 35, Column: 2)], height: 10);

        var dot = Assert.Single(layout.Dots);
        Assert.Equal(30, layout.Rows[3].FirstLine); // группа 3 = строки 30–39
        Assert.True(layout.Rows[3].LastLine >= 35);
        Assert.Equal(3 * 1.0 + 0.5, dot.Y, 5); // центр ряда: 3*1 + 0.5
    }

    [Fact]
    public void All_empty_lines_are_guarded_against_zero_division()
    {
        var layout = MinimapLayout.Calculate([0, 0], [(Line: 1, Column: 0)], height: 20);

        Assert.Equal(2, layout.Rows.Count);
        Assert.Equal(0.0, layout.Rows[0].WidthFraction, 5);
        Assert.Equal(0.0, Assert.Single(layout.Dots).XFraction, 5);
    }
}
