using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-26 «Активное обучение порогов»: логистическая регрессия своими
/// руками (без ML-пакетов) на вердиктах — подбор персональных порогов.
/// </summary>
public class ThresholdLearnerTests
{
    [Fact]
    public void Perfectly_separable_data_is_separated()
    {
        // Признак x: принятые вердикты с высоким x, отклонённые с низким.
        var samples = new List<(double[] X, bool Accepted)>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add(([0.8 + 0.01 * i], true));
            samples.Add(([0.2 + 0.01 * i], false));
        }

        var model = LogisticRegression.Train(samples, epochs: 500, learningRate: 0.5);
        // Высокий признак → высокая вероятность принятия.
        Assert.True(model.Predict([0.9]) > 0.9);
        Assert.True(model.Predict([0.1]) < 0.1);
    }

    [Fact]
    public void Suggested_threshold_splits_training_data_reasonably()
    {
        var samples = new List<(double[] X, bool Accepted)>();
        for (var i = 0; i < 30; i++) samples.Add(([0.7 + 0.005 * i], true));
        for (var i = 0; i < 30; i++) samples.Add(([0.3 - 0.005 * i], false));

        var model = LogisticRegression.Train(samples, epochs: 500, learningRate: 0.5);
        var threshold = model.SuggestThreshold();

        var accepted = samples.Where(s => s.X[0] >= threshold).ToList();
        var rejected = samples.Where(s => s.X[0] < threshold).ToList();
        // Порог разделяет выборку почти идеально.
        Assert.True(accepted.All(s => s.Accepted));
        Assert.True(rejected.All(s => !s.Accepted));
    }

    [Fact]
    public void Empty_data_gives_neutral_model()
    {
        var model = LogisticRegression.Train([], epochs: 10, learningRate: 0.1);
        Assert.Equal(0.5, model.Predict([0.7]), precision: 1);
        Assert.Equal(0.5, model.SuggestThreshold(), precision: 1);
    }

    [Fact]
    public void Multi_feature_model_uses_all_weights()
    {
        // Принятие зависит от обоих признаков (x0 + x1 > 1).
        var samples = new List<(double[] X, bool Accepted)>();
        for (var i = 0; i < 25; i++)
        {
            samples.Add(([0.9, 0.9], true));
            samples.Add(([0.1, 0.1], false));
            samples.Add(([0.9, 0.1], true)); // сумма 1.0 — на границе, но выше
        }
        var model = LogisticRegression.Train(samples, epochs: 800, learningRate: 0.5);
        Assert.True(model.Predict([0.9, 0.9]) > model.Predict([0.1, 0.1]));
        Assert.Equal(2, model.Weights.Length);
    }
}
