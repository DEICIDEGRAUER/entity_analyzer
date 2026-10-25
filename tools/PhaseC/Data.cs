using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhaseC;

internal sealed record CodexEntry(string Id, string Name, string Canonical);

internal sealed record EntityMention(
    string CardId,
    string Entity,
    string Canonical,
    string File,
    string Variant,
    string Context,
    string ContextSha256)
{
    private static readonly Regex Latin = new("[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex Cyrillic = new("[А-Яа-яЁё]", RegexOptions.Compiled);

    public bool IsLatin => Latin.IsMatch(Variant);
    public bool IsCyrillic => Cyrillic.IsMatch(Variant);

    public string CandidateId => "ctx_" + CardId + "_" + ContextSha256[..16];
}

internal sealed record LabelAnchor(
    string PairId,
    string QueryId,
    string Kind,
    int Label,
    string QueryCardId,
    string QueryEntity,
    string QueryCanonical,
    string QueryFile,
    string QueryVariant,
    string QueryContextSha256,
    string NeighborCardId,
    string NeighborEntity,
    string NeighborCanonical,
    string NeighborFile,
    string NeighborVariant,
    string NeighborContextSha256,
    string Reason);

internal static class DataLoader
{
    public static IReadOnlyDictionary<string, CodexEntry> LoadCodex(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var cards = document.RootElement.GetProperty("cards");
        var result = new Dictionary<string, CodexEntry>(StringComparer.Ordinal);
        foreach (var card in cards.EnumerateArray())
        {
            var name = card.GetProperty("name").GetString()
                ?? throw new InvalidDataException("Codex card has no name.");
            var id = card.GetProperty("id").GetString()
                ?? throw new InvalidDataException($"Codex card {name} has no id.");
            var canonical = card.TryGetProperty("canonical", out var canonicalElement)
                && canonicalElement.ValueKind == JsonValueKind.String
                ? canonicalElement.GetString() ?? string.Empty
                : string.Empty;
            result[name] = new CodexEntry(id, name, canonical);
        }

        return result;
    }

    public static IReadOnlyList<EntityMention> LoadMentions(
        string path,
        IReadOnlyDictionary<string, CodexEntry> codex)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("entities_table.csv is empty.");
        var header = CsvUtil.ParseLine(headerLine).Select(x => x.Trim()).ToArray();
        var indexes = header
            .Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);

        foreach (var required in new[] { "file", "entity", "variant", "context" })
        {
            if (!indexes.ContainsKey(required))
                throw new InvalidDataException($"entities_table.csv is missing column '{required}'.");
        }

        var mentions = new List<EntityMention>();
        var lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = CsvUtil.ParseLine(line);
            if (fields.Count < header.Length)
            {
                throw new InvalidDataException(
                    $"entities_table.csv line {lineNumber} has {fields.Count} fields, expected {header.Length}.");
            }

            var entity = fields[indexes["entity"]];
            if (!codex.TryGetValue(entity, out var card))
                continue;

            var context = fields[indexes["context"]];
            var variant = fields[indexes["variant"]];
            var file = fields[indexes["file"]];
            mentions.Add(new EntityMention(
                card.Id,
                entity,
                card.Canonical,
                file,
                variant,
                context,
                CsvUtil.Sha256Text(context)));
        }

        return mentions;
    }

    public static IReadOnlyList<LabelAnchor> LoadLabels(string path)
    {
        var result = new List<LabelAnchor>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            result.Add(new LabelAnchor(
                RequiredString(root, "pair_id"),
                RequiredString(root, "query_id"),
                RequiredString(root, "kind"),
                root.GetProperty("label").GetInt32(),
                RequiredString(root, "query_card_id"),
                RequiredString(root, "query_entity"),
                OptionalString(root, "query_canonical"),
                RequiredString(root, "query_file"),
                RequiredString(root, "query_variant"),
                RequiredString(root, "query_context_sha256"),
                RequiredString(root, "neighbor_card_id"),
                RequiredString(root, "neighbor_entity"),
                OptionalString(root, "neighbor_canonical"),
                RequiredString(root, "neighbor_file"),
                RequiredString(root, "neighbor_variant"),
                RequiredString(root, "neighbor_context_sha256"),
                RequiredString(root, "reason")));
        }

        return result;
    }

    public static EntityMention FindMention(
        IReadOnlyList<EntityMention> mentions,
        string cardId,
        string file,
        string contextSha256)
    {
        var hits = mentions
            .Where(m => m.CardId == cardId
                && m.File == file
                && string.Equals(m.ContextSha256, contextSha256, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (hits.Length != 1)
        {
            var sameCardAndFile = mentions
                .Where(m => m.CardId == cardId && m.File == file)
                .Select(m => m.ContextSha256)
                .Distinct(StringComparer.Ordinal)
                .Take(10)
                .ToArray();
            var cardCount = mentions.Count(m => m.CardId == cardId);
            throw new InvalidDataException(
                $"Expected exactly one mention for card={cardId}, file={file}, context={contextSha256}; " +
                $"found {hits.Length}; sameCardFileHashes=[{string.Join(", ", sameCardAndFile)}]; " +
                $"cardMentionCount={cardCount}.");
        }

        return hits[0];
    }

    public static IReadOnlyList<EntityMention> BuildCandidatePool(
        IReadOnlyList<EntityMention> mentions,
        Func<EntityMention, bool> predicate,
        IReadOnlySet<string> cardIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<EntityMention>();
        foreach (var mention in mentions
            .Where(m => cardIds.Contains(m.CardId) && predicate(m))
            .OrderBy(m => m.CardId, StringComparer.Ordinal)
            .ThenBy(m => m.ContextSha256, StringComparer.Ordinal)
            .ThenBy(m => m.File, StringComparer.Ordinal)
            .ThenBy(m => m.Variant, StringComparer.Ordinal))
        {
            var key = mention.CardId + "\0" + mention.Context;
            if (seen.Add(key))
                result.Add(mention);
        }

        return result;
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
        && property.GetString() is { } value
            ? value
            : throw new InvalidDataException($"Label is missing string property '{name}'.");

    private static string OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
