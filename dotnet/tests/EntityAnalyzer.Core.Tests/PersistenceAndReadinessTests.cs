using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Variants;
using System.IO;
using System.Text.Json;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

// ═══════════════ CatalogDocument (JSON) ═══════════════

public class CatalogDocumentTests
{
    [Fact]
    public void Json_roundtrip_preserves_cyrillic_names_and_lists()
    {
        var doc = CatalogDocument.FromEntities(new[]
        {
            new EntityDefinition("Наг и Йеб", "Nug and Yeb",
                ["нагйеб", "наг-йеб"], ["Великий", "Древний"]),
            new EntityDefinition("Азатот", "Azathoth", [], ["Слепой бог"]),
        });

        var restored = CatalogDocument.FromJson(doc.ToJson());

        Assert.Equal(CatalogDocument.CurrentVersion, restored.Version);
        Assert.Equal(2, restored.Entities.Count);
        Assert.Equal("Наг и Йеб", restored.Entities[0].Name);
        Assert.Equal("Nug and Yeb", restored.Entities[0].Canon);
        Assert.Equal(["нагйеб", "наг-йеб"], restored.Entities[0].Irregular);
        Assert.Equal(["Великий", "Древний"], restored.Entities[0].Titles);
        Assert.Equal("Азатот", restored.Entities[1].Name);
        Assert.Equal([], restored.Entities[1].Irregular);
    }

    [Fact]
    public void Malformed_json_throws()
    {
        Assert.ThrowsAny<JsonException>(() => CatalogDocument.FromJson("{ not json "));
    }

    [Fact]
    public void Empty_document_serializes_to_valid_json()
    {
        var json = CatalogDocument.FromEntities([]).ToJson();
        var restored = CatalogDocument.FromJson(json);
        Assert.Empty(restored.Entities);
    }
}

// ═══════════════ CatalogStore (atomic file persistence) ═══════════════

public class CatalogStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Save_then_load_roundtrip()
    {
        var path = Path.Combine(_dir, "user_catalog.json");
        var doc = CatalogDocument.FromEntities(
            [new EntityDefinition("Тест", "Test", ["тст"], [])]);

        CatalogStore.Save(path, doc);
        var loaded = CatalogStore.Load(path);

        Assert.NotNull(loaded);
        Assert.Single(loaded.Entities);
        Assert.Equal("Тест", loaded.Entities[0].Name);
    }

    [Fact]
    public void Missing_file_returns_null()
    {
        Assert.Null(CatalogStore.Load(Path.Combine(_dir, "nope.json")));
    }

    [Fact]
    public void Corrupt_file_returns_null_instead_of_crashing()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, "{{{ broken");

        Assert.Null(CatalogStore.Load(path));
    }

    [Fact]
    public void Save_creates_missing_directories()
    {
        var path = Path.Combine(_dir, "a", "b", "catalog.json");
        CatalogStore.Save(path, CatalogDocument.FromEntities([]));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_is_atomic_no_partial_file_on_success_path()
    {
        // After a successful save the target must be complete valid JSON,
        // and no *.tmp siblings may remain next to it.
        var path = Path.Combine(_dir, "atomic.json");
        CatalogStore.Save(path, CatalogDocument.FromEntities(
            [new EntityDefinition("X", "Y", [], [])]));

        Assert.NotNull(CatalogStore.Load(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}

// ═══════════════ ReadinessEvaluator ═══════════════

public class ReadinessEvaluatorTests
{
    private const string ExistingDir = @"C:\corpus"; // existence is injected, never probed

    [Fact]
    public void No_corpus_path_blocks_with_message()
    {
        var r = ReadinessEvaluator.Evaluate(corpusDir: "", corpusExists: false, variantCount: 500, isBusy: false);
        Assert.False(r.CanStart);
        Assert.Contains(r.Blockers, b => b.Contains("корпус", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Nonexistent_directory_blocks_with_message()
    {
        var r = ReadinessEvaluator.Evaluate(@"C:\ghost\dir", corpusExists: false, variantCount: 500, isBusy: false);
        Assert.False(r.CanStart);
        Assert.Contains(r.Blockers, b => b.Contains("не найден", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Zero_variants_blocks_with_message()
    {
        var r = ReadinessEvaluator.Evaluate(ExistingDir, corpusExists: true, variantCount: 0, isBusy: false);
        Assert.False(r.CanStart);
        // QA-UX #2: подсказка ведёт на существующую страницу «Кодекс».
        Assert.Contains(r.Blockers, b => b.Contains("«Кодекс»", StringComparison.Ordinal));
    }

    [Fact]
    public void Busy_blocks_even_when_everything_ready()
    {
        var r = ReadinessEvaluator.Evaluate(ExistingDir, corpusExists: true, variantCount: 500, isBusy: true);
        Assert.False(r.CanStart);
        Assert.Contains(r.Blockers, b => b.Contains("выполняется", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void All_conditions_met_means_ready()
    {
        var r = ReadinessEvaluator.Evaluate(ExistingDir, corpusExists: true, variantCount: 439, isBusy: false);
        Assert.True(r.CanStart);
        Assert.Empty(r.Blockers);
    }

    [Fact]
    public void Multiple_problems_listed_in_stable_order()
    {
        var r = ReadinessEvaluator.Evaluate("", corpusExists: false, variantCount: 0, isBusy: true);
        Assert.False(r.CanStart);
        Assert.True(r.Blockers.Count >= 3);
        // corpus-related blocker always comes first (user fixes top-down)
        Assert.Contains("корпус", r.Blockers[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Whitespace_only_corpus_counts_as_missing()
    {
        var r = ReadinessEvaluator.Evaluate("   ", corpusExists: false, variantCount: 500, isBusy: false);
        Assert.False(r.CanStart);
    }
}

// ═══════════════ DefaultCatalog (embedded resource) ═══════════════

public class DefaultCatalogTests
{
    [Fact]
    public void Embedded_resource_loads_with_entities()
    {
        var doc = DefaultCatalog.Load();
        Assert.NotEmpty(doc.Entities);
        Assert.All(doc.Entities, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            Assert.False(string.IsNullOrWhiteSpace(e.Canon));
        });
    }

    [Fact]
    public void Legacy_nug_and_yeb_present_with_rich_variant_pool()
    {
        var doc = DefaultCatalog.Load();
        var nug = doc.Entities.FirstOrDefault(e =>
            e.Canon.Contains("Nug", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(nug);

        var catalog = new EntityCatalog(doc.Entities, generateVariants: true);
        var total = catalog.VariantsByEntity.Values.Sum(v => v.Count);
        Assert.True(total >= 400, $"expected rich pool, got {total}");
    }

    [Fact]
    public void Load_is_cached_and_returns_same_instance()
    {
        Assert.Same(DefaultCatalog.Load(), DefaultCatalog.Load());
    }
}
