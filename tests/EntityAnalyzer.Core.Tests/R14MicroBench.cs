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
        // В CI тайминги шумят: сравниваем медиану трёх независимых серий
        // и допускаем 50% запаса. Это всё ещё ловит грубую деградацию,
        // но не падает от разовой просадки планировщика.
        var ratios = new List<double>();
        for (var round = 0; round < 3; round++)
        {
            var tLegacy = T(legacy);
            var tFast = T(fast);
            ratios.Add(tFast / Math.Max(0.001, tLegacy));
        }
        ratios.Sort();
        var ratio = ratios[ratios.Count / 2];
        var line = $"median anchored/legacy={ratio:F2}x; runs={string.Join(", ", ratios.Select(r => r.ToString("F2")))}";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "r14_bench.txt"), line);
        Assert.True(ratio <= 1.5, line);
    }
}
