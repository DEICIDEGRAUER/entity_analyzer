namespace EntityAnalyzer.Core.Analysis;

/// <summary>Режим запроса совместных упоминаний.</summary>
public enum CoOccurMode
{
    /// <summary>Оба объекта в одном файле (любые строки).</summary>
    SameFile,
    /// <summary>Оба в файле и ближе N строк друг к другу.</summary>
    NearLines,
    /// <summary>Оба в одном файле, но разнесены не менее чем на N строк.</summary>
    FarLines,
    /// <summary>Первый объект упоминается, второй в этом файле отсутствует.</summary>
    FirstWithoutSecond,
}

/// <summary>Строка результата запроса.</summary>
public sealed record CoOccurRow(
    string FileName,
    string FullPath,
    string EntityA,
    IReadOnlyList<int> LinesA,
    string? EntityB,
    IReadOnlyList<int>? LinesB,
    int MinGapLines,
    string Snippet);

/// <summary>
/// Гибкие запросы по уже выполненному анализу: несколько объектов поиска,
/// упомянутые рядом в одном файле — или наоборот, намеренно разнесённые;
/// присутствие одного без другого. Работает поверх сессии без пересканирования.
/// </summary>
public static class CoOccurrenceQuery
{
    public static IReadOnlyList<CoOccurRow> Run(
        AnalysisSession session,
        string entityA,
        string? entityB,
        CoOccurMode mode,
        int gapLines)
    {
        if (session is null || string.IsNullOrWhiteSpace(entityA)) return [];
        var needBoth = mode != CoOccurMode.FirstWithoutSecond;
        if (needBoth && string.IsNullOrWhiteSpace(entityB)) return [];

        var rows = new List<CoOccurRow>();

        foreach (var file in session.Files)
        {
            var mentionsA = file.Mentions.Where(m => m.Entity == entityA).ToList();
            var linesA = mentionsA.Select(m => m.Line).Distinct().OrderBy(x => x).ToList();

            List<int>? linesB = null;
            if (needBoth)
            {
                linesB = file.Mentions.Where(m => m.Entity == entityB).Select(m => m.Line)
                    .Distinct().OrderBy(x => x).ToList();
            }

            var passes = mode switch
            {
                CoOccurMode.SameFile => linesA.Count > 0 && linesB!.Count > 0,
                CoOccurMode.NearLines => linesA.Count > 0 && linesB!.Count > 0 &&
                                         MinGap(linesA, linesB) is { } g && g <= Math.Max(1, gapLines),
                CoOccurMode.FarLines => linesA.Count > 0 && linesB!.Count > 0 &&
                                        MinGap(linesA, linesB) is { } g && g >= Math.Max(1, gapLines),
                CoOccurMode.FirstWithoutSecond => linesA.Count > 0 && file.Mentions.All(m => m.Entity != entityB),
                _ => false,
            };
            if (!passes) continue;

            var snippet = mentionsA.FirstOrDefault()?.Context ?? "";
            if (snippet.Length > 180) snippet = snippet[..180].Trim() + "…";

            rows.Add(new CoOccurRow(file.FileName, file.FullPath, entityA, linesA,
                needBoth ? entityB : null, linesB,
                linesB is { Count: > 0 } ? MinGap(linesA, linesB) ?? -1 : -1,
                snippet));
        }

        return rows.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Минимальное расстояние в строках между любыми парами упоминаний.</summary>
    private static int? MinGap(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        if (a.Count == 0 || b.Count == 0) return null;
        int best = int.MaxValue;
        foreach (var x in a)
            foreach (var y in b)
            {
                var d = Math.Abs(x - y);
                if (d < best) best = d;
            }
        return best;
    }
}
