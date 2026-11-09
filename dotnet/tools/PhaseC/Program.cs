using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Embedders.Onnx;

namespace PhaseC;

internal static class Program
{
    private const string ExpectedEntityTableSha256 =
        "1561C5949C32B3F49C32B49D66A4BCECCAE61118AB604C4EC4926F0E55337FB5";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Main(string[] args)
    {
        try
        {
            Run(args);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Run(string[] args)
    {
        var options = ParseOptions(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return;
        }

        var corpusPath = Require(options, "--corpus");
        var catalogPath = Require(options, "--catalog");
        var modelDirectory = Require(options, "--model");
        var labelsDirectory = options.Value("--labels") ?? FindLabelsDirectory();
        var outputDirectory = options.Value("--out")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "opencode",
                "phase-c-eval");
        var manifestPath = options.Value("--manifest");
        var trackCOutputDirectory = options.Value("--track-c-out");
        var aliasBaselineOutputDirectory = options.Value("--alias-baseline-out");
        var stabilityQueryCount = options.IntValue("--stability-queries", 100);
        var maxPerEntity = options.IntValue("--max-per-entity", 4);
        var representationName = options.Value("--representation") ?? "name";
        var representation = representationName.ToLowerInvariant() switch
        {
            "name" => TextRepresentation.Name,
            "context" => TextRepresentation.Context,
            _ => throw new ArgumentException("--representation must be 'name' or 'context'."),
        };
        var expectedCorpusSha256 = options.Value("--expected-corpus-sha256")
            ?? ExpectedEntityTableSha256;

        Directory.CreateDirectory(outputDirectory);
        var primaryLabelsPath = Path.Combine(labelsDirectory, "labels-primary.jsonl");
        var trackALabelsPath = Path.Combine(labelsDirectory, "labels-track-a.jsonl");
        var labelsMetaPath = Path.Combine(labelsDirectory, "labels.meta.json");

        Console.WriteLine("Phase C retrieval evaluation");
        Console.WriteLine($"  corpus: {corpusPath}");
        Console.WriteLine($"  catalog: {catalogPath}");
        Console.WriteLine($"  model: {modelDirectory}");
        Console.WriteLine($"  labels: {labelsDirectory}");
        Console.WriteLine($"  representation: {representationName}");
        Console.WriteLine($"  output: {outputDirectory}");

        var corpusSha256 = CsvUtil.Sha256File(corpusPath);
        if (!string.Equals(corpusSha256, expectedCorpusSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"entities_table.csv hash mismatch: expected {expectedCorpusSha256}, got {corpusSha256}. " +
                "Phase C must not change the extraction input.");
        }

        var labelsMetaSha256 = CsvUtil.Sha256File(labelsMetaPath);
        var primaryLabelsSha256 = CsvUtil.Sha256File(primaryLabelsPath);
        var trackALabelsSha256 = CsvUtil.Sha256File(trackALabelsPath);
        var meta = ReadJson(labelsMetaPath);
        var expectedPrimaryHash = meta.GetProperty("primary").GetProperty("label_file_sha256").GetString();
        var expectedTrackAHash = meta.GetProperty("track_a").GetProperty("label_file_sha256").GetString();
        if (!string.Equals(primaryLabelsSha256, expectedPrimaryHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(trackALabelsSha256, expectedTrackAHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Label files do not match labels.meta.json.");
        }

        var codex = DataLoader.LoadCodex(catalogPath);
        var mentions = DataLoader.LoadMentions(corpusPath, codex);
        var primaryLabels = DataLoader.LoadLabels(primaryLabelsPath);
        var trackALabels = DataLoader.LoadLabels(trackALabelsPath);
        var allCardIds = mentions.Select(m => m.CardId).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

        ValidateLabels(primaryLabels, mentions);
        ValidateLabels(trackALabels, mentions);

        var primaryPositive = primaryLabels.Where(x => x.Label == 1).ToList();
        var primaryHardNegative = primaryLabels.Where(x => x.Label == 0).ToList();
        var trackAPositive = trackALabels.Where(x => x.Label == 1).ToList();
        var trackANegative = trackALabels.Where(x => x.Label == 0).ToList();

        if (primaryPositive.Count != 11 || primaryHardNegative.Count != 11)
            throw new InvalidDataException("Primary labels must be 11 positive + 11 hard negative anchors.");
        if (primaryPositive.Select(x => x.QueryId).Distinct().Count() != 11)
            throw new InvalidDataException("Each primary positive anchor must have a distinct query_id.");
        if (trackAPositive.Count < 3 || trackALabels.Count < 8)
            throw new InvalidDataException("Track A needs at least 3 positives and 8 total pairs.");

        var candidatePoolB = DataLoader.BuildCandidatePool(
            mentions,
            mention => mention.IsLatin,
            allCardIds);
        if (candidatePoolB.Count != 275)
        {
            throw new InvalidDataException(
                $"Expected 275 Latin-script candidate mentions, got {candidatePoolB.Count}.");
        }

        var candidatePoolA = DataLoader.BuildCandidatePool(
            mentions,
            _ => true,
            allCardIds);
        Console.WriteLine($"  mentions={mentions.Count}, entities={allCardIds.Count}, " +
            $"primaryCandidates={candidatePoolB.Count}, graphCandidates={candidatePoolA.Count}");

        var primaryCandidateIds = candidatePoolB.Select(x => x.CandidateId).ToHashSet(StringComparer.Ordinal);
        foreach (var label in primaryLabels)
        {
            var neighbor = DataLoader.FindMention(
                mentions,
                label.NeighborCardId,
                label.NeighborFile,
                label.NeighborContextSha256);
            if (!primaryCandidateIds.Contains(neighbor.CandidateId))
            {
                throw new InvalidDataException(
                    $"Primary label {label.PairId} points to a neighbor outside the 275-row candidate pool.");
            }
        }

        using var onnxEmbedder = new OnnxE5Embedder(new OnnxE5Options
        {
            ModelDirectory = Path.GetFullPath(modelDirectory),
        });
        var bowEmbedder = new BagOfWordsEmbedder(256);

        Console.WriteLine("Building BoW primary index...");
        var bowB = Ranker.Build(bowEmbedder, candidatePoolB, representation);
        Console.WriteLine("Building ONNX primary index...");
        var onnxB = Ranker.Build(onnxEmbedder, candidatePoolB, representation);
        Console.WriteLine("Building BoW graph index...");
        var bowA = Ranker.Build(bowEmbedder, candidatePoolA, representation);
        Console.WriteLine("Building ONNX graph index...");
        var onnxA = Ranker.Build(onnxEmbedder, candidatePoolA, representation);

        Console.WriteLine("Evaluating primary cross-lingual labels...");
        var bowBSummary = Evaluator.EvaluateContextQueries(
            primaryPositive,
            mentions,
            candidatePoolB,
            bowB,
            representation);
        var onnxBSummary = Evaluator.EvaluateContextQueries(
            primaryPositive,
            mentions,
            candidatePoolB,
            onnxB,
            representation);

        var hardNegativeRanks = new List<object>();
        foreach (var label in primaryHardNegative)
        {
            var neighbor = DataLoader.FindMention(
                mentions,
                label.NeighborCardId,
                label.NeighborFile,
                label.NeighborContextSha256);
            hardNegativeRanks.Add(new
            {
                pair_id = label.PairId,
                query_id = label.QueryId,
                neighbor_card_id = label.NeighborCardId,
                rank_bow = bowB.RankOf(QueryText(label, mentions, representation), neighbor.CandidateId),
                rank_onnx = onnxB.RankOf(QueryText(label, mentions, representation), neighbor.CandidateId),
            });
        }

        Console.WriteLine("Evaluating supplemental graph labels...");
        var bowASummary = Evaluator.EvaluateEntityQueries(trackAPositive, mentions, bowA, representation);
        var onnxASummary = Evaluator.EvaluateEntityQueries(trackAPositive, mentions, onnxA, representation);

        var negativeTop5Hits = new List<object>();
        foreach (var label in trackANegative)
        {
            var queryText = QueryText(label, mentions, representation);
            var bowTop = bowA.SearchEntities(queryText, label.QueryCardId, 5).Select(x => x.CardId).ToList();
            var onnxTop = onnxA.SearchEntities(queryText, label.QueryCardId, 5).Select(x => x.CardId).ToList();
            negativeTop5Hits.Add(new
            {
                pair_id = label.PairId,
                query_id = label.QueryId,
                neighbor_card_id = label.NeighborCardId,
                in_top5_bow = bowTop.Contains(label.NeighborCardId, StringComparer.Ordinal),
                in_top5_onnx = onnxTop.Contains(label.NeighborCardId, StringComparer.Ordinal),
            });
        }

        var stabilityQueries = SelectStabilityQueries(mentions, allCardIds, stabilityQueryCount, maxPerEntity);
        Console.WriteLine($"Evaluating top-10 stability on {stabilityQueries.Count} query contexts...");
        var (stabilityOverlap, exactOverlapCount) = Evaluator.AverageTop10EntityOverlap(bowA, onnxA, stabilityQueries);

        if (trackCOutputDirectory is not null)
        {
            Console.WriteLine("Writing Track C difference analysis...");
            WriteTrackCAnalysis(
                trackALabels,
                mentions,
                bowA,
                onnxA,
                stabilityQueries,
                candidatePoolA.Count,
                trackALabelsSha256,
                Path.GetFullPath(trackCOutputDirectory));
        }

        if (aliasBaselineOutputDirectory is not null)
        {
            Console.WriteLine("Evaluating alias-normalized BoW baseline...");
            WriteAliasBaseline(
                codex,
                candidatePoolB,
                mentions,
                primaryPositive,
                primaryHardNegative,
                bowBSummary,
                onnxBSummary,
                Path.GetFullPath(aliasBaselineOutputDirectory));
        }

        var csvPath = Path.Combine(outputDirectory, "semantic_neighbors.csv");
        WriteSemanticNeighborsCsv(trackALabels, mentions, bowA, onnxA, candidatePoolA.Count, csvPath);
        var csvSha256 = CsvUtil.Sha256File(csvPath);

        var p5Delta = onnxBSummary.PrecisionAt5 - bowBSummary.PrecisionAt5;
        var ndcgDelta = onnxBSummary.NdcgAt10 - bowBSummary.NdcgAt10;
        var trackAP5Delta = onnxASummary.PrecisionAt5 - bowASummary.PrecisionAt5;
        var trackANdcgDelta = onnxASummary.NdcgAt10 - bowASummary.NdcgAt10;
        var relativeGate = p5Delta >= 0.10 && ndcgDelta >= 0.05;
        var absolutePass = onnxBSummary.PrecisionAt5 >= 0.40 && onnxBSummary.NdcgAt10 >= 0.45;
        var trackAOk = trackAPositive.Count >= 3
            && trackALabels.Count >= 8
            && onnxASummary.PrecisionAt5 >= bowASummary.PrecisionAt5 - 0.05
            && onnxASummary.NdcgAt10 >= bowASummary.NdcgAt10 - 0.05;
        var stabilityZone = StabilityZone(stabilityOverlap);
        var decision = Decide(absolutePass, relativeGate, trackAOk, stabilityOverlap);

        var report = new
        {
            schema_version = 1,
            generated_utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            inputs = new
            {
                entities_table = new { path = Path.GetFileName(corpusPath), sha256 = corpusSha256 },
                labels = new
                {
                    primary = new { file = Path.GetFileName(primaryLabelsPath), sha256 = primaryLabelsSha256 },
                    track_a = new { file = Path.GetFileName(trackALabelsPath), sha256 = trackALabelsSha256 },
                    meta = new { file = Path.GetFileName(labelsMetaPath), sha256 = labelsMetaSha256 },
                },
                catalog = new { path = Path.GetFileName(catalogPath), sha256 = CsvUtil.Sha256File(catalogPath) },
                manifest = manifestPath is null
                    ? null
                    : new { path = Path.GetFileName(manifestPath), sha256 = CsvUtil.Sha256File(manifestPath) },
                model = new
                {
                    model_file = new
                    {
                        file = "model_int8.onnx",
                        sha256 = CsvUtil.Sha256File(Path.Combine(modelDirectory, "onnx", "model_int8.onnx")),
                    },
                    tokenizer = new
                    {
                        file = "sentencepiece.bpe.model",
                        sha256 = CsvUtil.Sha256File(Path.Combine(modelDirectory, "sentencepiece.bpe.model")),
                    },
                },
            },
            corpus = new
            {
                mention_count = mentions.Count,
                matched_entity_count = allCardIds.Count,
                primary_candidate_count = candidatePoolB.Count,
                graph_candidate_count = candidatePoolA.Count,
            },
            representation = representationName.ToLowerInvariant(),
            primary_cross_lingual = new
            {
                queries = primaryPositive.Count,
                positive_anchors = primaryPositive.Count,
                hard_negative_anchors = primaryHardNegative.Count,
                relevance_rule = "all candidate rows with the query card_id are relevant; everything else is 0",
                bow = new
                {
                    precision_at_5 = bowBSummary.PrecisionAt5,
                    ndcg_at_10 = bowBSummary.NdcgAt10,
                    query_count = bowBSummary.QueryCount,
                },
                onnx = new
                {
                    precision_at_5 = onnxBSummary.PrecisionAt5,
                    ndcg_at_10 = onnxBSummary.NdcgAt10,
                    query_count = onnxBSummary.QueryCount,
                },
                delta = new
                {
                    precision_at_5 = p5Delta,
                    ndcg_at_10 = ndcgDelta,
                    relative_gate = relativeGate,
                },
                absolute_pass = absolutePass,
                hard_negative_ranks = hardNegativeRanks,
                per_query = BuildPerQueryRows(primaryPositive, bowBSummary, onnxBSummary),
            },
            supplemental_entity_graph = new
            {
                pair_count = trackALabels.Count,
                positive_queries = trackAPositive.Count,
                negative_queries = trackANegative.Count,
                note = "P@5 and nDCG@10 are over the 3 positive queries; n=3 is supplemental only.",
                bow = new
                {
                    precision_at_5 = bowASummary.PrecisionAt5,
                    ndcg_at_10 = bowASummary.NdcgAt10,
                    query_count = bowASummary.QueryCount,
                },
                onnx = new
                {
                    precision_at_5 = onnxASummary.PrecisionAt5,
                    ndcg_at_10 = onnxASummary.NdcgAt10,
                    query_count = onnxASummary.QueryCount,
                },
                delta = new { precision_at_5 = trackAP5Delta, ndcg_at_10 = trackANdcgDelta },
                not_worse_than_bow_by_0_05 = trackAOk,
                negative_top5_hits = negativeTop5Hits,
                per_query = BuildPerQueryRows(trackAPositive, bowASummary, onnxASummary),
            },
            stability = new
            {
                query_contexts = stabilityQueries.Count,
                max_per_entity = maxPerEntity,
                candidate_pool = candidatePoolA.Count,
                scripts = new
                {
                    cyrillic = stabilityQueries.Count(x => x.IsCyrillic),
                    latin = stabilityQueries.Count(x => x.IsLatin),
                    other = stabilityQueries.Count(x => !x.IsCyrillic && !x.IsLatin),
                },
                average_top10_overlap = stabilityOverlap,
                identical_top10_queries = exactOverlapCount,
                zone = stabilityZone,
            },
            decision = new
            {
                absolute_thresholds = "P@5: <0.40 fail, 0.40-0.65 acceptable, >0.65 wins; nDCG@10: <0.45 fail, 0.45-0.70 acceptable, >0.70 wins",
                relative_gate = "ONNX-BoW >= +0.10 P@5 and >= +0.05 nDCG@10",
                verdict = decision,
                verdict_note = decision == "GO (full)"
                    ? "Cross-lingual gate and supplemental graph gate pass; stability is <= 70%."
                    : decision == "GO (cross-lingual only)"
                        ? "Cross-lingual gate passes, but graph is unavailable/worse or top-10 stability is > 70%."
                        : decision == "HOLD"
                            ? "Absolute gate passes but the relative gate or label quality needs work."
                            : "Cross-lingual absolute gate fails; do not roll out Phase C.",
            },
            outputs = new
            {
                semantic_neighbors_csv = new { file = Path.GetFileName(csvPath), sha256 = csvSha256 },
                report_markdown = "phase-c-report.md",
                report_json = "phase-c-report.json",
            },
        };

        var reportJsonPath = Path.Combine(outputDirectory, "phase-c-report.json");
        File.WriteAllText(reportJsonPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine, Encoding.UTF8);
        var reportMarkdownPath = Path.Combine(outputDirectory, "phase-c-report.md");
        File.WriteAllText(
            reportMarkdownPath,
            BuildMarkdownReport(
                corpusSha256,
                primaryLabelsSha256,
                trackALabelsSha256,
                manifestPath,
                modelDirectory,
                representationName,
                candidatePoolB.Count,
                candidatePoolA.Count,
                bowBSummary,
                onnxBSummary,
                p5Delta,
                ndcgDelta,
                relativeGate,
                absolutePass,
                hardNegativeRanks,
                bowASummary,
                onnxASummary,
                trackAP5Delta,
                trackANdcgDelta,
                trackAOk,
                negativeTop5Hits,
                stabilityQueries,
                stabilityOverlap,
                exactOverlapCount,
                stabilityZone,
                decision),
            Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine($"Primary P@5: BoW {bowBSummary.PrecisionAt5:F3} -> ONNX {onnxBSummary.PrecisionAt5:F3} " +
            $"(delta {p5Delta:+0.000;-0.000;0.000})");
        Console.WriteLine($"Primary nDCG@10: BoW {bowBSummary.NdcgAt10:F3} -> ONNX {onnxBSummary.NdcgAt10:F3} " +
            $"(delta {ndcgDelta:+0.000;-0.000;0.000})");
        Console.WriteLine($"Track A P@5: BoW {bowASummary.PrecisionAt5:F3} -> ONNX {onnxASummary.PrecisionAt5:F3} " +
            $"(delta {trackAP5Delta:+0.000;-0.000;0.000})");
        Console.WriteLine($"Track A nDCG@10: BoW {bowASummary.NdcgAt10:F3} -> ONNX {onnxASummary.NdcgAt10:F3} " +
            $"(delta {trackANdcgDelta:+0.000;-0.000;0.000})");
        Console.WriteLine($"Top-10 overlap: {stabilityOverlap:P1} over {stabilityQueries.Count} contexts " +
            $"({stabilityZone}); identical: {exactOverlapCount}");
        Console.WriteLine($"Verdict: {decision}");
        Console.WriteLine($"CSV: {csvPath}");
        Console.WriteLine($"Report: {reportMarkdownPath}");
        Console.WriteLine($"JSON: {reportJsonPath}");
    }

    private static void ValidateLabels(IReadOnlyList<LabelAnchor> labels, IReadOnlyList<EntityMention> mentions)
    {
        foreach (var label in labels)
        {
            _ = DataLoader.FindMention(
                mentions,
                label.QueryCardId,
                label.QueryFile,
                label.QueryContextSha256);
            _ = DataLoader.FindMention(
                mentions,
                label.NeighborCardId,
                label.NeighborFile,
                label.NeighborContextSha256);
        }
    }

    private static IReadOnlyList<EntityMention> SelectStabilityQueries(
        IReadOnlyList<EntityMention> mentions,
        IReadOnlySet<string> cardIds,
        int count,
        int maxPerEntity)
    {
        var selected = new List<EntityMention>();
        var perEntity = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var mention in mentions
            .Where(m => cardIds.Contains(m.CardId))
            .GroupBy(m => (m.CardId, m.Context))
            .Select(group => group.First())
            .OrderBy(m => CsvUtil.Sha256Text(m.CardId + "\0" + m.Context), StringComparer.Ordinal)
            .ThenBy(m => m.CardId, StringComparer.Ordinal)
            .ThenBy(m => m.ContextSha256, StringComparer.Ordinal))
        {
            perEntity.TryGetValue(mention.CardId, out var countForEntity);
            if (countForEntity >= maxPerEntity)
                continue;

            selected.Add(mention);
            perEntity[mention.CardId] = countForEntity + 1;
            if (selected.Count == count)
                break;
        }

        if (selected.Count == 0)
            throw new InvalidDataException("No stability query contexts were selected.");

        if (selected.Count < count)
        {
            Console.WriteLine(
                $"  warning: only {selected.Count} stability query contexts available " +
                $"with max {maxPerEntity} per entity; requested {count}.");
        }

        return selected;
    }

    private static void WriteSemanticNeighborsCsv(
        IReadOnlyList<LabelAnchor> labels,
        IReadOnlyList<EntityMention> mentions,
        Ranker bow,
        Ranker onnx,
        int candidateCount,
        string path)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n";
        writer.WriteLine("query_entity_id;query_entity_text;rank;neighbor_entity_id;neighbor_text;score_bow;score_onnx");

        foreach (var label in labels)
        {
            var query = DataLoader.FindMention(
                mentions,
                label.QueryCardId,
                label.QueryFile,
                label.QueryContextSha256);
            var queryText = bow.QueryText(query);
            var bowScores = bow.ScoreMap(queryText);
            var onnxScores = onnx.ScoreMap(queryText);
            var bowTop = TopEntities(bow.SearchContexts(queryText, candidateCount), label.QueryCardId, 5);
            var onnxTop = TopEntities(onnx.SearchContexts(queryText, candidateCount), label.QueryCardId, 5);

            var pairs = new Dictionary<string, CsvPair>(StringComparer.Ordinal);
            for (var i = 0; i < bowTop.Count; i++)
            {
                var candidate = bowTop[i];
                pairs[candidate.CardId] = new CsvPair(
                    candidate,
                    i + 1,
                    int.MaxValue,
                    bowScores.TryGetValue(candidate.CandidateId, out var bowScore) ? bowScore : 0,
                    onnxScores.TryGetValue(candidate.CandidateId, out var onnxScore) ? onnxScore : 0);
            }

            for (var i = 0; i < onnxTop.Count; i++)
            {
                var candidate = onnxTop[i];
                if (pairs.TryGetValue(candidate.CardId, out var existing))
                {
                    pairs[candidate.CardId] = existing with { OnnxRank = i + 1 };
                }
                else
                {
                    pairs[candidate.CardId] = new CsvPair(
                        candidate,
                        int.MaxValue,
                        i + 1,
                        bowScores.TryGetValue(candidate.CandidateId, out var bowScore) ? bowScore : 0,
                        onnxScores.TryGetValue(candidate.CandidateId, out var onnxScore) ? onnxScore : 0);
                }
            }

            var ordered = pairs.Values
                .OrderBy(pair => Math.Min(pair.BowRank, pair.OnnxRank))
                .ThenBy(pair => pair.Neighbor.CardId, StringComparer.Ordinal)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var pair = ordered[i];
                writer.Write(CsvUtil.Quote(label.QueryCardId));
                writer.Write(';');
                writer.Write(CsvUtil.Quote(queryText));
                writer.Write(';');
                writer.Write((i + 1).ToString(CultureInfo.InvariantCulture));
                writer.Write(';');
                writer.Write(CsvUtil.Quote(pair.Neighbor.CardId));
                writer.Write(';');
                writer.Write(CsvUtil.Quote(pair.Neighbor.Text));
                writer.Write(';');
                writer.Write(CsvUtil.Score(pair.BowScore));
                writer.Write(';');
                writer.Write(CsvUtil.Score(pair.OnnxScore));
                writer.Write('\n');
            }
        }
    }

    private static void WriteAliasBaseline(
        IReadOnlyDictionary<string, CodexEntry> codex,
        IReadOnlyList<EntityMention> candidates,
        IReadOnlyList<EntityMention> mentions,
        IReadOnlyList<LabelAnchor> positiveLabels,
        IReadOnlyList<LabelAnchor> hardNegativeLabels,
        EvaluationSummary rawBowSummary,
        EvaluationSummary onnxSummary,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var analysis = AliasBaseline.Run(
            codex,
            candidates,
            mentions,
            positiveLabels,
            hardNegativeLabels,
            rawBowSummary,
            onnxSummary);
        var path = Path.Combine(outputDirectory, "alias-baseline.json");
        File.WriteAllText(path, JsonSerializer.Serialize(analysis, JsonOptions) + Environment.NewLine, Encoding.UTF8);
    }

    private static void WriteTrackCAnalysis(
        IReadOnlyList<LabelAnchor> labels,
        IReadOnlyList<EntityMention> mentions,
        Ranker bow,
        Ranker onnx,
        IReadOnlyList<EntityMention> stabilityQueries,
        int candidateCount,
        string trackALabelsSha256,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var canonicalByCard = mentions
            .GroupBy(m => m.CardId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Canonical, StringComparer.Ordinal);

        var queries = new List<object>();
        var unionRows = new List<TrackCUnionRow>();
        foreach (var label in labels)
        {
            var queryMention = DataLoader.FindMention(
                mentions,
                label.QueryCardId,
                label.QueryFile,
                label.QueryContextSha256);
            var queryText = bow.QueryText(queryMention);
            var queryCanonical = canonicalByCard.TryGetValue(label.QueryCardId, out var canonical)
                ? canonical
                : label.QueryCanonical;
            var queryScript = ScriptOf(label.QueryVariant);
            var bowTop = BuildTrackCTopRows(bow.SearchEntities(queryText, label.QueryCardId, 5), canonicalByCard);
            var onnxTop = BuildTrackCTopRows(onnx.SearchEntities(queryText, label.QueryCardId, 5), canonicalByCard);
            var union = BuildTrackCUnionRows(
                label,
                queryCanonical,
                queryScript,
                bowTop,
                onnxTop);
            unionRows.AddRange(union);
            queries.Add(new
            {
                pair_id = label.PairId,
                query_id = label.QueryId,
                kind = label.Kind,
                label = label.Label,
                query_card_id = label.QueryCardId,
                query_canonical = queryCanonical,
                query_variant = label.QueryVariant,
                query_script = queryScript,
                bow_top5 = bowTop.Select(RowJson),
                onnx_top5 = onnxTop.Select(RowJson),
                union = union.Select(RowJson),
            });
        }

        var aggregate = new
        {
            query_count = labels.Count,
            candidate_contexts = candidateCount,
            union_rows = unionRows.Count,
            both = unionRows.Count(x => x.Membership == "both"),
            onnx_only = unionRows.Count(x => x.Membership == "onnx_only"),
            bow_only = unionRows.Count(x => x.Membership == "bow_only"),
            onnx_only_cross_lingual = unionRows.Count(x => x.Membership == "onnx_only" && x.LanguageRelation == "cross_lingual"),
            onnx_only_intra_lingual = unionRows.Count(x => x.Membership == "onnx_only" && x.LanguageRelation == "intra_lingual"),
            onnx_only_unknown = unionRows.Count(x => x.Membership == "onnx_only" && x.LanguageRelation == "unknown"),
            bow_only_cross_lingual = unionRows.Count(x => x.Membership == "bow_only" && x.LanguageRelation == "cross_lingual"),
            bow_only_intra_lingual = unionRows.Count(x => x.Membership == "bow_only" && x.LanguageRelation == "intra_lingual"),
            bow_only_unknown = unionRows.Count(x => x.Membership == "bow_only" && x.LanguageRelation == "unknown"),
            bow_zero_score_rows = unionRows.Count(x => x.BowZeroScore),
            bow_zero_score_onnx_only = unionRows.Count(x => x.Membership == "onnx_only" && x.BowZeroScore),
            bow_zero_score_bow_only = unionRows.Count(x => x.Membership == "bow_only" && x.BowZeroScore),
            bow_only_cross_lingual_zero_score = unionRows.Count(x => x.Membership == "bow_only" && x.LanguageRelation == "cross_lingual" && x.BowZeroScore),
            bow_only_cross_lingual_nonzero = unionRows.Count(x => x.Membership == "bow_only" && x.LanguageRelation == "cross_lingual" && !x.BowZeroScore),
        };

        var stability = AnalyzeStabilityDifferences(bow, onnx, stabilityQueries, canonicalByCard);
        var analysis = new
        {
            schema_version = 1,
            generated_utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            purpose = "Track C exploratory analysis: what ONNX and BoW contribute in top-5/top-10; not a gate and not a production output.",
            track_a_labels_sha256 = trackALabelsSha256,
            track_a = new
            {
                pair_count = labels.Count,
                note = "8 Track A queries; top-5 per method, union deduplicated by neighbor_card_id; manual semantic classes are recorded in docs/PHASE_C_TRACK_C.md.",
                aggregate,
                queries,
            },
            stability_differences = stability,
        };

        var path = Path.Combine(outputDirectory, "track-c-analysis.json");
        File.WriteAllText(path, JsonSerializer.Serialize(analysis, JsonOptions) + Environment.NewLine, Encoding.UTF8);
        Console.WriteLine($"Track C analysis: {path}");
    }

    private static IReadOnlyList<TrackCTopRow> BuildTrackCTopRows(
        IReadOnlyList<RankedEntity> ranking,
        IReadOnlyDictionary<string, string> canonicalByCard)
    {
        var result = new List<TrackCTopRow>();
        for (var i = 0; i < ranking.Count; i++)
        {
            var row = ranking[i];
            result.Add(new TrackCTopRow(
                i + 1,
                row.CardId,
                row.Entity,
                canonicalByCard.TryGetValue(row.CardId, out var canonical) ? canonical : string.Empty,
                row.Variant,
                row.File,
                row.Score,
                ScriptOf(row.Variant)));
        }

        return result;
    }

    private static IReadOnlyList<TrackCUnionRow> BuildTrackCUnionRows(
        LabelAnchor label,
        string queryCanonical,
        string queryScript,
        IReadOnlyList<TrackCTopRow> bowTop,
        IReadOnlyList<TrackCTopRow> onnxTop)
    {
        var bowByCard = bowTop.ToDictionary(x => x.CardId, StringComparer.Ordinal);
        var onnxByCard = onnxTop.ToDictionary(x => x.CardId, StringComparer.Ordinal);
        var cardIds = bowByCard.Keys
            .Concat(onnxByCard.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(cardId => Math.Min(
                bowByCard.TryGetValue(cardId, out var bow) ? bow.Rank : int.MaxValue,
                onnxByCard.TryGetValue(cardId, out var onnx) ? onnx.Rank : int.MaxValue))
            .ThenBy(cardId => cardId, StringComparer.Ordinal)
            .ToList();

        var result = new List<TrackCUnionRow>();
        foreach (var cardId in cardIds)
        {
            var inBow = bowByCard.TryGetValue(cardId, out var bowRow);
            var inOnnx = onnxByCard.TryGetValue(cardId, out var onnxRow);
            var row = inOnnx ? onnxRow! : bowRow!;
            var membership = inBow && inOnnx ? "both" : inOnnx ? "onnx_only" : "bow_only";
            result.Add(new TrackCUnionRow(
                label.PairId,
                label.QueryId,
                label.Kind,
                label.Label,
                label.QueryCardId,
                queryCanonical,
                label.QueryVariant,
                queryScript,
                cardId,
                row.Entity,
                row.Canonical,
                row.Variant,
                row.File,
                row.Script,
                inBow ? bowRow!.Rank : null,
                inOnnx ? onnxRow!.Rank : null,
                inBow ? bowRow!.Score : null,
                inOnnx ? onnxRow!.Score : null,
                membership,
                LanguageRelation(queryScript, row.Script),
                inBow && bowRow!.Score <= 0.0000001,
                inOnnx && onnxRow!.Score <= 0.0000001));
        }

        return result;
    }

    private static object AnalyzeStabilityDifferences(
        Ranker bow,
        Ranker onnx,
        IReadOnlyList<EntityMention> queries,
        IReadOnlyDictionary<string, string> canonicalByCard)
    {
        const int topK = 10;
        var both = 0;
        var onnxOnly = 0;
        var bowOnly = 0;
        var onnxOnlyCross = 0;
        var onnxOnlyIntra = 0;
        var onnxOnlyUnknown = 0;
        var bowOnlyCross = 0;
        var bowOnlyIntra = 0;
        var bowOnlyUnknown = 0;
        var bowOnlyZeroScore = 0;
        var bowOnlyNonzero = 0;
        var bowOnlyCrossZeroScore = 0;
        var bowOnlyCrossNonzero = 0;
        var onnxOnlyZeroScore = 0;
        var bothBowZeroScore = 0;
        var examples = new List<object>();

        foreach (var query in queries)
        {
            var queryText = bow.QueryText(query);
            var queryScript = ScriptOf(query.Variant);
            var queryCanonical = canonicalByCard.TryGetValue(query.CardId, out var canonical)
                ? canonical
                : string.Empty;
            var bowTop = bow.SearchEntities(queryText, query.CardId, topK);
            var onnxTop = onnx.SearchEntities(queryText, query.CardId, topK);
            var bowByCard = bowTop.ToDictionary(x => x.CardId, StringComparer.Ordinal);
            var onnxByCard = onnxTop
                .Select((row, index) => (row, rank: index + 1))
                .ToDictionary(x => x.row.CardId, x => x, StringComparer.Ordinal);
            var cardIds = bowByCard.Keys.Concat(onnxByCard.Keys).Distinct(StringComparer.Ordinal);
            foreach (var cardId in cardIds)
            {
                var inBow = bowByCard.TryGetValue(cardId, out var bowRow);
                var inOnnx = onnxByCard.TryGetValue(cardId, out var onnxEntry);
                var row = inOnnx ? onnxEntry.row : bowRow!;
                var language = LanguageRelation(queryScript, ScriptOf(row.Variant));
                if (inBow && inOnnx)
                {
                    both++;
                    if (bowRow!.Score <= 0.0000001)
                        bothBowZeroScore++;
                }
                else if (inOnnx)
                {
                    onnxOnly++;
                    if (onnxEntry.row.Score <= 0.0000001)
                        onnxOnlyZeroScore++;
                    if (language == "cross_lingual") onnxOnlyCross++;
                    else if (language == "intra_lingual") onnxOnlyIntra++;
                    else onnxOnlyUnknown++;
                    if (examples.Count < 25)
                    {
                        examples.Add(new
                        {
                            query_card_id = query.CardId,
                            query_canonical = queryCanonical,
                            query_variant = query.Variant,
                            query_script = queryScript,
                            neighbor_card_id = row.CardId,
                            neighbor_canonical = canonicalByCard.TryGetValue(row.CardId, out var neighborCanonical)
                                ? neighborCanonical
                                : string.Empty,
                            neighbor_variant = row.Variant,
                            neighbor_script = ScriptOf(row.Variant),
                            onnx_rank = onnxEntry.rank,
                            onnx_score = onnxEntry.row.Score,
                            language_relation = language,
                        });
                    }
                }
                else
                {
                    bowOnly++;
                    if (bowRow!.Score <= 0.0000001)
                        bowOnlyZeroScore++;
                    else
                        bowOnlyNonzero++;
                    if (language == "cross_lingual")
                    {
                        bowOnlyCross++;
                        if (bowRow.Score <= 0.0000001)
                            bowOnlyCrossZeroScore++;
                        else
                            bowOnlyCrossNonzero++;
                    }
                    else if (language == "intra_lingual") bowOnlyIntra++;
                    else bowOnlyUnknown++;
                }
            }
        }

        return new
        {
            query_contexts = queries.Count,
            top_k = topK,
            both,
            onnx_only = onnxOnly,
            bow_only = bowOnly,
            onnx_only_cross_lingual = onnxOnlyCross,
            onnx_only_intra_lingual = onnxOnlyIntra,
            onnx_only_unknown = onnxOnlyUnknown,
            bow_only_cross_lingual = bowOnlyCross,
            bow_only_intra_lingual = bowOnlyIntra,
            bow_only_unknown = bowOnlyUnknown,
            bow_only_zero_score = bowOnlyZeroScore,
            bow_only_nonzero = bowOnlyNonzero,
            bow_only_cross_lingual_zero_score = bowOnlyCrossZeroScore,
            bow_only_cross_lingual_nonzero = bowOnlyCrossNonzero,
            onnx_only_zero_score = onnxOnlyZeroScore,
            both_bow_zero_score = bothBowZeroScore,
            onnx_only_cross_lingual_share = onnxOnly == 0 ? 0 : onnxOnlyCross / (double)onnxOnly,
            bow_only_cross_lingual_share = bowOnly == 0 ? 0 : bowOnlyCross / (double)bowOnly,
            examples,
        };
    }

    private static string ScriptOf(string text)
    {
        var cyrillic = false;
        var latin = false;
        foreach (var character in text)
        {
            if (character >= '\u0400' && character <= '\u04FF')
                cyrillic = true;
            else if ((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z'))
                latin = true;
        }

        return cyrillic && latin ? "mixed"
            : cyrillic ? "cyrillic"
            : latin ? "latin"
            : "other";
    }

    private static string LanguageRelation(string queryScript, string neighborScript) =>
        (queryScript == "cyrillic" && neighborScript == "latin")
        || (queryScript == "latin" && neighborScript == "cyrillic")
            ? "cross_lingual"
            : queryScript == neighborScript && queryScript is "cyrillic" or "latin"
                ? "intra_lingual"
                : "unknown";

    private static object RowJson(TrackCTopRow row) => new
    {
        rank = row.Rank,
        card_id = row.CardId,
        entity = row.Entity,
        canonical = row.Canonical,
        variant = row.Variant,
        file = row.File,
        script = row.Script,
        score = row.Score,
    };

    private static object RowJson(TrackCUnionRow row) => new
    {
        neighbor_card_id = row.CardId,
        neighbor_entity = row.Entity,
        neighbor_canonical = row.Canonical,
        neighbor_variant = row.Variant,
        neighbor_file = row.File,
        neighbor_script = row.NeighborScript,
        bow_rank = row.BowRank,
        onnx_rank = row.OnnxRank,
        score_bow = row.BowScore,
        score_onnx = row.OnnxScore,
        membership = row.Membership,
        language_relation = row.LanguageRelation,
        bow_zero_score_tie = row.BowZeroScore,
        onnx_zero_score_tie = row.OnnxZeroScore,
    };

    private sealed record TrackCTopRow(
        int Rank,
        string CardId,
        string Entity,
        string Canonical,
        string Variant,
        string File,
        double Score,
        string Script);

    private sealed record TrackCUnionRow(
        string PairId,
        string QueryId,
        string Kind,
        int Label,
        string QueryCardId,
        string QueryCanonical,
        string QueryVariant,
        string QueryScript,
        string CardId,
        string Entity,
        string Canonical,
        string Variant,
        string File,
        string NeighborScript,
        int? BowRank,
        int? OnnxRank,
        double? BowScore,
        double? OnnxScore,
        string Membership,
        string LanguageRelation,
        bool BowZeroScore,
        bool OnnxZeroScore);


    private static IReadOnlyList<RankedContext> TopEntities(
        IReadOnlyList<RankedContext> ranking,
        string excludeCardId,
        int topK)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RankedContext>();
        foreach (var candidate in ranking)
        {
            if (candidate.CardId == excludeCardId || !seen.Add(candidate.CardId))
                continue;

            result.Add(candidate);
            if (result.Count == topK)
                break;
        }

        return result;
    }

    private sealed record CsvPair(
        RankedContext Neighbor,
        int BowRank,
        int OnnxRank,
        double BowScore,
        double OnnxScore);

    private static IReadOnlyList<object> BuildPerQueryRows(
        IReadOnlyList<LabelAnchor> labels,
        EvaluationSummary bow,
        EvaluationSummary onnx)
    {
        var rows = new List<object>();
        for (var i = 0; i < labels.Count; i++)
        {
            rows.Add(new
            {
                query_id = labels[i].QueryId,
                query_card_id = labels[i].QueryCardId,
                relevant_count = bow.Queries[i].RelevantCount,
                bow_precision_at_5 = bow.Queries[i].PrecisionAt5,
                bow_ndcg_at_10 = bow.Queries[i].NdcgAt10,
                onnx_precision_at_5 = onnx.Queries[i].PrecisionAt5,
                onnx_ndcg_at_10 = onnx.Queries[i].NdcgAt10,
                top10_overlap = Metrics.Top10Overlap(bow.Queries[i].Top10, onnx.Queries[i].Top10),
            });
        }

        return rows;
    }

    private static string QueryText(
        LabelAnchor label,
        IReadOnlyList<EntityMention> mentions,
        TextRepresentation representation) =>
        representation == TextRepresentation.Name
            ? label.QueryVariant
            : DataLoader.FindMention(
                mentions,
                label.QueryCardId,
                label.QueryFile,
                label.QueryContextSha256).Context;

    private static string StabilityZone(double overlap) =>
        overlap < 0.30 ? "fundamentally_different (<30%)"
        : overlap <= 0.70 ? "onnx_complements_bow (30-70%)"
        : "onnx_adds_little (>70%)";

    private static string Decide(bool absolutePass, bool relativeGate, bool trackAOk, double stability)
    {
        if (!absolutePass)
            return "NO-GO";
        if (!relativeGate)
            return "HOLD";
        if (trackAOk && stability <= 0.70)
            return "GO (full)";
        return "GO (cross-lingual only)";
    }

    private static string BuildMarkdownReport(
        string corpusSha256,
        string primaryLabelsSha256,
        string trackALabelsSha256,
        string? manifestPath,
        string modelDirectory,
        string representation,
        int candidatePoolB,
        int candidatePoolA,
        EvaluationSummary bowB,
        EvaluationSummary onnxB,
        double p5Delta,
        double ndcgDelta,
        bool relativeGate,
        bool absolutePass,
        IReadOnlyList<object> hardNegativeRanks,
        EvaluationSummary bowA,
        EvaluationSummary onnxA,
        double trackAP5Delta,
        double trackANdcgDelta,
        bool trackAOk,
        IReadOnlyList<object> negativeTop5Hits,
        IReadOnlyList<EntityMention> stabilityQueries,
        double stabilityOverlap,
        int exactOverlapCount,
        string stabilityZone,
        string decision)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Phase C retrieval results");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTime.UtcNow:O}");
        builder.AppendLine();
        builder.AppendLine("## Inputs");
        builder.AppendLine();
        builder.AppendLine($"- `entities_table.csv` SHA-256: `{corpusSha256}`");
        builder.AppendLine($"- `labels-primary.jsonl` SHA-256: `{primaryLabelsSha256}`");
        builder.AppendLine($"- `labels-track-a.jsonl` SHA-256: `{trackALabelsSha256}`");
        if (manifestPath is not null)
            builder.AppendLine($"- manifest SHA-256: `{CsvUtil.Sha256File(manifestPath)}`");
        builder.AppendLine($"- candidate contexts: primary `{candidatePoolB}`, graph `{candidatePoolA}`");
        builder.AppendLine($"- representation: **{representation}** (primary gate)");
        builder.AppendLine($"- model directory: `{Path.GetFileName(Path.GetFullPath(modelDirectory))}`");
        builder.AppendLine();
        builder.AppendLine("## Primary cross-lingual set (11 queries)");
        builder.AppendLine();
        builder.AppendLine("| metric | BoW | ONNX | ONNX - BoW |");
        builder.AppendLine("|---|---:|---:|---:|");
        builder.AppendLine($"| Precision@5 | {bowB.PrecisionAt5:F3} | {onnxB.PrecisionAt5:F3} | {p5Delta:+0.000;-0.000;0.000} |");
        builder.AppendLine($"| nDCG@10 | {bowB.NdcgAt10:F3} | {onnxB.NdcgAt10:F3} | {ndcgDelta:+0.000;-0.000;0.000} |");
        builder.AppendLine();
        builder.AppendLine($"- Absolute gate (ONNX): **{(absolutePass ? "PASS" : "FAIL")}**");
        builder.AppendLine($"- Relative gate (>= +0.10 P@5 and >= +0.05 nDCG@10): **{(relativeGate ? "PASS" : "FAIL")}**");
        builder.AppendLine();
        builder.AppendLine("Hard-negative anchors (0 labels) were checked for false positives:");
        builder.AppendLine();
        builder.AppendLine("| pair | rank BoW | rank ONNX |");
        builder.AppendLine("|---|---:|---:|");
        foreach (var row in hardNegativeRanks)
        {
            var element = JsonSerializer.SerializeToElement(row);
            builder.AppendLine(
                $"| {element.GetProperty("pair_id").GetString()} | " +
                $"{element.GetProperty("rank_bow").GetInt32()} | " +
                $"{element.GetProperty("rank_onnx").GetInt32()} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Supplemental entity graph (3 positive queries)");
        builder.AppendLine();
        builder.AppendLine("| metric | BoW | ONNX | ONNX - BoW |");
        builder.AppendLine("|---|---:|---:|---:|");
        builder.AppendLine($"| Precision@5 | {bowA.PrecisionAt5:F3} | {onnxA.PrecisionAt5:F3} | {trackAP5Delta:+0.000;-0.000;0.000} |");
        builder.AppendLine($"| nDCG@10 | {bowA.NdcgAt10:F3} | {onnxA.NdcgAt10:F3} | {trackANdcgDelta:+0.000;-0.000;0.000} |");
        builder.AppendLine();
        builder.AppendLine($"- ONNX not worse than BoW by more than 0.05: **{(trackAOk ? "PASS" : "FAIL")}**");
        builder.AppendLine("- This is a 3-query supplemental set; it cannot independently establish a full rollout.");
        builder.AppendLine();
        builder.AppendLine("Negative anchors in top-5:");
        builder.AppendLine();
        builder.AppendLine("| pair | BoW | ONNX |");
        builder.AppendLine("|---|---:|---:|");
        foreach (var row in negativeTop5Hits)
        {
            var element = JsonSerializer.SerializeToElement(row);
            builder.AppendLine(
                $"| {element.GetProperty("pair_id").GetString()} | " +
                $"{(element.GetProperty("in_top5_bow").GetBoolean() ? "hit" : "-")} | " +
                $"{(element.GetProperty("in_top5_onnx").GetBoolean() ? "hit" : "-")} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Top-10 stability");
        builder.AppendLine();
        builder.AppendLine($"- Query contexts: **{stabilityQueries.Count}** (max 4 per entity; not 100 distinct entities)");
        builder.AppendLine($"- Candidate pool: Track A / graph (`{candidatePoolA}` contexts, entity-level top-10)");
        builder.AppendLine($"- Scripts: {stabilityQueries.Count(x => x.IsCyrillic)} Cyrillic, " +
            $"{stabilityQueries.Count(x => x.IsLatin)} Latin, " +
            $"{stabilityQueries.Count(x => !x.IsCyrillic && !x.IsLatin)} other");
        builder.AppendLine($"- Average top-10 overlap by `neighbor_entity_id`: **{stabilityOverlap:P1}**");
        builder.AppendLine($"- Identical top-10: **{exactOverlapCount} / {stabilityQueries.Count}**");
        builder.AppendLine($"- Zone: **{stabilityZone}**");
        builder.AppendLine();
        builder.AppendLine("## Decision");
        builder.AppendLine();
        builder.AppendLine($"**{decision}**");
        builder.AppendLine();
        builder.AppendLine("Thresholds are fixed in `docs/PHASE_C.md`: P@5 <0.40 fail / 0.40-0.65 acceptable / >0.65 wins; " +
            "nDCG@10 <0.45 fail / 0.45-0.70 acceptable / >0.70 wins; ONNX must beat BoW by >= +0.10 P@5 and >= +0.05 nDCG@10. " +
            "Track A is supplemental and cannot override the cross-lingual gate.");
        builder.AppendLine();
        builder.AppendLine("The CSV `semantic_neighbors.csv` is the union of the BoW and ONNX top-5 **entities**, " +
            "deduplicated by `neighbor_entity_id` and ordered by the best rank; `score_bow` and `score_onnx` " +
            "refer to the same `(neighbor_entity_id, neighbor_text)` row. " +
            "CSV contains extracted variants and scores only; full source contexts stay in the input table.");
        return builder.ToString();
    }

    private static JsonElement ReadJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        return document.RootElement.Clone();
    }

    private static string FindLabelsDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var candidate = Path.Combine(
                    current.FullName,
                    "test-fixtures",
                    "phase-c-corpus-1000",
                    "labels");
                if (Directory.Exists(candidate))
                    return candidate;
                current = current.Parent;
            }
        }

        throw new InvalidOperationException(
            "Could not find test-fixtures/phase-c-corpus-1000/labels; pass --labels explicitly.");
    }

    private static string Require(Options options, string name) =>
        options.Value(name) ?? throw new ArgumentException($"Missing required option {name}.");

    private static Options ParseOptions(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h")
            {
                options.ShowHelp = true;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unknown argument: {arg}");

            if (i + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {arg}.");

            options.Values[arg] = args[++i];
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "Usage: dotnet run --project tools/PhaseC -- " +
            "--corpus <entities_table.csv> --catalog <codex.json> --model <model-dir> " +
            "[--labels <labels-dir>] [--out <dir>] [--manifest <manifest.json>] " +
            "[--representation name|context] [--stability-queries 100] [--max-per-entity 4] " +
            "[--track-c-out <dir>] [--alias-baseline-out <dir>]");
    }

    private sealed class Options
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public bool ShowHelp { get; set; }

        public string? Value(string name) => Values.TryGetValue(name, out var value) ? value : null;

        public int IntValue(string name, int fallback) =>
            Value(name) is { } value && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
    }
}
