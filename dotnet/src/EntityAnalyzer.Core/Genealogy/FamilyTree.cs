using EntityAnalyzer.Core.Codex;

namespace EntityAnalyzer.Core.Genealogy;

/// <summary>
/// Узел семейного дерева: человек из GEDCOM или карточка кодекса.
/// Поколение и компонента считаются построителем, Order — обход от корней.
/// </summary>
public sealed record FamilyTreeNode(
    string Id,
    string Name,
    string Subtitle,
    string Sex,
    string Detail,
    int Generation,
    int Component,
    int Order)
{
    /// <summary>Худший статус уверенности среди связей персоны (для бейджа на узле).</summary>
    public FamilyConfidence Confidence { get; init; } = FamilyConfidence.Unknown;
}

/// <summary>Связь «родитель → ребёнок» с подписью, типом родства и уверенностью.</summary>
public sealed record FamilyTreeLink(
    string ParentId,
    string ChildId,
    string Label,
    FamilyRelationKind Kind = FamilyRelationKind.Biological,
    FamilyConfidence Confidence = FamilyConfidence.Unknown,
    string Note = "")
{
    /// <summary>Подпись для канвы и HTML: «отец · приёмн.», «гипотеза».</summary>
    public string DisplayLabel => FamilyRelationKinds.DisplayLabel(Label, Kind);
}

/// <summary>Связь супругов/партнёров с типом и уверенностью.</summary>
public sealed record FamilyTreePair(
    string A,
    string B,
    string Label,
    FamilyRelationKind Kind = FamilyRelationKind.Partner,
    FamilyConfidence Confidence = FamilyConfidence.Unknown)
{
    public string DisplayLabel => FamilyRelationKinds.DisplayLabel(Label, Kind);
}

/// <summary>
/// Автособираемое семейное дерево: узлы с поколениями плюс рёбра двух видов.
/// Дерево — не «лес одной семьи»: поддерживаются разрозненные компоненты,
/// повторные браки, циклы и битые ссылки (они получают поколение 0).
/// </summary>
public sealed class FamilyTree
{
    public IReadOnlyList<FamilyTreeNode> Nodes { get; }
    public IReadOnlyList<FamilyTreeLink> ParentLinks { get; }
    public IReadOnlyList<FamilyTreePair> SpouseLinks { get; }
    public int GenerationCount { get; }

    internal FamilyTree(
        IReadOnlyList<FamilyTreeNode> nodes,
        IReadOnlyList<FamilyTreeLink> parentLinks,
        IReadOnlyList<FamilyTreePair> spouseLinks,
        int generationCount)
    {
        Nodes = nodes;
        ParentLinks = parentLinks;
        SpouseLinks = spouseLinks;
        GenerationCount = generationCount;
    }

    /// <summary>Поколение 1 — родоначальники; для подписи в интерфейсе.</summary>
    public static string GenerationLabel(int generation) => $"Поколение {generation + 1}";

    /// <summary>
    /// Одна ветвь (компонент связности) как самостоятельное дерево. Рёбра,
    /// выходящие за пределы компонента, отбрасываются; нумерация поколений
    /// сохраняется. Нужно для больших разрозненных GEDCOM: канва показывает
    /// выбранную ветвь, а не весь лес сразу.
    /// </summary>
    public FamilyTree Subset(int component)
    {
        var nodes = Nodes.Where(n => n.Component == component).ToList();
        if (nodes.Count == Nodes.Count) return this;
        var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var parents = ParentLinks
            .Where(l => ids.Contains(l.ParentId) && ids.Contains(l.ChildId))
            .ToList();
        var spouses = SpouseLinks
            .Where(p => ids.Contains(p.A) && ids.Contains(p.B))
            .ToList();
        var generations = nodes.Count == 0 ? 0 : nodes.Max(n => n.Generation) + 1;
        return new FamilyTree(nodes, parents, spouses, generations);
    }

