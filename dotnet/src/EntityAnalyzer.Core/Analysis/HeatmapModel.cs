namespace EntityAnalyzer.Core.Analysis;

/// <summary>Плитка heat map: один файл корпуса.</summary>
public sealed record HeatTile(
    string FileName,
    long Chars,
    int Mentions,
    int BranchIndex,
    double DensityPer10k);

/// <summary>
/// R1-6 «Heat map корпуса»: плиточная карта файлов — размер плитки = объём
/// текста, цвет ветви = доминирующая сущность файла, насыщенность = плотность
/// упоминаний. Чистый расчёт; резолвер ветви передаётся извне (кодекс живёт
/// в App-слое).
/// </summary>
public static class HeatmapModel
{
    public static IReadOnlyList<HeatTile> Build(
        AnalysisSession session, Func<string, int> branchResolver)
    {
        var tiles = new List<HeatTile>();
        foreach (var f in session.Files)
        {
            if (f.Mentions.Count == 0) continue;

            // Доминирующая ветвь: сущность с максимумом упоминаний в файле.
            var byEntity = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var m in f.Mentions)
                byEntity[m.Entity] = byEntity.GetValueOrDefault(m.Entity) + 1;
            var top = byEntity.OrderByDescending(kv => kv.Value).First().Key;
            var branch = branchResolver(top);

            tiles.Add(new HeatTile(
                f.FileName,
                f.CharCount,
                f.Mentions.Count,
                branch,
                Math.Round(f.Mentions.Count * 10000.0 / Math.Max(1, f.CharCount), 1)));
        }

        return [.. tiles.OrderByDescending(t => t.Chars).ThenBy(t => t.FileName, StringComparer.Ordinal)];
    }
}
