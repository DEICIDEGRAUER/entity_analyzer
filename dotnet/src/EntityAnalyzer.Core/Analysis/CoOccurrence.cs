using System.Text.Json;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Узел графа ко-упоминаний.</summary>
public sealed record GraphNode(string Name, int Weight);

/// <summary>Ребро графа: две сущности, совместно встреченные в файлах.</summary>
public sealed record GraphLink(string Source, string Target, int Weight);

/// <summary>
/// Граф совместных появлений сущностей: ребро тем весомее, чем больше файлов
/// упоминает обе сущности. Строится из готовой сессии анализа.
/// </summary>
public sealed class CoOccurrenceGraph
{
    public IReadOnlyList<GraphNode> Nodes { get; }
    public IReadOnlyList<GraphLink> Links { get; }

    public CoOccurrenceGraph(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphLink> links)
    {
        Nodes = nodes;
        Links = links;
    }

    public static CoOccurrenceGraph Build(AnalysisSession session)
    {
        // Сущности, встреченные в одном файле.
        var perFile = new List<HashSet<string>>();
        var weights = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var f in session.Files)
        {
            if (f.Mentions.Count == 0) continue;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in f.Mentions) set.Add(m.Entity);
            foreach (var e in set)
                weights[e] = weights.GetValueOrDefault(e) + f.Mentions.Count(m => m.Entity == e);
            if (set.Count >= 2) perFile.Add(set);
        }

        var links = new Dictionary<(string A, string B), int>();
        foreach (var set in perFile)
        {
            var list = set.OrderBy(x => x, StringComparer.Ordinal).ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var key = (list[i], list[j]);
                    links[key] = links.GetValueOrDefault(key) + 1;
                }
        }

        var nodes = weights
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new GraphNode(kv.Key, kv.Value))
            .ToList();

        var edges = links
            .Select(kv => new GraphLink(kv.Key.A, kv.Key.B, kv.Value))
            .OrderByDescending(l => l.Weight)
            .ToList();

        return new CoOccurrenceGraph(nodes, edges);
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(new
        {
            nodes = Nodes.Select(n => new { id = n.Name, w = n.Weight }),
            links = Links.Select(l => new { s = l.Source, t = l.Target, w = l.Weight }),
        }, new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>DOT-экспорт для Graphviz.</summary>
    public string ToDot()
    {
        var sb = new System.Text.StringBuilder("graph cooccurrence {\n  layout=neato; overlap=false;\n");
        foreach (var n in Nodes)
            sb.AppendLine($"  \"{Esc(n.Name)}\" [fontsize={Math.Max(10, Math.Min(28, 8 + n.Weight))}];");
        foreach (var l in Links)
            sb.AppendLine($"  \"{Esc(l.Source)}\" -- \"{Esc(l.Target)}\" [penwidth={Math.Max(1, Math.Min(6, l.Weight))}];");
        sb.Append('}');
        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("\"", "\\\"");
}
