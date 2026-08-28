using System.IO;
using System.Text;

namespace EntityAnalyzer.Core.Codex;

/// <summary>Sort orders for card listings.</summary>
public enum SortMode { ByName, ByMentionsDesc, ByType, ByModified }

/// <summary>A proposed link derived from corpus co-occurrence statistics.</summary>
public sealed record LinkProposal(string FirstId, string FirstName, string SecondId, string SecondName, int CoCount);

/// <summary>Pure codex logic: merge, paths, suggestions, filtering, slugs.</summary>
public static class CodexService
{
    /// <summary>All searchable text of a card in one lowercase string.</summary>
    public static string SearchText(Card c)
    {
        var sb = new StringBuilder();
        sb.Append(c.Name).Append(' ')
          .Append(c.Canonical).Append(' ')
          .Append(c.Group).Append(' ');
        foreach (var a in c.Aliases) sb.Append(a).Append(' ');
        foreach (var t in c.Tags) sb.Append(t).Append(' ');
        sb.Append(c.Description).Append(' ');
        foreach (var v in c.Fields.Values) sb.Append(v.Display()).Append(' ');
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Merges duplicate into primary: aliases, tags, fields, links are moved;
    /// self-loops are dropped; duplicate is removed. Returns a human report.
    /// </summary>
    public static string MergeCards(CodexDocument doc, string primaryId, string duplicateId)
    {
        var primary = doc.FindCard(primaryId) ?? throw new InvalidOperationException("primary card not found");
        var dup = doc.FindCard(duplicateId) ?? throw new InvalidOperationException("duplicate card not found");
        if (primaryId == duplicateId) throw new InvalidOperationException("cannot merge a card into itself");

        foreach (var a in dup.Aliases.ToList()) primary.AddAlias(a);
        foreach (var t in dup.Tags) if (!primary.Tags.Contains(t)) primary.Tags.Add(t);
        foreach (var (k, v) in dup.Fields) if (!primary.Fields.ContainsKey(k)) primary.Fields[k] = v;
        if (string.IsNullOrWhiteSpace(primary.Description))
            primary.Description = dup.Description;
        else if (!string.IsNullOrWhiteSpace(dup.Description))
            primary.Description += "\n\n---\n(объединено из «" + dup.Name + "»)\n" + dup.Description;
        if (primary.ColorMark is null && dup.ColorMark is not null) primary.ColorMark = dup.ColorMark;
        if (string.IsNullOrEmpty(primary.ImagePath) && dup.ImagePath is not null) primary.ImagePath = dup.ImagePath;

        int rewired = 0, dropped = 0;
        foreach (var link in doc.Links.ToArray())
        {
            if (link.FromId == dup.Id && link.ToId == primary.Id ||
                link.ToId == dup.Id && link.FromId == primary.Id)
            {
                doc.Links.Remove(link); // would become a self-loop
                dropped++;
            }
            else if (link.FromId == dup.Id)
            {
                link.FromId = primary.Id; rewired++;
            }
            else if (link.ToId == dup.Id)
            {
                link.ToId = primary.Id; rewired++;
            }
        }

        doc.Cards.Remove(dup);
        primary.ModifiedAt = DateTime.Now;
        return $"«{dup.Name}» объединена с «{primary.Name}»: алиасов +{dup.Aliases.Count}, связей перенесено {rewired}, петель отброшено {dropped}";
    }

    /// <summary>One labeled step of a path between cards.</summary>
    public sealed record PathStep(string FromId, string FromName, string ToId, string ToName, string Label);

    /// <summary>BFS over the undirected projection of links; [] when unreachable.</summary>
    public static PathStep[] FindPath(CodexDocument doc, string fromId, string toId, int maxDepth = 4)
    {
        if (fromId == toId) return [];

        var parent = new Dictionary<string, (string PrevId, CardLink Link, bool Forward)>();
        var visited = new HashSet<string> { fromId };
        var queue = new Queue<string>();
        queue.Enqueue(fromId);

        for (int depth = 0; depth < maxDepth && queue.Count > 0; depth++)
        {
            int levelCount = queue.Count;
            while (levelCount-- > 0)
            {
                var cur = queue.Dequeue();
                foreach (var link in doc.Links)
                {
                    string? next = null;
                    bool forward = false;
                    if (link.FromId == cur) { next = link.ToId; forward = true; }
                    else if (link.ToId == cur) { next = link.FromId; }
                    if (next is null || !visited.Add(next)) continue;

                    parent[next] = (cur, link, forward);
                    if (next == toId) return Reconstruct(parent, doc, fromId, toId);
                    queue.Enqueue(next);
                }
            }
        }
        return [];
    }

    private static PathStep[] Reconstruct(
        Dictionary<string, (string PrevId, CardLink Link, bool Forward)> parent,
        CodexDocument doc, string fromId, string toId)
    {
        var steps = new List<PathStep>();
        var cur = toId;
        while (cur != fromId)
        {
            var (prevId, link, forward) = parent[cur];
            var fromCard = doc.FindCard(prevId)!;
            var toCard = doc.FindCard(cur)!;
            var label = forward
                ? doc.FindRelation(link.RelationId)?.ForwardLabel ?? link.RelationId
                : doc.FindRelation(link.RelationId)?.ReverseLabel ?? link.RelationId;
            steps.Add(new PathStep(prevId, fromCard.Name, cur, toCard.Name, label));
            cur = prevId;
        }
        steps.Reverse();
        return steps.ToArray();
    }

    /// <summary>Proposes links for co-occurrence pairs when both cards exist and are not yet linked.</summary>
    public static List<LinkProposal> SuggestLinks(CodexDocument doc, IEnumerable<(string NameA, string NameB, int Count)> coPairs)
    {
        var byName = new Dictionary<string, Card>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in doc.Cards) byName[c.Name] = c;

        var linked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in doc.Links)
        {
            linked.Add(l.FromId + "|" + l.ToId);
            linked.Add(l.ToId + "|" + l.FromId);
        }

