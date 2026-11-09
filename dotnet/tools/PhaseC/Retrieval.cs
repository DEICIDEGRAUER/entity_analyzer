using EntityAnalyzer.Core.Analysis;

namespace PhaseC;

internal enum TextRepresentation
{
    Name,
    Context,
}

internal sealed record RankedContext(
    string CandidateId,
    string CardId,
    string Entity,
    string File,
    string Variant,
    string Text,
    double Score);

internal sealed record RankedEntity(
    string CardId,
    string Entity,
    string File,
    string Variant,
    string CandidateId,
    string Text,
    double Score);

internal sealed record QueryMetrics(
    string QueryId,
    string QueryCardId,
    int RelevantCount,
    double PrecisionAt5,
    double NdcgAt10,
    IReadOnlyList<string> Top10);

internal sealed record EvaluationSummary(
    double PrecisionAt5,
    double NdcgAt10,
    IReadOnlyList<QueryMetrics> Queries,
    int QueryCount)
{
    public double Top10OverlapWith(EvaluationSummary other)
    {
        if (Queries.Count != other.Queries.Count)
            throw new ArgumentException("Query counts do not match.", nameof(other));

        var sum = 0.0;
        for (var i = 0; i < Queries.Count; i++)
            sum += Metrics.Top10Overlap(Queries[i].Top10, other.Queries[i].Top10);

        return QueryCount == 0 ? 0 : sum / QueryCount;
    }
}

internal static class Metrics
{
    public const int PrecisionK = 5;
    public const int NdcgK = 10;

    public static double PrecisionAt5(IReadOnlyList<string> ranked, IReadOnlySet<string> relevant)
    {
        if (ranked.Count == 0 || relevant.Count == 0)
            return 0;

        var hits = 0;
        for (var i = 0; i < Math.Min(PrecisionK, ranked.Count); i++)
            if (relevant.Contains(ranked[i]))
                hits++;
        return hits / (double)PrecisionK;
    }

    public static double NdcgAt10(IReadOnlyList<string> ranked, IReadOnlySet<string> relevant)
    {
        if (relevant.Count == 0)
            return 0;

        var dcg = 0.0;
        for (var i = 0; i < Math.Min(NdcgK, ranked.Count); i++)
            if (relevant.Contains(ranked[i]))
                dcg += 1.0 / Math.Log2(i + 2);

        var idealHits = Math.Min(NdcgK, relevant.Count);
        var idcg = 0.0;
        for (var i = 0; i < idealHits; i++)
            idcg += 1.0 / Math.Log2(i + 2);

        return idcg == 0 ? 0 : dcg / idcg;
    }

    public static double Top10Overlap(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var topA = a.Take(NdcgK).ToHashSet(StringComparer.Ordinal);
        var topB = b.Take(NdcgK).ToHashSet(StringComparer.Ordinal);
        if (topA.Count == 0 || topB.Count == 0)
            return 0;

        var intersection = topA.Count(topB.Contains);
        return intersection / (double)NdcgK;
    }

    public static double Average(IReadOnlyList<QueryMetrics> queries, Func<QueryMetrics, double> selector) =>
        queries.Count == 0 ? 0 : queries.Average(selector);
}

internal sealed class Ranker
{
    private readonly VectorIndex _index;
    private readonly IReadOnlyList<EntityMention> _candidates;
    private readonly IReadOnlyDictionary<string, EntityMention> _byId;
    private readonly TextRepresentation _representation;

    private Ranker(
        VectorIndex index,
        IReadOnlyList<EntityMention> candidates,
        IReadOnlyDictionary<string, EntityMention> byId,
        TextRepresentation representation)
    {
        _index = index;
        _candidates = candidates;
        _byId = byId;
        _representation = representation;
    }

    public int CandidateCount => _candidates.Count;

    public static Ranker Build(
        IEmbedder embedder,
        IReadOnlyList<EntityMention> candidates,
        TextRepresentation representation)
    {
        var byId = new Dictionary<string, EntityMention>(StringComparer.Ordinal);
        var index = new VectorIndex(embedder);
        foreach (var candidate in candidates)
        {
            if (!byId.TryAdd(candidate.CandidateId, candidate))
            {
                throw new InvalidDataException(
                    $"Duplicate candidate id {candidate.CandidateId} for card {candidate.CardId}.");
            }

            index.Add(candidate.CandidateId, TextOf(candidate, representation));
        }

        return new Ranker(index, candidates, byId, representation);
    }

    public string QueryText(EntityMention mention) => TextOf(mention, _representation);

    public string QueryText(LabelAnchor label) =>
        _representation == TextRepresentation.Name ? label.QueryVariant : throw new InvalidOperationException(
            "Context query text requires the mention table; use DataLoader.FindMention with the label.");

