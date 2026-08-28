namespace EntityAnalyzer.Core.Analysis;

/// <summary>Узел с рассчитанной позицией на канве.</summary>
public sealed record LayoutNode(string Name, double X, double Y);

/// <summary>
/// R2-7 «Интерактивный граф кодекса»: детерминированная force-directed
/// раскладка (симуляция Фрухтермана-Рейнгольда). Начальные позиции —
/// архимедова спираль по индексу узла, без Random: два вызова на любых
/// машинах дают идентичную картину («созвездие карандашом»).
/// </summary>
public static class GraphLayout
{
    private const int Iterations = 300;
    private const double Margin = 40;      // поля канвы — имена не должны обрезаться
    private const double MinSeparation = 2; // защита от слипания (тест Nodes_do_not_overlap)

    public static IReadOnlyList<LayoutNode> Calculate(CoOccurrenceGraph graph, double width, double height)
    {
        var nodes = graph.Nodes;
        if (nodes.Count == 0) return [];
        if (nodes.Count == 1)
            return [new LayoutNode(nodes[0].Name, width / 2, height / 2)];

        var w = Math.Max(width - 2 * Margin, 10);
        var h = Math.Max(height - 2 * Margin, 10);
        var cx = width / 2;
        var cy = height / 2;
        var n = nodes.Count;

        // Идеальное расстояние: площадь на узел под корнем (Fruchterman-Reingold).
        var k = Math.Sqrt(w * h / n);

        // Спираль Архимеда: детерминированное разведение без случайности.
        var xs = new double[n];
        var ys = new double[n];
        var goldenAngle = Math.PI * (3 - Math.Sqrt(5));
        for (var i = 0; i < n; i++)
        {
            var r = k * 0.7 * Math.Sqrt(i + 0.5);
            var a = i * goldenAngle;
            xs[i] = Clamp(cx + r * Math.Cos(a), Margin, width - Margin);
            ys[i] = Clamp(cy + r * Math.Sin(a), Margin, height - Margin);
        }

        // Индекс имени → позиция (рёбра ссылаются по имени).
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++) index[nodes[i].Name] = i;

        // Рёбра в индексах.
        var edges = new List<(int A, int B)>();
        foreach (var l in graph.Links)
        {
            if (index.TryGetValue(l.Source, out var a) && index.TryGetValue(l.Target, out var b))
                edges.Add((a, b));
        }

        // Симуляция: отталкивание всех пар ~ k²/d, притяжение рёбер ~ d²/k,
        // температура гасит колебания к финалу.
        var fx = new double[n];
        var fy = new double[n];
        for (var step = 0; step < Iterations; step++)
        {
            var t = 1.0 - (double)step / Iterations;          // температура 1→0
            var maxDisp = k * 0.35 * t + 0.5;
            Array.Clear(fx);
            Array.Clear(fy);

            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++)
                {
                    var dx = xs[i] - xs[j];
                    var dy = ys[i] - ys[j];
                    var d2 = dx * dx + dy * dy;
                    if (d2 < 1e-6) { dx = 0.01; dy = 0.01; d2 = 1e-4; } // разлипание
                    var d = Math.Sqrt(d2);
                    var f = k * k / d;
                    var ux = dx / d;
                    var uy = dy / d;
                    fx[i] += ux * f; fy[i] += uy * f;
                    fx[j] -= ux * f; fy[j] -= uy * f;
                }

            foreach (var (a, b) in edges)
            {
                var dx = xs[a] - xs[b];
                var dy = ys[a] - ys[b];
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 1e-6) continue;
                var f = d * d / k;
                var ux = dx / d;
                var uy = dy / d;
                fx[a] -= ux * f; fy[a] -= uy * f;
                fx[b] += ux * f; fy[b] += uy * f;
            }

            for (var i = 0; i < n; i++)
            {
                var disp = Math.Sqrt(fx[i] * fx[i] + fy[i] * fy[i]);
                if (disp < 1e-9) continue;
                var lim = Math.Min(disp, maxDisp);
                xs[i] = Clamp(xs[i] + fx[i] / disp * lim, Margin, width - Margin);
                ys[i] = Clamp(ys[i] + fy[i] / disp * lim, Margin, height - Margin);
            }
        }

        // Финальное разлипание: если пара всё же совпала, раздвинуть чуть-чуть.
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var dx = xs[j] - xs[i];
                var dy = ys[j] - ys[i];
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d >= MinSeparation) continue;
                xs[j] = Clamp(xs[j] + 0.5, Margin, width - Margin);
                ys[j] = Clamp(ys[j] + 0.5, Margin, height - Margin);
            }

        var result = new List<LayoutNode>(n);
        for (var i = 0; i < n; i++)
            result.Add(new LayoutNode(nodes[i].Name, xs[i], ys[i]));
        return result;
    }

    private static double Clamp(double v, double min, double max)
        => v < min ? min : (v > max ? max : v);
}
