namespace EntityAnalyzer.Core.Analysis;

/// <summary>Обученная логистическая регрессия: w·x + b → σ.</summary>
public sealed record LogisticModel(double[] Weights, double Bias)
{
    public double Predict(double[] x)
    {
        if (Weights.Length == 0) return 0.5;
        var z = Bias;
        for (var i = 0; i < Weights.Length && i < x.Length; i++) z += Weights[i] * x[i];
        return 1.0 / (1.0 + Math.Exp(-z));
    }

    /// <summary>
    /// Порог вероятности, максимизирующий точность на обучающей выборке:
    /// перебор кандидатов по предсказаниям (детерминированный).
    /// </summary>
    public double SuggestThreshold(
        IReadOnlyList<(double[] X, bool Accepted)>? samples = null, int featureIndex = 0)
    {
        if (samples is null || samples.Count == 0) return 0.5;

        // Кандидаты — значения признака выборки (порог «принять, если x ≥ t»).
        var values = samples.Select(s => s.X[featureIndex]).Distinct().OrderBy(v => v).ToList();
        if (values.Count == 0) return 0.5;

        double bestThreshold = values[0];
        var bestAccuracy = -1.0;
        foreach (var t in values)
        {
            var correct = samples.Count(s => (s.X[featureIndex] >= t) == s.Accepted);
            var accuracy = correct / (double)samples.Count;
            if (accuracy > bestAccuracy)
            {
                bestAccuracy = accuracy;
                bestThreshold = t;
            }
        }
        return bestThreshold;
    }
}

/// <summary>
/// R2-26 «Активное обучение порогов»: логистическая регрессия своими
/// руками — градиентный спуск по логистической функции потерь, без ML-пакетов.
/// Обучается на вердиктах пользователя (R2-13): признаки = сигналы
/// упоминания (fuzzy-процент, длина варианта и т.п.), метка = принят/отклонён.
/// </summary>
public static class LogisticRegression
{
    public static LogisticModel Train(
        IReadOnlyList<(double[] X, bool Accepted)> samples,
        int epochs = 500,
        double learningRate = 0.5)
    {
        if (samples.Count == 0) return new LogisticModel([], 0.0);

        var dim = samples.Max(s => s.X.Length);
        var w = new double[dim];
        double b = 0;

        for (var epoch = 0; epoch < epochs; epoch++)
        {
            var gw = new double[dim];
            double gb = 0;
            foreach (var (x, accepted) in samples)
            {
                var y = accepted ? 1.0 : 0.0;
                var p = new LogisticModel(w, b).Predict(x);
                var err = p - y;
                for (var i = 0; i < dim; i++)
                    gw[i] += err * (i < x.Length ? x[i] : 0);
                gb += err;
            }
            for (var i = 0; i < dim; i++) w[i] -= learningRate * gw[i] / samples.Count;
            b -= learningRate * gb / samples.Count;
        }

        return new LogisticModel(w, b);
    }
}