    /// <summary>
    /// Эго-сеть вокруг персоны: предки, потомки и партнёры в пределах
    /// указанного числа шагов. Пустые поколения сверху отбрасываются, чтобы
    /// фокус на глубокой ветви не открывался на пустом экране.
    /// </summary>
    public FamilyTree SubsetAround(string personId, int generations)
    {
        if (!Nodes.Any(n => string.Equals(n.Id, personId, StringComparison.Ordinal)))
            return this;

        var depthLimit = Math.Clamp(generations, 1, 8);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Connect(string a, string b)
        {
            if (!adjacency.TryGetValue(a, out var list)) adjacency[a] = list = [];
            list.Add(b);
            if (!adjacency.TryGetValue(b, out var reverse)) adjacency[b] = reverse = [];
            reverse.Add(a);
        }
        foreach (var link in ParentLinks) Connect(link.ParentId, link.ChildId);
        foreach (var pair in SpouseLinks) Connect(pair.A, pair.B);

        var keep = new HashSet<string>(StringComparer.Ordinal) { personId };
        var depth = new Dictionary<string, int>(StringComparer.Ordinal) { [personId] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(personId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var nextDepth = depth[current] + 1;
            if (nextDepth > depthLimit) continue;
            if (!adjacency.TryGetValue(current, out var neighbours)) continue;
            foreach (var other in neighbours)
            {
                if (depth.ContainsKey(other)) continue;
                depth[other] = nextDepth;
                keep.Add(other);
                queue.Enqueue(other);
            }
        }

        var nodes = Nodes.Where(n => keep.Contains(n.Id)).ToList();
        var parents = ParentLinks
            .Where(l => keep.Contains(l.ParentId) && keep.Contains(l.ChildId))
            .ToList();
        var spouses = SpouseLinks
            .Where(p => keep.Contains(p.A) && keep.Contains(p.B))
            .ToList();

        // В фокусе важна компактность: пустые поколения выше выбранной ветви
        // не должны занимать экран. Абсолютные номера нужны только полному древу.
        if (nodes.Count > 0)
        {
            var minGeneration = nodes.Min(n => n.Generation);
            if (minGeneration > 0)
                nodes = [.. nodes.Select(n => n with { Generation = n.Generation - minGeneration })];
        }

        var generationCount = nodes.Count == 0 ? 0 : nodes.Max(n => n.Generation) + 1;
        return new FamilyTree(RecalculateConfidence(nodes, parents, spouses), parents, spouses, generationCount);
    }

    /// <summary>
    /// Скрыть слой гипотез: остаются подтверждённые/вероятные связи, а также
    /// изолированные персоны. Узлы, державшиеся только на гипотезах, исчезают
    /// вместе с ними — но исходное дерево не меняется.
    /// </summary>
    public FamilyTree WithoutHypotheses()
    {
        var originalLinked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in ParentLinks)
        {
            originalLinked.Add(link.ParentId);
            originalLinked.Add(link.ChildId);
        }
        foreach (var pair in SpouseLinks)
        {
            originalLinked.Add(pair.A);
            originalLinked.Add(pair.B);
        }

        var parents = ParentLinks.Where(l => l.Confidence != FamilyConfidence.Hypothesis).ToList();
        var spouses = SpouseLinks.Where(p => p.Confidence != FamilyConfidence.Hypothesis).ToList();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in parents)
        {
            keep.Add(link.ParentId);
            keep.Add(link.ChildId);
        }
        foreach (var pair in spouses)
        {
            keep.Add(pair.A);
            keep.Add(pair.B);
        }
        foreach (var node in Nodes)
            if (!originalLinked.Contains(node.Id))
                keep.Add(node.Id);

        if (keep.Count == Nodes.Count && parents.Count == ParentLinks.Count && spouses.Count == SpouseLinks.Count)
            return this;

        var nodes = Nodes.Where(n => keep.Contains(n.Id)).ToList();
        parents = parents.Where(l => keep.Contains(l.ParentId) && keep.Contains(l.ChildId)).ToList();
        spouses = spouses.Where(p => keep.Contains(p.A) && keep.Contains(p.B)).ToList();
        var generationCount = nodes.Count == 0 ? 0 : nodes.Max(n => n.Generation) + 1;
        return new FamilyTree(RecalculateConfidence(nodes, parents, spouses), parents, spouses, generationCount);
    }

    /// <summary>Сколько связей находится в слое гипотез.</summary>
    public int HypothesisCount =>
        ParentLinks.Count(l => l.Confidence == FamilyConfidence.Hypothesis) +
        SpouseLinks.Count(p => p.Confidence == FamilyConfidence.Hypothesis);

    private static IReadOnlyList<FamilyTreeNode> RecalculateConfidence(
        IReadOnlyList<FamilyTreeNode> nodes,
        IReadOnlyList<FamilyTreeLink> parentLinks,
        IReadOnlyList<FamilyTreePair> spouseLinks)
    {
        var byNode = new Dictionary<string, List<FamilyConfidence>>(StringComparer.Ordinal);
        foreach (var link in parentLinks)
        {
            Add(link.ParentId, link.Confidence);
            Add(link.ChildId, link.Confidence);
        }
        foreach (var pair in spouseLinks)
        {
            Add(pair.A, pair.Confidence);
            Add(pair.B, pair.Confidence);
        }

        return nodes.Select(n => n with
        {
            Confidence = byNode.TryGetValue(n.Id, out var levels)
                ? FamilyConfidenceScale.Combine(levels)
                : FamilyConfidence.Unknown,
        }).ToList();

        void Add(string id, FamilyConfidence level)
        {
            if (!byNode.TryGetValue(id, out var levels))
                byNode[id] = levels = [];
            levels.Add(level);
        }
    }
}

