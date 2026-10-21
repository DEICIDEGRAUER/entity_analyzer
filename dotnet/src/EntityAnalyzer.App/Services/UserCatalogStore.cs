using EntityAnalyzer.Core.Persistence;
using System.IO;

namespace EntityAnalyzer.App.Services;

/// <summary>
/// Persists the user-edited catalog to %APPDATA%\EntityAnalyzer\user_catalog.json.
/// Absent file means "use the embedded default catalog".
/// </summary>
public static class UserCatalogStore
{
    public static string FilePath => Path.Combine(SettingsService.DirectoryPath, "user_catalog.json");

    /// <summary>User document or null when none was saved yet / file is corrupt.</summary>
    public static CatalogDocument? Load() => CatalogStore.Load(FilePath);

    public static void Save(CatalogDocument document) => CatalogStore.Save(FilePath, document);

    /// <summary>Forget user edits → app falls back to the embedded default.</summary>
    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* non-fatal */ }
    }
}
