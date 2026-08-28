using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

public class QualityCollectorTests
{
    [Fact]
    public void Funnel_counters_snapshot()
    {
        var q = new QualityCollector();
        q.CountRawMatches(10);
        q.CountBoundaryRejected(3);
        q.CountBrokenWordRejected(1);
        q.CountFinalMentions(4);
        q.CountCacheHit(); q.CountCacheMiss();

        var report = q.Snapshot(TimeSpan.FromSeconds(5));
        Assert.Equal(10, report.RawMatches);
        Assert.Equal(4, report.BoundaryRejected + report.BrokenWordRejected);
        Assert.Equal(4, report.FinalMentions);
        Assert.Equal("40%", report.FilterEfficiency); // (3+1)/10 killed by boundary filters
        Assert.Equal(1, report.CacheHits);
    }
}
