using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EntityAnalyzer.Core.Codex;

namespace EntityAnalyzer.Core.Hierarchy;

/// <summary>Узел иерархии: карточка кодекса, встроенная в дерево подчинения.</summary>
public sealed class HierarchyNode
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Marker { get; init; } = "◆";
    public string Color { get; init; } = "#C9A227";
    public string Detail { get; init; } = "";

    /// <summary>Повторная ссылка на карточку, уже показанную выше (без детей).</summary>
    public bool IsReference { get; init; }

    public List<HierarchyNode> Children { get; } = [];

    public int LocalDepth { get; init; }
}

/// <summary>Собранное «Древо Иерархии»: корни, счётчики, ссылки-повторы.</summary>
public sealed class HierarchyTree
{
    public IReadOnlyList<HierarchyNode> Roots { get; }
    public int Cards { get; }
    public int Placements { get; }
    public int References { get; }

    internal HierarchyTree(IReadOnlyList<HierarchyNode> roots, int cards, int placements, int references)
    {
        Roots = roots;
        Cards = cards;
        Placements = placements;
        References = references;
    }

    /// <summary>Плоский список узлов в порядке обхода (для поиска и списков).</summary>
    public IEnumerable<HierarchyNode> Flatten()
    {
        var stack = new Stack<HierarchyNode>(Roots.AsEnumerable().Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
    }
}

/// <summary>
/// Item «Древо Иерархии» группы 4: автосборка дерева подчинения из
/// типизированных связей кодекса. Смысл связей: «служит» и «находится» —
/// ребёнок слева (From), «породил», «родитель», «владеет» — ребёнок справа (To).
/// Повторные вхождения карточки становятся ссылками без детей, циклы
/// разрываются посещением — дерево всегда конечно.
/// </summary>
public static class HierarchyTreeBuilder
{
    /// <summary>Связи, участвующие в иерархии по умолчанию, в порядке старшинства.</summary>
    public static readonly string[] DefaultRelations = ["serves", "located", "spawn", "parent", "owns"];

    /// <summary>Ребёнок слева (From): «служит», «находится в».</summary>
    private static readonly HashSet<string> ChildIsFrom = new(StringComparer.OrdinalIgnoreCase) { "serves", "located" };

    /// <summary>Ребёнок справа (To): «породил», «родитель», «владеет».</summary>
    private static readonly HashSet<string> ChildIsTo = new(StringComparer.OrdinalIgnoreCase) { "spawn", "parent", "owns" };

    /// <summary>Связь пригодна для иерархии (её стороны однозначно старше/младше).</summary>
    public static bool Supports(string relationId) =>
        ChildIsFrom.Contains(relationId) || ChildIsTo.Contains(relationId);

