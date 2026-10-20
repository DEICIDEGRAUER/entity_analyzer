namespace EntityAnalyzer.Core.Genealogy;

/// <summary>Позиция узла на канве дерева.</summary>
public sealed record TreePlacement(string Id, double X, double Y);

/// <summary>Шаг сетки дерева: колонка, строка, поля.</summary>
public sealed record TreeLayoutOptions(
    double ColumnWidth = 180,
    double RowHeight = 130,
    double Margin = 60,
    double NodeWidth = 156,
    double NodeHeight = 62);

/// <summary>Готовая раскладка: позиции, размеры канвы и границы поколений.</summary>
public sealed record TreeLayoutResult(
    IReadOnlyList<TreePlacement> Placements,
    double Width,
    double Height,
    double RowHeight,
    double RowMargin,
    int GenerationCount);

/// <summary>
/// Детерминированная раскладка «поколения в ряд» для больших переплетённых
/// деревьев: строки — поколения, внутри строки узлы упорядочены обходом от
/// родоначальников и сгруппированы по компонентам; супруги ставятся рядом.
/// Без Random — одинаковый вход даёт одинаковую картинку.
/// </summary>
public static class FamilyTreeLayout
{
    public static TreeLayoutResult Calculate(FamilyTree tree, TreeLayoutOptions? options = null)
    {
        var opt = options ?? new TreeLayoutOptions();
        if (tree.Nodes.Count == 0)
            return new TreeLayoutResult([], opt.Margin * 2, opt.Margin * 2, opt.RowHeight, opt.Margin, 0);

        var generations = tree.Nodes
            .GroupBy(n => n.Generation)
            .OrderBy(g => g.Key)
            .Select(g => g.ToList())
            .ToList();

        foreach (var row in generations)
        {
            row.Sort((a, b) =>
            {
                var c = a.Component.CompareTo(b.Component);
                if (c != 0) return c;
                c = a.Order.CompareTo(b.Order);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Id, b.Id);
            });
        }

        // Супруги рядом: брачная пара должна стоять в строке подряд.
        var rowOf = new Dictionary<string, List<FamilyTreeNode>>(StringComparer.Ordinal);
        foreach (var row in generations)
            foreach (var node in row)
                rowOf[node.Id] = row;

        var nodeById = tree.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (var pair in tree.SpouseLinks)
        {
            if (!nodeById.TryGetValue(pair.A, out var a) || !nodeById.TryGetValue(pair.B, out var b)) continue;
            if (a.Generation != b.Generation) continue;
            var row = rowOf[a.Id];
            var ia = row.IndexOf(a);
            var ib = row.IndexOf(b);
            if (ib == ia + 1 || ib == ia - 1) continue;
            row.RemoveAt(ib);
            ia = row.IndexOf(a);
            row.Insert(Math.Min(ia + 1, row.Count), b);
        }

        var placements = new List<TreePlacement>(tree.Nodes.Count);
        double maxX = 0;
        for (var g = 0; g < generations.Count; g++)
        {
            var row = generations[g];
            for (var i = 0; i < row.Count; i++)
            {
                var x = opt.Margin + i * opt.ColumnWidth;
                var y = opt.Margin + g * opt.RowHeight;
                placements.Add(new TreePlacement(row[i].Id, x, y));
                maxX = Math.Max(maxX, x);
            }
        }

        var width = maxX + opt.NodeWidth + opt.Margin;
        var height = opt.Margin + generations.Count * opt.RowHeight;
        return new TreeLayoutResult(placements, width, height, opt.RowHeight, opt.Margin, generations.Count);
    }
}