    public IReadOnlyList<RankedContext> SearchContexts(string query, int topK)
    {
        return [.. _index.Search(query, topK).Select(result =>
        {
            var candidate = _byId[result.Id];
            return new RankedContext(
                result.Id,
                candidate.CardId,
                candidate.Entity,
                candidate.File,
                candidate.Variant,
                TextOf(candidate, _representation),
                result.Score);
        })];
    }

    public IReadOnlyList<RankedEntity> SearchEntities(string query, string excludeCardId, int topK)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RankedEntity>();
        foreach (var ranked in SearchContexts(query, _candidates.Count))
        {
            if (ranked.CardId == excludeCardId || !seen.Add(ranked.CardId))
                continue;

            result.Add(new RankedEntity(
                ranked.CardId,
                ranked.Entity,
                ranked.File,
                ranked.Variant,
                ranked.CandidateId,
                ranked.Text,
                ranked.Score));
            if (result.Count == topK)
                break;
        }

        return result;
    }

    private static string TextOf(EntityMention mention, TextRepresentation representation) =>
        representation == TextRepresentation.Name ? mention.Variant : mention.Context;

    public int RankOf(string query, string candidateId)
    {
        var results = SearchContexts(query, _candidates.Count);
        for (var i = 0; i < results.Count; i++)
            if (results[i].CandidateId == candidateId)
                return i + 1;
        return 0;
    }

    public IReadOnlyDictionary<string, double> ScoreMap(string query)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var ranked in SearchContexts(query, _candidates.Count))
            result[ranked.CandidateId] = ranked.Score;
        return result;
    }
}

internal static class Evaluator
{
    public static EvaluationSummary EvaluateContextQueries(
        IReadOnlyList<LabelAnchor> labels,
        IReadOnlyList<EntityMention> mentions,
        IReadOnlyList<EntityMention> candidates,
        Ranker ranker,
        TextRepresentation representation)
    {
        var queries = new List<QueryMetrics>();
        foreach (var label in labels)
        {
            var relevant = candidates
                .Where(c => c.CardId == label.QueryCardId)
                .Select(c => c.CandidateId)
                .ToHashSet(StringComparer.Ordinal);
            if (relevant.Count == 0)
                throw new InvalidDataException($"No relevant candidates for query {label.QueryId}.");

            var queryText = representation == TextRepresentation.Name
                ? label.QueryVariant
                : DataLoader.FindMention(
                    mentions,
                    label.QueryCardId,
                    label.QueryFile,
                    label.QueryContextSha256).Context;
            var top10 = ranker.SearchContexts(queryText, 10).Select(x => x.CandidateId).ToList();
            queries.Add(new QueryMetrics(
                label.QueryId,
                label.QueryCardId,
                relevant.Count,
                Metrics.PrecisionAt5(top10, relevant),
                Metrics.NdcgAt10(top10, relevant),
                top10));
        }

        return new EvaluationSummary(
            Metrics.Average(queries, q => q.PrecisionAt5),
            Metrics.Average(queries, q => q.NdcgAt10),
            queries,
            queries.Count);
    }

    public static EvaluationSummary EvaluateEntityQueries(
        IReadOnlyList<LabelAnchor> positiveLabels,
        IReadOnlyList<EntityMention> mentions,
        Ranker ranker,
        TextRepresentation representation)
    {
        var queries = new List<QueryMetrics>();
        foreach (var label in positiveLabels)
        {
            var queryText = representation == TextRepresentation.Name
                ? label.QueryVariant
                : DataLoader.FindMention(
                    mentions,
                    label.QueryCardId,
                    label.QueryFile,
                    label.QueryContextSha256).Context;
            var top10 = ranker.SearchEntities(queryText, label.QueryCardId, 10)
                .Select(x => x.CardId)
                .ToList();
            var relevant = new HashSet<string>([label.NeighborCardId], StringComparer.Ordinal);
            queries.Add(new QueryMetrics(
                label.QueryId,
                label.QueryCardId,
                1,
                Metrics.PrecisionAt5(top10, relevant),
                Metrics.NdcgAt10(top10, relevant),
                top10));
        }

        return new EvaluationSummary(
            Metrics.Average(queries, q => q.PrecisionAt5),
            Metrics.Average(queries, q => q.NdcgAt10),
            queries,
            queries.Count);
    }

    public static (double Average, int Exact) AverageTop10EntityOverlap(
        Ranker bow,
        Ranker onnx,
        IReadOnlyList<EntityMention> queries)
    {
        if (queries.Count == 0)
            return (0, 0);

        var sum = 0.0;
        var exact = 0;
        foreach (var query in queries)
        {
            var queryText = bow.QueryText(query);
            var bowTop = bow.SearchEntities(queryText, query.CardId, 10).Select(x => x.CardId).ToList();
            var onnxTop = onnx.SearchEntities(queryText, query.CardId, 10).Select(x => x.CardId).ToList();
            var overlap = Metrics.Top10Overlap(bowTop, onnxTop);
            sum += overlap;
            if (overlap >= 1.0)
                exact++;
        }

        return (sum / queries.Count, exact);
    }
}
