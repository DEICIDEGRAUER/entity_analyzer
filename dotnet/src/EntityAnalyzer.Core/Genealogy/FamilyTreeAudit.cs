namespace EntityAnalyzer.Core.Genealogy;

/// <summary>Категория проблемы, найденной в древе без изменения самих данных.</summary>
public enum FamilyIssueKind
{
    Isolated,
    DuplicateName,
    MultipleParents,
    Hypothesis,
    Disputed,
}

/// <summary>Наблюдение аудита: проблема, пояснение и персоны, к которым она ведёт.</summary>
public sealed record FamilyTreeIssue(
    FamilyIssueKind Kind,
    string Title,
    string Detail,
    IReadOnlyList<string> NodeIds);

/// <summary>
/// Аудит древа: не «ошибки импорта», а приглашение к исследованию. Ничего не
/// исправляет и не удаляет; показывает дубли имён, сирот, несколько наборов
/// родителей, слой гипотез и противоречивые связи.
/// </summary>
public static class FamilyTreeAudit
{
    public static IReadOnlyList<FamilyTreeIssue> Analyze(FamilyTree tree)
    {
        var issues = new List<FamilyTreeIssue>();
        var byId = tree.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        var linked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in tree.ParentLinks)
        {
            linked.Add(link.ParentId);
            linked.Add(link.ChildId);
        }
        foreach (var pair in tree.SpouseLinks)
        {
            linked.Add(pair.A);
            linked.Add(pair.B);
        }

        var isolated = tree.Nodes.Where(n => !linked.Contains(n.Id)).ToList();
        if (isolated.Count > 0)
        {
            issues.Add(new FamilyTreeIssue(
                FamilyIssueKind.Isolated,
                $"Одинокие персоны: {isolated.Count}",
                "Без родительских и партнёрских связей: " + Names(isolated, 4) +
                ". Это может быть нормальным черновиком, но полезно проверить.",
                [.. isolated.Select(n => n.Id)]));
        }

        var duplicateGroups = tree.Nodes
            .GroupBy(n => NormalizeName(n.Name))
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        foreach (var group in duplicateGroups)
        {
            var people = group.ToList();
            issues.Add(new FamilyTreeIssue(
                FamilyIssueKind.DuplicateName,
                $"Похожие имена: {group.Key} ({people.Count})",
                "Возможные дубли или однофамильцы: " + Names(people, 5) +
                ". Сначала сравните годы, места и связи.",
                [.. people.Select(n => n.Id)]));
        }

        var multipleParents = tree.ParentLinks
            .GroupBy(l => l.ChildId)
            .Select(g => new
            {
                Child = g.Key,
                Parents = g.Select(l => l.ParentId).Distinct(StringComparer.Ordinal).ToList(),
            })
            .Where(x => x.Parents.Count > 2)
            .OrderByDescending(x => x.Parents.Count)
            .ToList();
        foreach (var item in multipleParents)
        {
            var childName = byId.TryGetValue(item.Child, out var child) ? child.Name : item.Child;
            issues.Add(new FamilyTreeIssue(
                FamilyIssueKind.MultipleParents,
                $"Несколько наборов родителей: {childName}",
                $"У персоны {item.Parents.Count} родителя: " +
                string.Join(", ", item.Parents.Select(id => byId.TryGetValue(id, out var p) ? p.Name : id)) +
                ". Проверьте, не смешаны ли биологические, приёмные и опекунские связи.",
                [item.Child, .. item.Parents]));
        }

        AddConfidenceIssue(FamilyConfidence.Disputed, FamilyIssueKind.Disputed,
            "Противоречивые связи", "Связи, требующие проверки источников");
        AddConfidenceIssue(FamilyConfidence.Hypothesis, FamilyIssueKind.Hypothesis,
            "Слой гипотез", "Предполагаемые связи; их можно скрыть переключателем «Гипотезы»");

        return issues;

        void AddConfidenceIssue(FamilyConfidence level, FamilyIssueKind kind, string title, string explanation)
        {
            var parentLinks = tree.ParentLinks.Where(l => l.Confidence == level).ToList();
            var spouseLinks = tree.SpouseLinks.Where(p => p.Confidence == level).ToList();
            var count = parentLinks.Count + spouseLinks.Count;
            if (count == 0) return;

            var ids = parentLinks.SelectMany(l => new[] { l.ParentId, l.ChildId })
                .Concat(spouseLinks.SelectMany(p => new[] { p.A, p.B }))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var example = parentLinks.Select(l => l.DisplayLabel)
                .Concat(spouseLinks.Select(p => p.DisplayLabel))
                .FirstOrDefault() ?? "";
            issues.Add(new FamilyTreeIssue(
                kind,
                $"{title}: {count}",
                $"{explanation}. Например: {example}.",
                ids));
        }
    }

    private static string Names(IEnumerable<FamilyTreeNode> nodes, int max) =>
        string.Join(", ", nodes.Take(max).Select(n => n.Name)) +
        (nodes.Count() > max ? "…" : "");

    private static string NormalizeName(string name)
    {
        var chars = name.ToLowerInvariant().Replace('ё', 'е')
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
