using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R2-11: «Исследование как файл» (.eaproj) ═══════════════
// Исследование — это корпус + кодекс + паттерны + настройки прогона, а не
// россыпь файлов в %APPDATA%. Пакет — ZIP с манифестом; открыл файл —
// восстановил контекст. Формат: manifest.json + codex.json +
// semantic_patterns.json + settings.json внутри архива.

public class ProjectPackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea_proj_" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public ProjectPackageTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "study" + ProjectPackage.Extension);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ProjectManifest Manifest() => new(
        DateTimeOffset.Parse("2026-08-25T10:00:00Z"),
        EngineVersion: "1.0.0",
        CorpusDir: @"D:\Corpus\Мифы",
        Preset: "titanic",
        Notes: "Сравнение редакций");

    [Fact]
    public void Save_then_load_roundtrip_preserves_all_sections()
    {
        ProjectPackage.Save(_path, Manifest(),
            codexJson: "{\"cards\":[]}",
            semanticPatternsJson: "{\"patterns\":[1]}",
            settingsJson: "{\"preset\":\"titanic\"}");

        var loaded = ProjectPackage.Load(_path);

        Assert.Equal("1.0.0", loaded.Manifest.EngineVersion);
        Assert.Equal(@"D:\Corpus\Мифы", loaded.Manifest.CorpusDir);
        Assert.Equal("titanic", loaded.Manifest.Preset);
        Assert.Equal("Сравнение редакций", loaded.Manifest.Notes);
        Assert.Equal("{\"cards\":[]}", loaded.CodexJson);
        Assert.Equal("{\"patterns\":[1]}", loaded.SemanticPatternsJson);
        Assert.Equal("{\"preset\":\"titanic\"}", loaded.SettingsJson);
    }

    [Fact]
    public void Optional_sections_may_be_absent()
    {
        ProjectPackage.Save(_path, Manifest(), null, null, null);

        var loaded = ProjectPackage.Load(_path);
        Assert.Null(loaded.CodexJson);
        Assert.Null(loaded.SemanticPatternsJson);
        Assert.Null(loaded.SettingsJson);
        // Манифест обязателен всегда.
        Assert.Equal("titanic", loaded.Manifest.Preset);
    }

    [Fact]
    public void Package_is_a_real_zip_with_readable_manifest()
    {
        // Формат прозрачен: исследователь открывает пакет любым архиватором.
        ProjectPackage.Save(_path, Manifest(), "x", null, null);

        using var zip = System.IO.Compression.ZipFile.OpenRead(_path);
        Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
        Assert.Contains(zip.Entries, e => e.FullName == "codex.json");
    }

    [Fact]
    public void Loading_garbage_throws_invalid_data()
    {
        File.WriteAllBytes(_path, [0x50, 0x4B, 0x03, 0x04, 0xDE, 0xAD]); // обрезанный zip
        Assert.ThrowsAny<System.IO.InvalidDataException>(() => ProjectPackage.Load(_path));
    }

    [Fact]
    public void Extension_constant_is_stable()
    {
        Assert.Equal(".eaproj", ProjectPackage.Extension);
    }
}
