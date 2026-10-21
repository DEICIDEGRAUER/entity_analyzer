using System.IO;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.App.Services;

/// <summary>
/// Persists the user codex to %APPDATA%\EntityAnalyzer\codex.json.
/// On first launch migrates the legacy user_catalog.json (v1) loss-free;
/// absent/corrupt file means "use the embedded default codex".
/// </summary>
public static class UserCodexStore
{
    public static string FilePath => Path.Combine(SettingsService.DirectoryPath, "codex.json");
    private static string LegacyPath => Path.Combine(SettingsService.DirectoryPath, "user_catalog.json");

    /// <summary>Load user codex; migrates v1 catalog when only it exists.</summary>
    public static (CodexDocument Document, string Source) Load()
    {
        var own = SafeLoad(FilePath);
        if (own is not null) return (own, "пользовательский");

        var legacy = CatalogStore.Load(LegacyPath);
        if (legacy is not null)
        {
            var migrated = CodexDocument.MigrateFromV1(legacy);
            Save(migrated); // persist migration immediately
            return (migrated, "мигрирован из каталога v1");
        }

        return (DefaultCodex.Load(), "встроенный");
    }

    private static CodexDocument? SafeLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            Core.Logging.Audit.Read("codex", path); // R2-23
            return CodexDocument.FromJson(File.ReadAllText(path));
        }
        catch
        {
            return null; // corrupt user data degrades to defaults
        }
    }

    public static void Save(CodexDocument document)
    {
        var dir = SettingsService.DirectoryPath;
        Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, document.ToJson());
        File.Move(tmp, FilePath, overwrite: true);
        Core.Logging.Audit.Write("codex", FilePath); // R2-23
    }

    /// <summary>Forget user edits → app falls back to the embedded default.</summary>
    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* non-fatal */ }
    }
}
