using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>
/// R2-7 «Интерактивный граф кодекса»: детерминированная force-directed
/// раскладка («созвездие терминов карандашом»). Без Random — позиции
/// воспроизводимы между прогонами и машинами.
/// </summary>
public class GraphLayoutTests
{
    private static CoOccurrenceGraph MakeGraph(
        (string Name, int Weight)[] nodes, (string A, string B, int W)[] links) =>
        new([.. nodes.Select(n => new GraphNode(n.Name, n.Weight))],
            [.. links.Select(l => new GraphLink(l.A, l.B, l.W))]);

    [Fact]
    public void Empty_graph_gives_empty_layout()
    {
        var layout = GraphLayout.Calculate(MakeGraph([], []), 800, 600);
        Assert.Empty(layout);
    }

    [Fact]
    public void Single_node_is_centered()
    {
        var layout = GraphLayout.Calculate(
            MakeGraph([("Ктулху", 5)], []), 800, 600);
        var n = Assert.Single(layout);
        Assert.Equal(400, n.X, 1);
        Assert.Equal(300, n.Y, 1);
    }

    [Fact]
    public void All_nodes_within_bounds()
    {
        var g = MakeGraph(
        [
            ("Ктулху", 10), ("Наг", 6), ("Йеб", 4), ("Дагон", 3),
            ("Гидра", 2), ("Азатот", 8), ("Ньярлатотеп", 5),
        ],
        [("Ктулху", "Наг", 3), ("Наг", "Йеб", 2), ("Азатот", "Ньярлатотеп", 1)]);
        foreach (var n in GraphLayout.Calculate(g, 800, 600))
        {
            Assert.InRange(n.X, 0, 800);
            Assert.InRange(n.Y, 0, 600);
        }
    }

    [Fact]
    public void Layout_is_deterministic()
    {
        var g = MakeGraph(
        [
            ("Ктулху", 9), ("Наг", 7), ("Йеб", 5), ("Дагон", 3), ("Гидра", 2),
        ],
        [("Ктулху", "Наг", 4), ("Наг", "Йеб", 3), ("Дагон", "Гидра", 1)]);
        var a = GraphLayout.Calculate(g, 800, 600);
        var b = GraphLayout.Calculate(g, 800, 600);
        Assert.Equal(a.Select(x => (x.Name, x.X, x.Y)), b.Select(x => (x.Name, x.X, x.Y)));
    }

    [Fact]
    public void Linked_pair_is_closer_than_unlinked_average()
    {
        // Звезда: Ктулху связан со всеми, Ньярлатотеп — изолирован.
        var g = MakeGraph(
        [
            ("Ктулху", 9), ("Наг", 7), ("Йеб", 5), ("Дагон", 3), ("Ньярлатотеп", 1),
        ],
        [("Ктулху", "Наг", 4), ("Ктулху", "Йеб", 3), ("Ктулху", "Дагон", 2)]);
        var pos = GraphLayout.Calculate(g, 800, 600).ToDictionary(n => n.Name);

        double Dist(string a, string b)
        {
            var dx = pos[a].X - pos[b].X;
            var dy = pos[a].Y - pos[b].Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        var linkedAvg = new[] { Dist("Ктулху", "Наг"), Dist("Ктулху", "Йеб"), Dist("Ктулху", "Дагон") }.Average();
        var unlinkedAvg = new[] { Dist("Ньярлатотеп", "Наг"), Dist("Ньярлатотеп", "Йеб"), Dist("Ньярлатотеп", "Дагон") }.Average();
        Assert.True(linkedAvg < unlinkedAvg,
            $"связанные {linkedAvg:F1} должны быть ближе несвязанных {unlinkedAvg:F1}");
    }

    [Fact]
    public void Nodes_do_not_overlap()
    {
        var g = MakeGraph(
        [
            ("Ктулху", 5), ("Наг", 4), ("Йеб", 3), ("Дагон", 2), ("Гидра", 1),
        ],
        [("Ктулху", "Наг", 1)]);
        var pos = GraphLayout.Calculate(g, 800, 600).ToList();
        for (var i = 0; i < pos.Count; i++)
            for (var j = i + 1; j < pos.Count; j++)
            {
                var dx = pos[i].X - pos[j].X;
                var dy = pos[i].Y - pos[j].Y;
                Assert.True(Math.Sqrt(dx * dx + dy * dy) > 1.0,
                    $"{pos[i].Name} и {pos[j].Name} слиплись");
            }
    }
}
