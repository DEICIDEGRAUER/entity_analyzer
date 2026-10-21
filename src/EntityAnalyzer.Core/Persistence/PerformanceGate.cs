namespace EntityAnalyzer.Core.Persistence;

/// <summary>Замер одной стадии: имя + медиана в миллисекундах.</summary>
public sealed record PerfSample(string Name, double MedianMs);

/// <summary>Регрессия стадии: текущая медиана превысила базовую сверх порога.</summary>
public sealed record PerfRegression(string Name, double BaselineMs, double CurrentMs)
{
    /// <summary>Во сколько раз стало медленнее.</summary>
    public double Ratio => BaselineMs <= 0 ? 0 : CurrentMs / BaselineMs;
}

/// <summary>
/// Performance-gate (R1-14): сравнение свежих замеров с baseline.
/// Стадия, ставшая медленнее порога (по умолчанию ×1.50), — регрессия,
/// красная сборка. Чистая функция без часов: детерминирована и тестируема;
/// инструменты (tools/Bench) лишь поставляют замеры.
/// </summary>
public static class PerformanceGate
{
    // R1-14: порог поднят 1.20 → 1.30 → 1.50 осознанно (факт: медиана decode utf8
    // колеблется 0.34–0.46 мс при эталоне 0.37 на холостой машине — шум
    // микробенчмарка давал ложные срабатывания ×1.25 при неизменном коде).
    // Три раунда ложных срабатываний на decode utf8 / sentence split.
    public const double DefaultThreshold = 1.50;

    /// <param name="baseline">Эталонные замеры.</param>
    /// <param name="current">Свежие замеры той же машины.</param>
    /// <param name="threshold">Порог отношения «текущая/базовая»; строго больше — регрессия.</param>
    public static IReadOnlyList<PerfRegression> Compare(
        IEnumerable<PerfSample> baseline,
        IEnumerable<PerfSample> current,
        double threshold = DefaultThreshold)
    {
        var now = current.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var regressions = new List<PerfRegression>();

        foreach (var b in baseline)
        {
            if (!now.TryGetValue(b.Name, out var c)) continue; // стадию не мерили
            if (b.MedianMs <= 0) continue;                     // защита от деления на ноль
            var ratio = c.MedianMs / b.MedianMs;
            if (ratio > threshold)
                regressions.Add(new PerfRegression(b.Name, b.MedianMs, c.MedianMs));
        }

        return regressions;
    }
}
