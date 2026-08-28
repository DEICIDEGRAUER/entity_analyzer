using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R1-6 «Heat map корпуса»: плиточная карта файлов — размер = объём,
/// насыщенность ветви = плотность упоминаний. Чистый расчёт в ядре;
/// ветвь сущности приходит извне (функция-резолвер).
/// </summary>
public class HeatmapModelTests
{
    private static AnalysisSession MakeSession(params (string File, string Text, (string Entity, int N)[] Mentions)[] files)
    {
        var results = new List<FileResult>();
        foreach (var (file, text, mentions) in files)
        {
            var ms = new List<Mention>();
            var n = 1;
            foreach (var (entity, count) in mentions)
                for (var i = 0; i < count; i++)
                    ms.Add(new Mention(n++, entity, entity, 1, "контекст"));
            results.Add(new FileResult(file, file, ms, ms.Count, 40, text.Length,
                ms.Count * 10000 / Math.Max(1, text.Length)));
        }
        return new AnalysisSession(results,
            new AnalysisSummary(0, 0, 0, results.Sum(r => r.CharCount),
                new Dictionary<string, int>()),
            new QualityReport(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], "0s"),
            "light", DateTimeOffset.Now, TimeSpan.Zero);
    }

    [Fact]
    public void Empty_session_gives_no_tiles()
    {
        Assert.Empty(HeatmapModel.Build(MakeSession(), _ => 0));
    }

    [Fact]
    public void Tiles_are_sorted_by_volume_descending()
    {
        var session = MakeSession(
            ("small.txt", new string('a', 100), [("Ктулху", 1)]),
            ("big.txt", new string('a', 9000), [("Ктулху", 1)]),
            ("mid.txt", new string('a', 2000), [("Ктулху", 1)]));
        var tiles = HeatmapModel.Build(session, _ => 0);
        Assert.Equal(["big.txt", "mid.txt", "small.txt"], tiles.Select(t => t.FileName));
    }

    [Fact]
    public void Density_is_mentions_per_10k_chars()
    {
        var session = MakeSession(("f.txt", new string('a', 5000), [("Ктулху", 3)]));
        var tile = Assert.Single(HeatmapModel.Build(session, _ => 2));
        Assert.Equal(6.0, tile.DensityPer10k, 1);
        Assert.Equal(2, tile.BranchIndex);
    }

    [Fact]
    public void Dominant_branch_is_entity_with_most_mentions()
    {
        // Резолвер: Ктулху→0, Наг и Йеб→4.
        var session = MakeSession(
            ("f.txt", new string('a', 4000), [("Наг и Йеб", 1), ("Ктулху", 5)]));
        var tile = Assert.Single(HeatmapModel.Build(session,
            name => name == "Наг и Йеб" ? 4 : 0));
        Assert.Equal(0, tile.BranchIndex); // у Ктулху больше упоминаний
    }
}