    public static HierarchyTree Build(
        CodexDocument doc,
        IReadOnlyList<string>? relationIds = null,
        string? rootId = null,
        int maxDepth = 24)
    {
        var relations = new HashSet<string>(relationIds ?? DefaultRelations, StringComparer.OrdinalIgnoreCase);
        var cardById = doc.Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var typeById = doc.Types.ToDictionary(t => t.Id, StringComparer.Ordinal);

        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var hasParent = new HashSet<string>(StringComparer.Ordinal);
        var connected = new HashSet<string>(StringComparer.Ordinal);

        void AddEdge(string parent, string child)
        {
            if (parent == child) return;
            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = [];
            if (!list.Contains(child, StringComparer.Ordinal))
                list.Add(child);
            hasParent.Add(child);
            connected.Add(parent);
            connected.Add(child);
        }

        foreach (var link in doc.Links)
        {
            if (!relations.Contains(link.RelationId)) continue;
            if (!cardById.ContainsKey(link.FromId) || !cardById.ContainsKey(link.ToId)) continue;
            if (ChildIsFrom.Contains(link.RelationId))
                AddEdge(link.ToId, link.FromId);
            else if (ChildIsTo.Contains(link.RelationId))
                AddEdge(link.FromId, link.ToId);
        }

        if (connected.Count == 0)
            return new HierarchyTree([], 0, 0, 0);

        List<string> rootIds;
        if (rootId is not null && cardById.ContainsKey(rootId))
        {
            rootIds = [rootId];
        }
        else
        {
            rootIds = [.. doc.Cards
                .Select(c => c.Id)
                .Where(id => connected.Contains(id) && !hasParent.Contains(id))];
            if (rootIds.Count == 0)
                rootIds = [.. doc.Cards.Select(c => c.Id).Where(connected.Contains)];
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var references = 0;
        var placements = 0;

        HierarchyNode BuildNode(string id, int depth)
        {
            placements++;
            var card = cardById[id];
            var type = card.TypeId.Length > 0 && typeById.TryGetValue(card.TypeId, out var t) ? t : null;
            var isReference = !visited.Add(id);
            if (isReference) references++;
            var subtitle = card.Canonical.Length > 0 && !string.Equals(card.Canonical, card.Name, StringComparison.Ordinal)
                ? card.Canonical
                : card.Group;
            var detail = new StringBuilder();
            if (type is not null) detail.Append(type.Name);
            if (card.Group.Length > 0) detail.Append(detail.Length > 0 ? " · " : "").Append(card.Group);
            if (card.Description.Length > 0)
                detail.Append(detail.Length > 0 ? Environment.NewLine + Environment.NewLine : "").Append(card.Description.Trim());
            var aliases = card.Aliases.Count > 1 ? "Написания: " + string.Join(", ", card.Aliases) : "";
            if (aliases.Length > 0) detail.Append(Environment.NewLine + Environment.NewLine).Append(aliases);

            var node = new HierarchyNode
            {
                Id = id,
                Title = card.Name,
                Subtitle = subtitle,
                Kind = type?.Name ?? "",
                Marker = type?.Icon is { Length: > 0 } icon ? icon : "◆",
                Color = type?.Color is { Length: > 0 } color ? color : "#C9A227",
                Detail = detail.ToString(),
                IsReference = isReference,
                LocalDepth = depth,
            };

            if (!isReference && depth < maxDepth && children.TryGetValue(id, out var kids))
                foreach (var kid in kids)
                    node.Children.Add(BuildNode(kid, depth + 1));
            return node;
        }

        var roots = new List<HierarchyNode>();
        foreach (var id in rootIds)
            roots.Add(BuildNode(id, 0));

        return new HierarchyTree(roots, connected.Count, placements, references);
    }

    /// <summary>Markdown-экспорт: вложенные пункты.</summary>
    public static string ToMarkdown(HierarchyTree tree, string title = "Древо Иерархии")
    {
        var sb = new StringBuilder();
        sb.AppendLine("# " + title);
        foreach (var root in tree.Roots)
            AppendNode(sb, root, 0);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendNode(StringBuilder sb, HierarchyNode node, int depth)
    {
        sb.Append(new string(' ', depth * 2)).Append("- ").Append(node.Title);
        if (node.Kind.Length > 0) sb.Append(" (").Append(node.Kind).Append(')');
        if (node.IsReference) sb.Append(" ↻");
        sb.AppendLine();
        foreach (var child in node.Children)
            AppendNode(sb, child, depth + 1);
    }

    /// <summary>JSON-экспорт: вложенные узлы {id,title,kind,children}.</summary>
    public static string ToJson(HierarchyTree tree)
    {
        object Dto(HierarchyNode n) => new
        {
            id = n.Id,
            title = n.Title,
            kind = n.Kind,
            reference = n.IsReference ? true : (bool?)null,
            children = n.Children.Count == 0 ? null : n.Children.Select(Dto).ToList(),
        };
        var payload = new
        {
            roots = tree.Roots.Select(Dto).ToList(),
            cards = tree.Cards,
            placements = tree.Placements,
            references = tree.References,
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }
}