/// <summary>
/// Сборка дерева: из документа GEDCOM и из типизированных связей кодекса
/// («породил», «родитель»). Раскладка — отдельно, в <see cref="FamilyTreeLayout"/>.
/// </summary>
public static class FamilyTreeBuilder
{
    /// <summary>Стоимость подписи ребра по идентификатору связи кодекса.</summary>
    public static readonly string[] KinshipRelationIds =
    [
        "spawn", "parent", "adopt", "adopted", "adoption", "foster",
        "guardian", "godparent", "named", "partner", "spouse", "marriage",
    ];

    /// <summary>Есть ли у связи кодекса родственная семантика (включая партнёрство).</summary>
    public static bool IsKinshipRelation(CodexDocument doc, string relationId)
    {
        var relation = doc.Relations.FirstOrDefault(r =>
            string.Equals(r.Id, relationId, StringComparison.OrdinalIgnoreCase));
        return FamilyRelationKinds.Classify(relationId, relation?.ForwardLabel, relation?.ReverseLabel)
               != FamilyRelationKind.Unknown;
    }

    public static FamilyTree FromGedcom(GedcomDocument doc)
    {
        var nodes = new List<(string Id, string Name, string Subtitle, string Sex, string Detail)>();
        foreach (var p in doc.Persons)
        {
            var years = "";
            var b = p.Birth?.Year;
            var d = p.Death?.Year;
            if (b is not null && d is not null) years = $"{b}—{d}";
            else if (b is not null) years = $"{b}—";
            else if (d is not null) years = $"† {d}";
            if (p.AlternateNames.Count > 0)
                years = years.Length == 0
                    ? $"имён: {p.AlternateNames.Count + 1}"
                    : $"{years} · имён: {p.AlternateNames.Count + 1}";
            nodes.Add((p.Id, p.DisplayName, years, p.Sex, GedcomNarrator.PersonCard(doc, p)));
        }

        var parentLinks = new List<FamilyTreeLink>();
        var spouseLinks = new List<FamilyTreePair>();
        foreach (var f in doc.Families)
        {
            foreach (var childId in f.Children)
            {
                var child = doc.Person(childId);
                var kind = FamilyRelationKinds.FromPedigree(
                    child?.ChildPedigree.GetValueOrDefault(f.Id));
                var confidence = FamilyConfidenceScale.FromGedcomQuality(
                    child?.ChildConfidence.GetValueOrDefault(f.Id));

                if (f.HusbandId is not null)
                    parentLinks.Add(new FamilyTreeLink(f.HusbandId, childId, "отец", kind, confidence));
                if (f.WifeId is not null)
                    parentLinks.Add(new FamilyTreeLink(f.WifeId, childId, "мать", kind, confidence));
            }

            if (f.HusbandId is not null && f.WifeId is not null)
            {
                var label = f.Marriage?.Year is { } y ? $"брак {y}" : "партнёрство";
                spouseLinks.Add(new FamilyTreePair(f.HusbandId, f.WifeId, label,
                    FamilyRelationKind.Partner, FamilyConfidence.Unknown));
            }
        }

        return Build(nodes, parentLinks, spouseLinks);
    }

    /// <summary>
    /// Дерево из кодекса: родительские и партнёрские связи классифицируются
    /// по типу («породил», «родитель», «усыновляет», «опекун», «партнёр»…),
    /// а уверенность берётся из CardLink.Confidence. Карточки без родственных
    /// связей в дерево не попадают.
    /// </summary>
    public static FamilyTree FromCodex(CodexDocument doc)
    {
        var relationById = doc.Relations.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var cardById = doc.Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var parentLinks = new List<FamilyTreeLink>();
        var spouseLinks = new List<FamilyTreePair>();
        var nodeIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var link in doc.Links)
        {
            if (!cardById.ContainsKey(link.FromId) || !cardById.ContainsKey(link.ToId)) continue;
            if (link.FromId == link.ToId) continue;

            relationById.TryGetValue(link.RelationId, out var relation);
            var kind = FamilyRelationKinds.Classify(link.RelationId, relation?.ForwardLabel, relation?.ReverseLabel);
            if (kind == FamilyRelationKind.Unknown) continue;

            var label = relation is not null && relation.ForwardLabel.Length > 0
                ? relation.ForwardLabel
                : link.RelationId;
            var confidence = FamilyConfidenceScale.Parse(link.Confidence);

            if (kind == FamilyRelationKind.Partner)
                spouseLinks.Add(new FamilyTreePair(link.FromId, link.ToId, label, kind, confidence));
            else
                parentLinks.Add(new FamilyTreeLink(link.FromId, link.ToId, label, kind, confidence, link.Note));

            if (seen.Add(link.FromId)) nodeIds.Add(link.FromId);
            if (seen.Add(link.ToId)) nodeIds.Add(link.ToId);
        }

