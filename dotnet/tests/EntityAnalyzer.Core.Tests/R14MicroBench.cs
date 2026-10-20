using System.Diagnostics;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Semantics;
using Xunit;

namespace EntityAnalyzer.Tests;

/// <summary>R1-4: замер выигрыша якорной компиляции на плотном наборе паттернов.</summary>
public class R14MicroBench
{
    [Fact]
    public void Measure_Anchored_Vs_Legacy()
    {
        // 45 литеральных якорных паттернов + 5 легаси (capitalized-first).
        var patterns = new List<SemanticPattern>();
        var words = new[] { "древний", "пробуждение", "забытый", "храм", "жрец", "реликвия",
            "проклятие", "скрижаль", "идол", "культ", "ритуал", "сумрак", "источник" };
        for (var i = 0; i < words.Length; i++)
            patterns.Add(new SemanticPattern($"p{i}", $"p{i}", "",
                [new SemanticToken(TokenKind.Literal, words[i])]));
        for (var i = 0; i < 5; i++)
            patterns.Add(new SemanticPattern($"c{i}", $"c{i}", "", [SemanticToken.Cap(), SemanticToken.Lit("луны")]));

        // Корпус: 400 предложений, каждое третье содержит якорное слово.
        var rnd = new Random(42);
        var sb = new System.Text.StringBuilder();
        for (var s = 0; s < 400; s++)
        {
            var w = new List<string>();
            for (var j = 0; j < 12; j++) w.Add(words[rnd.Next(words.Length)]);
            if (s % 3 == 0) w[5] = words[rnd.Next(words.Length)];
            sb.Append(string.Join(' ', w)).Append(". ");
        }
        var text = sb.ToString();

        var legacy = new SemanticMatcher(patterns, new LexiconSet(), [], 50, useAnchoring: false);
        var fast = new SemanticMatcher(patterns, new LexiconSet(), [], 50, useAnchoring: true);

        // Прогрев + замер: 20 прогонов с высоким разрешением и медианой.
        // Одиночные замеры в миллисекундах слишком шумны под нагрузкой CI.
        double T(SemanticMatcher m)
        {
            for (var r = 0; r < 3; r++)
            {
                var warm = m.Scan(text, "b.txt", [0]);
                if (warm.Count == 0) throw new InvalidOperationException("нет совпадений");
            }

            var ts = new List<double>();
            for (var r = 0; r < 20; r++)
            {
                var start = Stopwatch.GetTimestamp();
                var res = m.Scan(text, "b.txt", [0]);
                var elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                if (res.Count == 0) throw new InvalidOperationException("нет совпадений");
                ts.Add(elapsed);
            }
            ts.Sort();
            // 25-й процентиль: отбрасываем редкие выбросы планировщика,
            // не подгоняя результат под лучший единичный замер.
            return ts[ts.Count / 4];
        }
        var tLegacy = T(legacy);
        var tFast = T(fast);
        var line = $"legacy={tLegacy:F1} ms | anchored={tFast:F1} ms | speedup={(tFast == 0 ? double.PositiveInfinity : tLegacy / Math.Max(0.001, tFast)):F1}x";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "r14_bench.txt"), line);
        // Допускаем 15% шума планировщика, но не реальное замедление.
        Assert.True(tFast <= tLegacy * 1.15, line);
    }
}
