using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

public class SourceQualitySidecarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-source-" + Guid.NewGuid().ToString("N"));

    public SourceQualitySidecarTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string WriteSidecar(string text)
    {
        var path = Path.Combine(_dir, "entities_source_quality.csv");
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void Sidecar_wins_over_detector_and_missing_file_is_unknown()
    {
        var path = WriteSidecar(
            "file,source_quality,source_reason,evidence_level,batch_id\n" +
            "\"a,quoted.txt\",unknown,candidate_tail,not_assessed,unknown-267\n" +
            "clean.txt,usable,detector_negative,detector_only,none\n");

        var sidecar = SourceQualitySidecar.Load(path);
        var detector = new SourceQualityResult(SourceQuality.Usable, SourceQuality.ReasonDetectorNegative);

        var quoted = sidecar.Resolve("a,quoted.txt", detector);
        Assert.Equal(SourceQuality.Unknown, quoted.Quality);
        Assert.Equal(SourceQuality.ReasonCandidateTail, quoted.Reason);

        var missing = sidecar.Resolve("not-listed.txt", detector);
        Assert.Equal(SourceQuality.Unknown, missing.Quality);
        Assert.Equal(SourceQuality.ReasonSidecarMissing, missing.Reason);
    }

    [Fact]
    public void Semicolon_sidecar_and_default_reason_are_supported()
    {
        var path = WriteSidecar("file;source_quality\nbad.txt;degraded_lexical\n");
        var sidecar = SourceQualitySidecar.Load(path);

        var result = sidecar.Resolve("bad.txt", default);
        Assert.Equal(SourceQuality.DegradedLexical, result.Quality);
        Assert.Equal(SourceQuality.ReasonLexicalSubstitution, result.Reason);
    }

    [Fact]
    public void Conflicting_duplicate_entry_is_rejected()
    {
        var path = WriteSidecar(
            "file,source_quality\n" +
            "same.txt,usable\n" +
            "same.txt,unknown\n");

        Assert.Throws<InvalidDataException>(() => SourceQualitySidecar.Load(path));
    }

    [Fact]
    public void Disabled_sidecar_preserves_detector_result()
    {
        var detector = new SourceQualityResult(SourceQuality.Usable, SourceQuality.ReasonDetectorNegative);
        var result = SourceQualitySidecar.Disabled.Resolve("any.txt", detector);
        Assert.Equal(detector, result);
    }
}
