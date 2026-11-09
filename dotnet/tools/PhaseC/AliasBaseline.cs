using EntityAnalyzer.Core.Analysis;

namespace PhaseC;

internal enum AliasResolutionStatus
{
    Unique,
    Ambiguous,
    Unknown,
}

internal sealed record AliasResolution(
    string Original,
    string Normalized,
    AliasResolutionStatus Status,
    int CardCount,
    string? CardId);

/// <summary>
/// Exact catalog-alias normalization for the cheap BoW baseline. It deliberately
/// does not use the label's query_card_id: an alias is resolved only when the
/// catalog maps the exact normalized text to one card. Ambiguous aliases are left
/// untouched instead of silently choosing a card.
/// </summary>
internal sealed class AliasNormalizer
{
    private readonly Dictionary<string, IReadOnlyList<CodexEntry>> _byAlias;
    private readonly Dictionary<string, string> _aliasTextByCard;

    private AliasNormalizer(
        Dictionary<string, IReadOnlyList<CodexEntry>> byAlias,
        Dictionary<string, string> aliasTextByCard)
    {
        _byAlias = byAlias;
        _aliasTextByCard = aliasTextByCard;
    }

    public int AliasCount => _byAlias.Count;

    public int DuplicateAliasCount => _byAlias.Values.Count(x =>
        x.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() > 1);

    public static AliasNormalizer Build(IEnumerable<CodexEntry> codex)
    {
        var byAlias = new Dictionary<string, List<CodexEntry>>(StringComparer.OrdinalIgnoreCase);
        var aliasTextByCard = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in codex)
        {
            var names = entry.Aliases
                .Append(entry.Name)
                .Append(entry.Canonical)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            aliasTextByCard[entry.Id] = string.Join(' ', names);

            foreach (var name in names)
            {
                var key = NormalizeKey(name);
                if (!byAlias.TryGetValue(key, out var entries))
                {
                    entries = [];
                    byAlias[key] = entries;
                }

                if (!entries.Any(x => x.Id == entry.Id))
                    entries.Add(entry);
            }
        }

