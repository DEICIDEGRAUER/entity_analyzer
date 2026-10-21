namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// Thread-safe collector of filter-funnel metrics, mirroring (and extending)
/// the original Python quality.py.
/// </summary>
public sealed class QualityCollector
{
    private readonly object _gate = new();
    private long _rawMatches;
    private int _boundary, _brokenWord, _listing, _commonWord, _title, _final, _deduplicated;
    private long _displayCapped;
    private int _blacklist, _meaningfulness, _userPattern;
    private int _overlapsResolved;
    private int _skippedTooLarge, _skippedEmpty, _skippedDuplicate;
    private int _cacheHits, _cacheMisses, _unchangedSkipped;
    private int _l2Hits, _l2Misses;
    private int _utf8Files, _cp1251Files;
    private long _bytesScanned;
    private readonly Dictionary<string, long> _stageTicks = [];
    private readonly List<(string File, string Error)> _errors = [];

    public void CountRawMatches(int n = 1) { if (n > 0) lock (_gate) _rawMatches += n; }
    public void CountBoundaryRejected(int n = 1) { if (n > 0) lock (_gate) _boundary += n; }
    public void CountBrokenWordRejected(int n = 1) { if (n > 0) lock (_gate) _brokenWord += n; }
    public void CountListingRejected(int n = 1) { if (n > 0) lock (_gate) _listing += n; }
    public void CountCommonWordRejected(int n = 1) { if (n > 0) lock (_gate) _commonWord += n; }
    public void CountTitleRejected(int n = 1) { if (n > 0) lock (_gate) _title += n; }
    public void CountFinalMentions(int n = 1) { if (n > 0 || n < 0) lock (_gate) _final += n; }
    public void CountDeduplicated(int n = 1) { if (n > 0) lock (_gate) _deduplicated += n; }

    /// <summary>Упоминания, отсечённые лимитом цитат/файл (не фильтрами — прозрачность воронки).</summary>
    public void CountDisplayCapped(int n = 1) { if (n > 0) lock (_gate) _displayCapped += n; }

    /// <summary>Совпадения, схлопнутые резолвером перекрытий вариантов (QA N04: замыкает арифметику воронки).</summary>
    public void CountOverlapsResolved(int n = 1) { if (n > 0) lock (_gate) _overlapsResolved += n; }

    /// <summary>Тихие пропуски файлов — со сводкой в отчёте (QA N06).</summary>
    public void CountSkippedTooLarge(int n = 1) { if (n > 0) lock (_gate) _skippedTooLarge += n; }
    public void CountSkippedEmpty(int n = 1) { if (n > 0) lock (_gate) _skippedEmpty += n; }
    public void CountSkippedDuplicate(int n = 1) { if (n > 0) lock (_gate) _skippedDuplicate += n; }
    public void CountBlacklistRejected(int n = 1) { if (n > 0) lock (_gate) _blacklist += n; }
    public void CountMeaningfulnessRejected(int n = 1) { if (n > 0) lock (_gate) _meaningfulness += n; }
    public void CountUserPatternRejected(int n = 1) { if (n > 0) lock (_gate) _userPattern += n; }
    public void CountCacheHit() { lock (_gate) _cacheHits++; }
    public void CountCacheMiss() { lock (_gate) _cacheMisses++; }
    public void CountUnchangedSkipped() { lock (_gate) _unchangedSkipped++; }

    /// <summary>L2 (память): попадания/промахи уровня RAM за прогон (дельта счётчиков слоя).</summary>
    public void SetCacheL2(int hits, int misses)
    {
        lock (_gate)
        {
            _l2Hits = hits;
            _l2Misses = misses;
        }
    }

    public void CountEncoding(bool isUtf8, long bytes)
    {
        lock (_gate)
        {
            if (isUtf8) _utf8Files++; else _cp1251Files++;
            _bytesScanned += bytes;
        }
    }

    /// <summary>Замер длительности стадии: using (q.Measure("match")) { … }</summary>
    public IDisposable Measure(string stage) => new StageScope(this, stage);

    private sealed class StageScope(QualityCollector owner, string stage) : IDisposable
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public void Dispose()
        {
            _sw.Stop();
            lock (owner._gate)
                owner._stageTicks[stage] = owner._stageTicks.GetValueOrDefault(stage) + _sw.ElapsedTicks;
        }
    }

    public void AddError(string file, string error)
    {
        lock (_gate)
        {
            _errors.Add((file, error));
            if (_errors.Count > 200) _errors.RemoveAt(0);
        }
    }

    public QualityReport Snapshot(TimeSpan duration, int duplicatesGrouped = 0,
        IReadOnlyList<SemanticPatternStat>? semanticStats = null)
    {
        List<(string, string)> errors;
        Dictionary<string, double> stages;
        lock (_gate)
        {
            errors = _errors.TakeLast(20).ToList();
            stages = _stageTicks.ToDictionary(
                kv => kv.Key,
                kv => Math.Round(kv.Value * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 1));

            return new QualityReport(
                Interlocked.Read(ref _rawMatches),
                _boundary, _brokenWord, _listing, _commonWord, _title,
                _final, _deduplicated,
                _cacheHits, _cacheMisses,
                errors.Select(e => (e.Item1, e.Item2)).ToList(),
                $"{duration.TotalSeconds:0.0}s",
                _blacklist, _meaningfulness, duplicatesGrouped,
                stages, _unchangedSkipped, _userPattern,
                _bytesScanned, _utf8Files, _cp1251Files,
                semanticStats,
                (int)Interlocked.Read(ref _displayCapped))
            {
                OverlapsResolved = _overlapsResolved,
                SkippedTooLarge = _skippedTooLarge,
                SkippedEmpty = _skippedEmpty,
                SkippedDuplicate = _skippedDuplicate,
                L2Hits = _l2Hits,
                L2Misses = _l2Misses,
            };
        }
    }
}
