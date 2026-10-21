using System.IO;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>Atomic JSON file persistence for catalog documents.</summary>
public static class CatalogStore
{
    /// <summary>Loads a document; missing or corrupt file → null (never throws).</summary>
    public static CatalogDocument? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return CatalogDocument.FromJson(File.ReadAllText(path));
        }
        catch
        {
            // Corrupt user data must degrade to defaults, not crash the app.
            return null;
        }
    }

    /// <summary>Writes via temp file + move so a crash never leaves partial JSON.</summary>
    public static void Save(string path, CatalogDocument document)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, document.ToJson());
        File.Move(tmp, path, overwrite: true);
    }
}