        return new AliasNormalizer(
            byAlias.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<CodexEntry>)pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
            aliasTextByCard);
    }

    public AliasResolution Resolve(string text)
    {
        var key = NormalizeKey(text);
        if (!_byAlias.TryGetValue(key, out var entries) || entries.Count == 0)
            return new AliasResolution(text, text, AliasResolutionStatus.Unknown, 0, null);

        var cardIds = entries.Select(x => x.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (cardIds.Length != 1)
            return new AliasResolution(text, text, AliasResolutionStatus.Ambiguous, cardIds.Length, null);

        var entry = entries[0];
        var canonical = string.IsNullOrWhiteSpace(entry.Canonical) ? entry.Name : entry.Canonical;
        return new AliasResolution(text, canonical, AliasResolutionStatus.Unique, 1, entry.Id);
    }

    public string Normalize(string text) => Resolve(text).Normalized;

    public string AliasTextForCard(string cardId) =>
        _aliasTextByCard.TryGetValue(cardId, out var text) ? text : string.Empty;

    private static string NormalizeKey(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

internal static class AliasBaseline
{
    public static object Run(
        IReadOnlyDictionary<string, CodexEntry> codex,
        IReadOnlyList<EntityMention> candidates,
        IReadOnlyList<EntityMention> mentions,
        IReadOnlyList<LabelAnchor> positiveLabels,
        IReadOnlyList<LabelAnchor> hardNegativeLabels,
        EvaluationSummary rawBowSummary,
        EvaluationSummary onnxSummary)
    {
        var normalizer = AliasNormalizer.Build(codex.Values);
        var queryResolutions = positiveLabels
            .Select(label => (Label: label, Resolution: normalizer.Resolve(label.QueryVariant)))
            .ToArray();
        var candidateResolutions = candidates
            .Select(candidate => (Candidate: candidate, Resolution: normalizer.Resolve(candidate.Variant)))
            .ToArray();

        var normalizedCandidates = candidates
            .Select(candidate => candidate with { Variant = normalizer.Normalize(candidate.Variant) })
            .ToArray();
        var normalizedLabels = queryResolutions
            .Select(x => x.Label with { QueryVariant = x.Resolution.Normalized })
            .ToArray();

        var normalizedBow = Ranker.Build(
            new BagOfWordsEmbedder(256),
            normalizedCandidates,
            TextRepresentation.Name);
        var normalizedSummary = Evaluator.EvaluateContextQueries(
            normalizedLabels,
            mentions,
            normalizedCandidates,
            normalizedBow,
            TextRepresentation.Name);

        var expandedCandidates = candidates
            .Select(candidate => candidate with
            {
                Variant = candidate.Variant + " " + normalizer.AliasTextForCard(candidate.CardId),
            })
            .ToArray();
        var expandedBow = Ranker.Build(
            new BagOfWordsEmbedder(256),
            expandedCandidates,
            TextRepresentation.Name);
        var expandedSummary = Evaluator.EvaluateContextQueries(
            positiveLabels,
            mentions,
            expandedCandidates,
            expandedBow,
            TextRepresentation.Name);

        var rawByQuery = rawBowSummary.Queries.ToDictionary(x => x.QueryId, StringComparer.Ordinal);
        var onnxByQuery = onnxSummary.Queries.ToDictionary(x => x.QueryId, StringComparer.Ordinal);
        var normalizedByQuery = normalizedSummary.Queries.ToDictionary(x => x.QueryId, StringComparer.Ordinal);
        var expandedByQuery = expandedSummary.Queries.ToDictionary(x => x.QueryId, StringComparer.Ordinal);
        var normalizedByPair = normalizedLabels
            .Select((label, index) => (
                Label: label,
                Resolution: queryResolutions[index].Resolution,
                Metrics: normalizedByQuery[label.QueryId]))
            .ToArray();

        var perQuery = normalizedByPair.Select(item =>
        {
            var raw = rawByQuery[item.Label.QueryId];
            var onnx = onnxByQuery[item.Label.QueryId];
            var expanded = expandedByQuery[item.Label.QueryId];
            return new
            {
                pair_id = item.Label.PairId,
                query_id = item.Label.QueryId,
                query_variant = item.Resolution.Original,
                query_normalized = item.Label.QueryVariant,
                alias_status = item.Resolution.Status.ToString().ToLowerInvariant(),
                alias_card_count = item.Resolution.CardCount,
                p5_raw_bow = raw.PrecisionAt5,
                p5_alias_normalized_bow = item.Metrics.PrecisionAt5,
                p5_onnx = onnx.PrecisionAt5,
                p5_alias_expanded_bow = expanded.PrecisionAt5,
                top5_alias_normalized = item.Metrics.Top10.Take(5).ToArray(),
            };
        }).ToArray();

        var hardNegativeRanks = hardNegativeLabels.Select(label =>
        {
            var neighbor = DataLoader.FindMention(
                mentions,
                label.NeighborCardId,
                label.NeighborFile,
                label.NeighborContextSha256);
            var normalizedQuery = normalizer.Normalize(label.QueryVariant);
            return new
            {
                pair_id = label.PairId,
                query_id = label.QueryId,
                neighbor_card_id = label.NeighborCardId,
                rank_alias_normalized_bow = normalizedBow.RankOf(normalizedQuery, neighbor.CandidateId),
                rank_alias_expanded_bow = expandedBow.RankOf(label.QueryVariant, neighbor.CandidateId),
            };
        }).ToArray();

        return new
        {
            schema_version = 1,
            method = "exact catalog-alias normalization; ambiguous aliases are left unchanged; no query_card_id lookup",
            alias_catalog = new
            {
                unique_aliases = normalizer.AliasCount,
                duplicated_aliases = normalizer.DuplicateAliasCount,
                cards = codex.Count,
            },
            query_alias_resolution = new
            {
                total = queryResolutions.Length,
                unique = queryResolutions.Count(x => x.Resolution.Status == AliasResolutionStatus.Unique),
                ambiguous = queryResolutions.Count(x => x.Resolution.Status == AliasResolutionStatus.Ambiguous),
                unknown = queryResolutions.Count(x => x.Resolution.Status == AliasResolutionStatus.Unknown),
                unresolved = queryResolutions
                    .Where(x => x.Resolution.Status != AliasResolutionStatus.Unique)
                    .Select(x => new { pair_id = x.Label.PairId, query_variant = x.Resolution.Original })
                    .ToArray(),
            },
            candidate_alias_resolution = new
            {
                total = candidateResolutions.Length,
                unique = candidateResolutions.Count(x => x.Resolution.Status == AliasResolutionStatus.Unique),
                ambiguous = candidateResolutions.Count(x => x.Resolution.Status == AliasResolutionStatus.Ambiguous),
                unknown = candidateResolutions.Count(x => x.Resolution.Status == AliasResolutionStatus.Unknown),
                resolved_to_own_card = candidateResolutions.Count(x =>
                    x.Resolution.Status == AliasResolutionStatus.Unique
                    && x.Resolution.CardId == x.Candidate.CardId),
                resolved_to_other_card = candidateResolutions.Count(x =>
                    x.Resolution.Status == AliasResolutionStatus.Unique
                    && x.Resolution.CardId != x.Candidate.CardId),
            },
            metrics = new
            {
                queries = positiveLabels.Count,
                raw_bow = new
                {
                    precision_at_5 = rawBowSummary.PrecisionAt5,
                    ndcg_at_10 = rawBowSummary.NdcgAt10,
                },
                alias_normalized_bow = new
                {
                    precision_at_5 = normalizedSummary.PrecisionAt5,
                    ndcg_at_10 = normalizedSummary.NdcgAt10,
                },
                alias_expanded_bow = new
                {
                    precision_at_5 = expandedSummary.PrecisionAt5,
                    ndcg_at_10 = expandedSummary.NdcgAt10,
                    note = "diagnostic upper bound: candidate text is augmented with all aliases of its card_id; query stays raw",
                },
                onnx = new
                {
                    precision_at_5 = onnxSummary.PrecisionAt5,
                    ndcg_at_10 = onnxSummary.NdcgAt10,
                },
                alias_normalized_minus_raw_p5 =
                    normalizedSummary.PrecisionAt5 - rawBowSummary.PrecisionAt5,
                alias_normalized_minus_onnx_p5 =
                    normalizedSummary.PrecisionAt5 - onnxSummary.PrecisionAt5,
            },
            hard_negative_ranks = hardNegativeRanks,
            per_query = perQuery,
        };
    }
}
