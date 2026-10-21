using System.Reflection;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// Built-in catalog shipped inside the assembly (embedded JSON resource).
/// The application works out of the box — no external config file needed.
/// </summary>
public static class DefaultCatalog
{
    private const string ResourceName = "EntityAnalyzer.Core.Resources.default_catalog.json";
    private static CatalogDocument? _cached;
    private static readonly object _gate = new();

    /// <summary>Embedded document; parsed once, cached for the process lifetime.</summary>
    public static CatalogDocument Load()
    {
        if (_cached is not null) return _cached;
        lock (_gate)
        {
            if (_cached is not null) return _cached;

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded catalog resource '{ResourceName}' is missing. " +
                    "Regenerate it with tools/ConfigConverter.");

            using var reader = new StreamReader(stream);
            _cached = CatalogDocument.FromJson(reader.ReadToEnd());
            return _cached;
        }
    }
}
