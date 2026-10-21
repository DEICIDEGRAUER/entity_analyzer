namespace EntityAnalyzer.Core.Codex;

/// <summary>
/// Item 16: ребро ручной разметки для отрисовки поверх графа совместных
/// упоминаний: имена обеих карточек, подпись типа связи и заметка.
/// </summary>
public sealed record OverlayLink(string From, string To, string Label, string Note);

/// <summary>
/// Item 16: связывает типизированные связи кодекса (CardLink) с узлами
/// co-occurrence-графа. В оверлей попадают только связи, обе карточки
/// которых видны на графе (после фильтров ветвей); подпись — прямой ярлык
/// типа связи, у неизвестного типа — «связь». Чистая функция: без чтения
/// файлов, вызывающая сторона сама решает, как рисовать рёбра.
/// </summary>
public static class LinkOverlay
{
    public static List<OverlayLink> Build(
        IEnumerable<CardLink> links,
        IReadOnlyDictionary<string, string> namesById,
        IReadOnlyList<RelationType> relations,
        IReadOnlySet<string> nodeNames)
    {
        var result = new List<OverlayLink>();
        foreach (var link in links)
        {
            if (!namesById.TryGetValue(link.FromId, out var from)) continue;
            if (!namesById.TryGetValue(link.ToId, out var to)) continue;
            if (!nodeNames.Contains(from) || !nodeNames.Contains(to)) continue;

            var label = relations.FirstOrDefault(r => r.Id == link.RelationId)?.ForwardLabel;
            result.Add(new OverlayLink(
                from,
                to,
                string.IsNullOrWhiteSpace(label) ? "связь" : label,
                link.Note));
        }
        return result;
    }
}
