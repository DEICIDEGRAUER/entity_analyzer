using System.Text.Json;
using System.Text.Json.Serialization;
using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>Versioned serializable snapshot of the entity catalog.</summary>
public sealed record CatalogDocument(int Version, List<EntityDefinition> Entities)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static CatalogDocument FromEntities(IEnumerable<EntityDefinition> entities) =>
        new(CurrentVersion, entities.ToList());

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static CatalogDocument FromJson(string json) =>
        JsonSerializer.Deserialize<CatalogDocument>(json, JsonOpts)
        ?? throw new JsonException("catalog document is null");
}
