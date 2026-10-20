namespace EntityAnalyzer.Core.Analysis;

/// <summary>Узел с рассчитанной позицией на канве.</summary>
public sealed record LayoutNode(string Name, double X, double Y);

/// <summary>
/// R2-7 «Интерактивный граф кодекса»: детерминированная force-directed
/// раскладка (симуляция Фрухтермана-Рейнгольда с затуханием).
/// Начальные позиции — архимедова спираль по индексу узла, без Random:
/// два вызова на любых машинах дают идентичную картину («созвездие карандашом»).
/// </summary>
public static class GraphLayout
{
    private const int Iterations = 500;
    private const double Margin = 50;       // поля канвы — имена не должны обрезаться
    private const double MinSeparation = 8; // защита от слипания (увеличено для читаемости)
    private const double Damping = 0.85;    // затухание скорости для стабильности

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
        // Начинаем с большего радиуса, чтобы узлы не скопились в центре.
        var xs = new double[n];
        var ys = new double[n];
        var vx = new double[n]; // скорости для затухания
        var vy = new double[n];
        var goldenAngle = Math.PI * (3 - Math.Sqrt(5));
        for (var i = 0; i < n; i++)
        {
            // Радиус растёт как sqrt(i), но с минимальным отступом от центра
            var r = Math.Max(k * 0.3, k * 0.8 * Math.Sqrt(i + 0.5));
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
        // с затуханием скоростей для стабильности.
        var fx = new double[n];
        var fy = new double[n];
        for (var step = 0; step < Iterations; step++)
        {
            var t = 1.0 - (double)step / Iterations;          // температура 1→0
            var maxDisp = k * 0.3 * t + 1.0;                  // максимальное смещение за шаг
            Array.Clear(fx);
            Array.Clear(fy);

            // Отталкивание: все пары узлов
            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++)
                {
                    var dx = xs[i] - xs[j];
                    var dy = ys[i] - ys[j];
                    var d2 = dx * dx + dy * dy;
                    if (d2 < 1e-6) { dx = 0.01; dy = 0.01; d2 = 1e-4; } // разлипание
                    var d = Math.Sqrt(d2);
                    // Мягкое отталкивание: k²/d, но ограниченное сверху
                    var f = Math.Min(k * k / d, k * 2);
                    var ux = dx / d;
                    var uy = dy / d;
                    fx[i] += ux * f; fy[i] += uy * f;
                    fx[j] -= ux * f; fy[j] -= uy * f;
                }

            // Притяжение: только связанные рёбрами узлы
            foreach (var (a, b) in edges)
            {
                var dx = xs[a] - xs[b];
                var dy = ys[a] - ys[b];
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 1e-6) continue;
                // Притяжение: d²/k, но ограниченное
                var f = Math.Min(d * d / k, k * 2);
                var ux = dx / d;
                var uy = dy / d;
                fx[a] -= ux * f; fy[a] -= uy * f;
                fx[b] += ux * f; fy[b] += uy * f;
            }

            // Интегрирование с затуханием (velocity Verlet упрощённый)
            for (var i = 0; i < n; i++)
            {
                vx[i] = (vx[i] + fx[i]) * Damping;
                vy[i] = (vy[i] + fy[i]) * Damping;
                var disp = Math.Sqrt(vx[i] * vx[i] + vy[i] * vy[i]);
                if (disp < 1e-9) continue;
                var lim = Math.Min(disp, maxDisp);
                xs[i] = Clamp(xs[i] + vx[i] / disp * lim, Margin, width - Margin);
                ys[i] = Clamp(ys[i] + vy[i] / disp * lim, Margin, height - Margin);
            }
        }

        // Финальное разлипание: если пара всё же совпала, раздвинуть по нормали.
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var dx = xs[j] - xs[i];
                var dy = ys[j] - ys[i];
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d >= MinSeparation) continue;
                if (d < 1e-6) { dx = 0.01; dy = 0.01; d = 0.01; }
                var push = (MinSeparation - d) * 0.5;
                var ux = dx / d;
                var uy = dy / d;
                xs[i] = Clamp(xs[i] - ux * push, Margin, width - Margin);
                ys[i] = Clamp(ys[i] - uy * push, Margin, height - Margin);
                xs[j] = Clamp(xs[j] + ux * push, Margin, width - Margin);
                ys[j] = Clamp(ys[j] + uy * push, Margin, height - Margin);
            }

        // Item 51: подгонка под канву. Симуляция могла стянуть созвездие в
        // угол или в центр — после неё раскладка масштабируется до ~90 %
        // доступной площади. Масштаб общий по осям — пропорции сохраняются.
        FitToCanvas(xs, ys, cx, cy, w, h);

        var result = new List<LayoutNode>(n);
        for (var i = 0; i < n; i++)
            result.Add(new LayoutNode(nodes[i].Name, Clamp(xs[i], Margin, width - Margin),
                                                            Clamp(ys[i], Margin, height - Margin)));
        return result;
    }

    /// <summary>
    /// Растянуть раскладку на ~90 % доступной площади (только увеличение,
    /// с потолком 1.6×), сохраняя пропорции и центрируя по канве.
    /// </summary>
    private static void FitToCanvas(double[] xs, double[] ys, double cx, double cy, double w, double h)
    {
        var minX = xs[0]; var maxX = xs[0];
        var minY = ys[0]; var maxY = ys[0];
        for (var i = 1; i < xs.Length; i++)
        {
            if (xs[i] < minX) minX = xs[i];
            if (xs[i] > maxX) maxX = xs[i];
            if (ys[i] < minY) minY = ys[i];
            if (ys[i] > maxY) maxY = ys[i];
        }

        var spanX = maxX - minX;
        var spanY = maxY - minY;
        if (spanX < 1e-6 && spanY < 1e-6) return; // одна точка — не трогаем

        var sx = spanX > 1e-6 ? w * 0.9 / spanX : double.MaxValue;
        var sy = spanY > 1e-6 ? h * 0.9 / spanY : double.MaxValue;
        var s = Math.Min(Math.Min(sx, sy), 1.6);
        if (Math.Abs(s - 1.0) < 0.02) return;

        var bx = (minX + maxX) / 2;
        var by = (minY + maxY) / 2;
        for (var i = 0; i < xs.Length; i++)
        {
            xs[i] = cx + (xs[i] - bx) * s;
            ys[i] = cy + (ys[i] - by) * s;
        }
    }

    private static double Clamp(double v, double min, double max)
        => v < min ? min : (v > max ? max : v);
}
