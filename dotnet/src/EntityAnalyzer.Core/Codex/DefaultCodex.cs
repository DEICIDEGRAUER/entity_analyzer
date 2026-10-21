using System.Reflection;

namespace EntityAnalyzer.Core.Codex;

/// <summary>
/// Built-in codex shipped inside the assembly (embedded JSON resource):
/// standard categories, relation vocabulary and the migrated default entity.
/// </summary>
public static class DefaultCodex
{
    private const string ResourceName = "EntityAnalyzer.Core.Resources.default_codex.json";
    private static CodexDocument? _cached;
    private static readonly object _gate = new();

    public static CodexDocument Load()
    {
        if (_cached is not null) return _cached;
        lock (_gate)
        {
            if (_cached is not null) return _cached;

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded codex resource '{ResourceName}' is missing.");

            using var reader = new StreamReader(stream);
            _cached = CodexDocument.FromJson(reader.ReadToEnd());
            return _cached;
        }
    }
}
