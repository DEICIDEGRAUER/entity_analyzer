using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

public class JsonFileCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-cache-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static FileResult Sample(string name = "a.txt") => new(
        name, Path.Combine("corpus", name),
        [new Mention(1, "Наг и Йеб", "Наг и Йеб", 1, "контекст", Theme: "", IsFuzzy: false)],
        TotalFound: 3, ContextSize: 60, CharCount: 1200, DensityPer10k: 25.0);

    [Fact]
    public void Roundtrip_preserves_result()
    {
        var cache = new JsonFileCache(_dir);
        var original = Sample();

        cache.Set("key1", original);
        Assert.True(cache.TryGet("key1", out var loaded));

        Assert.NotNull(loaded);
        Assert.Equal(original.FileName, loaded!.FileName);
        Assert.Equal(original.TotalFound, loaded.TotalFound);
        Assert.Single(loaded.Mentions);
        Assert.Equal("Наг и Йеб", loaded.Mentions[0].Entity);
    }

    [Fact]
    public void Miss_on_unknown_key()
    {
        var cache = new JsonFileCache(_dir);
        Assert.False(cache.TryGet("nope", out _));
    }

    [Fact]
    public void Corrupt_index_is_tolerated()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "index.json"), "{ broken json !!");

        var cache = new JsonFileCache(_dir);
        Assert.False(cache.TryGet("x", out _));
        cache.Set("x", Sample()); // and still usable afterwards
        Assert.True(cache.TryGet("x", out _));
    }

    [Fact]
    public void Config_hash_invalidates_entries()
    {
        var cache = new JsonFileCache(_dir, configHash: "cfg-A");
        cache.Set("f", Sample());
        Assert.True(cache.TryGet("f", out _));

        var fresh = new JsonFileCache(_dir, configHash: "cfg-B");
        Assert.False(fresh.TryGet("f", out _)); // different preset → stale
    }
}