        var typeNames = doc.Types.ToDictionary(t => t.Id, t => t.Name, StringComparer.Ordinal);
        var nodes = nodeIds.Select(id =>
        {
            var card = cardById[id];
            var typeName = typeNames.TryGetValue(card.TypeId, out var t) ? t : card.TypeId;
            var subtitle = card.Canonical.Length > 0 && !string.Equals(card.Canonical, card.Name, StringComparison.Ordinal)
                ? card.Canonical
                : typeName;
            return (card.Id, card.Name, subtitle, "U", card.Description.Trim());
        }).ToList();

        return Build(nodes, parentLinks, spouseLinks);
    }

    // ─── Общая сборка: поколения, компоненты, порядок обхода ───

    private static FamilyTree Build(
        List<(string Id, string Name, string Subtitle, string Sex, string Detail)> raw,
        List<FamilyTreeLink> parentLinks,
        List<FamilyTreePair> spouseLinks)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
            index[raw[i].Id] = i;

        // Рёбра с неизвестными узлами не роняют дерево, а отбрасываются.
        parentLinks = [.. parentLinks.Where(l => index.ContainsKey(l.ParentId) && index.ContainsKey(l.ChildId))];
        parentLinks = [.. parentLinks.Distinct()];
        spouseLinks = [.. spouseLinks.Where(l => index.ContainsKey(l.A) && index.ContainsKey(l.B))];
        spouseLinks = [.. spouseLinks.Distinct()];

        var generations = ComputeGenerations(raw.Count, index, parentLinks, spouseLinks);
        var components = ComputeComponents(raw.Count, index, parentLinks, spouseLinks);
        var order = ComputeOrder(raw, index, parentLinks);

        var generationCount = generations.Length == 0 ? 0 : generations.Max() + 1;
        var nodes = new List<FamilyTreeNode>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            nodes.Add(new FamilyTreeNode(r.Id, r.Name, r.Subtitle, r.Sex, r.Detail,
                generations[i], components[i], order[i]));
        }

        var confidenceByNode = new Dictionary<string, List<FamilyConfidence>>(StringComparer.Ordinal);
        foreach (var link in parentLinks)
        {
            Add(link.ParentId, link.Confidence);
            Add(link.ChildId, link.Confidence);
        }
        foreach (var pair in spouseLinks)
        {
            Add(pair.A, pair.Confidence);
            Add(pair.B, pair.Confidence);
        }
        for (var i = 0; i < nodes.Count; i++)
        {
            if (confidenceByNode.TryGetValue(nodes[i].Id, out var levels))
                nodes[i] = nodes[i] with { Confidence = FamilyConfidenceScale.Combine(levels) };
        }

        return new FamilyTree(nodes, parentLinks, spouseLinks, generationCount);

        void Add(string id, FamilyConfidence level)
        {
            if (!confidenceByNode.TryGetValue(id, out var levels))
                confidenceByNode[id] = levels = [];
            levels.Add(level);
        }
    }

    /// <summary>
    /// Поколения: партнёры всегда стоят в одном поколении, поэтому сначала
    /// они объединяются в группы, затем рёбра «родитель → ребёнок» укладываются
    /// между группами. Обратные рёбра циклов отбрасываются обходом в глубину,
    /// топологический порядок (Kahn) даёт длиннейший путь от корней.
    /// </summary>
    private static int[] ComputeGenerations(
        int count,
        Dictionary<string, int> index,
        List<FamilyTreeLink> parentLinks,
        List<FamilyTreePair> spouseLinks)
    {
        // Партнёрская связь — ограничение равенства поколений.
        var union = Enumerable.Range(0, count).ToArray();
        int Find(int x)
        {
            while (union[x] != x)
            {
                union[x] = union[union[x]];
                x = union[x];
            }
            return x;
        }
        void Unite(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b) union[b] = a;
        }
        foreach (var pair in spouseLinks)
            if (index.TryGetValue(pair.A, out var a) && index.TryGetValue(pair.B, out var b))
                Unite(a, b);

        var groupOf = new int[count];
        var groupIndex = new Dictionary<int, int>();
        for (var i = 0; i < count; i++)
        {
            var root = Find(i);
            if (!groupIndex.TryGetValue(root, out var group))
                groupIndex[root] = group = groupIndex.Count;
            groupOf[i] = group;
        }

        var groups = groupIndex.Count;
        var edges = parentLinks
            .Select(l => (P: groupOf[index[l.ParentId]], C: groupOf[index[l.ChildId]]))
            .Where(e => e.P != e.C)
            .Distinct()
            .Select((e, i) => (e.P, e.C, I: i))
            .ToList();
        var adjacency = new List<(int To, int Edge)>[groups];
        for (var i = 0; i < groups; i++) adjacency[i] = [];
        foreach (var edge in edges)
            adjacency[edge.P].Add((edge.C, edge.I));

        // DFS: ребро в «серую» вершину замыкает цикл — в поколениях не участвует.
        var color = new byte[groups];
        var kept = new bool[edges.Count];
        for (var start = 0; start < groups; start++)
        {
            if (color[start] != 0) continue;
            color[start] = 1;
            var stack = new Stack<(int Node, int Next)>();
            stack.Push((start, 0));
            while (stack.Count > 0)
            {
                var (u, next) = stack.Pop();
                if (next >= adjacency[u].Count)
                {
                    color[u] = 2;
                    continue;
                }
                stack.Push((u, next + 1));
                var (v, edgeIndex) = adjacency[u][next];
                if (color[v] == 0)
                {
                    kept[edgeIndex] = true;
                    color[v] = 1;
                    stack.Push((v, 0));
                }
                else if (color[v] == 2)
                {
                    kept[edgeIndex] = true;
                }
            }
        }

        var indegree = new int[groups];
        var children = new List<int>[groups];
        for (var i = 0; i < groups; i++) children[i] = [];
        foreach (var edge in edges)
        {
            if (!kept[edge.I]) continue;
            children[edge.P].Add(edge.C);
            indegree[edge.C]++;
        }

        var groupGen = new int[groups];
        var queue = new Queue<int>();
        for (var i = 0; i < groups; i++)
            if (indegree[i] == 0) queue.Enqueue(i);

        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            foreach (var c in children[u])
            {
                if (groupGen[u] + 1 > groupGen[c]) groupGen[c] = groupGen[u] + 1;
                if (--indegree[c] == 0) queue.Enqueue(c);
            }
        }

        return [.. groupOf.Select(g => groupGen[g])];
    }

    /// <summary>Компоненты связности по всем рёбрам (родители + супруги).</summary>
    private static int[] ComputeComponents(
        int count, Dictionary<string, int> index,
        List<FamilyTreeLink> parentLinks, List<FamilyTreePair> spouseLinks)
    {
        var parent = Enumerable.Range(0, count).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        foreach (var link in parentLinks) Union(index[link.ParentId], index[link.ChildId]);
        foreach (var link in spouseLinks) Union(index[link.A], index[link.B]);

        var component = new int[count];
        var byRoot = new Dictionary<int, int>();
        for (var i = 0; i < count; i++)
        {
            var root = Find(i);
            if (!byRoot.TryGetValue(root, out var number))
                byRoot[root] = number = byRoot.Count;
            component[i] = number;
        }
        return component;
    }

    /// <summary>Порядок обхода: DFS от родоначальников, затем оставшиеся по файлу.</summary>
    private static int[] ComputeOrder(
        List<(string Id, string Name, string Subtitle, string Sex, string Detail)> raw,
        Dictionary<string, int> index, List<FamilyTreeLink> parentLinks)
    {
        var count = raw.Count;
        var childOf = new bool[count];
        var children = new List<int>[count];
        for (var i = 0; i < count; i++) children[i] = [];
        foreach (var link in parentLinks)
        {
            children[index[link.ParentId]].Add(index[link.ChildId]);
            childOf[index[link.ChildId]] = true;
        }

        var order = new int[count];
        Array.Fill(order, -1);
        var next = 0;
        var stack = new Stack<int>();
        for (var i = count - 1; i >= 0; i--)
            if (!childOf[i]) stack.Push(i);

        while (stack.Count > 0)
        {
            var u = stack.Pop();
            if (order[u] >= 0) continue;
            order[u] = next++;
            for (var i = children[u].Count - 1; i >= 0; i--)
                if (order[children[u][i]] < 0)
                    stack.Push(children[u][i]);
        }

        for (var i = 0; i < count; i++)
            if (order[i] < 0)
                order[i] = next++;
        return order;
    }
}