        var result = new List<LinkProposal>();
        foreach (var (a, b, count) in coPairs)
        {
            if (!byName.TryGetValue(a, out var ca) || !byName.TryGetValue(b, out var cb)) continue;
            if (linked.Contains(ca.Id + "|" + cb.Id)) continue;
            result.Add(new LinkProposal(ca.Id, ca.Name, cb.Id, cb.Name, count));
        }
        return result.OrderByDescending(p => p.CoCount).ToList();
    }

    /// <summary>Adds accepted proposals as typed links; returns how many were created.</summary>
    public static int AcceptProposals(CodexDocument doc, IEnumerable<LinkProposal> proposals, string relationId)
    {
        int added = 0;
        foreach (var p in proposals)
        {
            if (doc.Links.Any(l => (l.FromId == p.FirstId && l.ToId == p.SecondId) ||
                                   (l.FromId == p.SecondId && l.ToId == p.FirstId))) continue;
            doc.Links.Add(new CardLink(p.FirstId, p.SecondId, relationId, $"ко-упоминаний: {p.CoCount}"));
            added++;
        }
        return added;
    }

    /// <summary>Filters and sorts cards. Query matches name/aliases/tags/description/fields.</summary>
    public static List<Card> FilterCards(
        CodexDocument doc,
        string query = "",
        string typeId = "",
        string group = "",
        IReadOnlyList<string>? tagsAny = null,
        string? colorMark = null,
        IReadOnlySet<string>? mentionedNames = null,
        Dictionary<string, int>? mentionsByCard = null,
        SortMode sort = SortMode.ByName)
    {
        IEnumerable<Card> cards = doc.Cards;

        if (!string.IsNullOrWhiteSpace(typeId)) cards = cards.Where(c => c.TypeId == typeId);
        if (!string.IsNullOrWhiteSpace(group)) cards = cards.Where(c => string.Equals(c.Group, group, StringComparison.OrdinalIgnoreCase));
        if (colorMark is not null) cards = cards.Where(c => string.Equals(c.ColorMark, colorMark, StringComparison.OrdinalIgnoreCase));
        if (mentionedNames is not null)
            cards = cards.Where(c => mentionedNames.Contains(c.Name) || mentionedNames.Contains(c.Canonical));
        if (tagsAny is not null && tagsAny.Count > 0)
            cards = cards.Where(c => c.Tags.Any(t => tagsAny.Contains(t, StringComparer.OrdinalIgnoreCase)));

        var q = query.Trim().ToLowerInvariant();
        if (q.Length > 0) cards = cards.Where(c => SearchText(c).Contains(q, StringComparison.Ordinal));

        var list = cards.ToList();
        list.Sort(sort switch
        {
            SortMode.ByMentionsDesc => Comparer<Card>.Create((x, y) =>
                (mentionsByCard?.GetValueOrDefault(y.Name) ?? 0).CompareTo(mentionsByCard?.GetValueOrDefault(x.Name) ?? 0)),
            SortMode.ByType => Comparer<Card>.Create((x, y) =>
            {
                var r = string.Compare(doc.FindType(x.TypeId)?.Name, doc.FindType(y.TypeId)?.Name, StringComparison.Ordinal);
                return r != 0 ? r : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            }),
            SortMode.ByModified => Comparer<Card>.Create((x, y) => y.ModifiedAt.CompareTo(x.ModifiedAt)),
            _ => Comparer<Card>.Create((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)),
        });
        return list;
    }

    /// <summary>Card count per category display name.</summary>
    public static SortedDictionary<string, int> TypeCounts(CodexDocument doc)
    {
        var counts = new SortedDictionary<string, int>();
        foreach (var c in doc.Cards)
        {
            var name = doc.FindType(c.TypeId)?.Name ?? c.TypeId;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return counts;
    }

    /// <summary>File-system-safe slug for note file names.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_') sb.Append('_');
            // everything else dropped
        }
        var s = sb.ToString().Trim('_');
        return s.Length == 0 ? "card" : s;
    }

    /// <summary>Ensures unique slug inside a directory (appends -2, -3…).</summary>
    public static string UniqueSlug(string dir, string baseSlug)
    {
        var candidate = baseSlug;
        int n = 2;
        while (File.Exists(Path.Combine(dir, candidate + ".md")))
            candidate = baseSlug + "-" + n++;
        return candidate;
    }
}
