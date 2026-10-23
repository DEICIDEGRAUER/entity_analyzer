using Xunit;

namespace EntityAnalyzer.Embedders.Onnx.Tests;

internal static class OnnxTestPaths
{
    public const string ModelDirectoryEnvironmentVariable = "EA_ONNX_MODEL_DIR";

    public static string? ModelDirectory => ResolveModelDirectory();

    public static string? ModelPath => ModelDirectory is { } directory
        ? Path.Combine(directory, "onnx", "model_int8.onnx")
        : null;

    public static string? TokenizerPath => ModelDirectory is { } directory
        ? Path.Combine(directory, "sentencepiece.bpe.model")
        : null;

    public static string SkipReason =>
        "ONNX model is not available. Run `scripts/fetch-model.ps1 -Model e5-small` " +
        "(Windows) or `scripts/fetch-model.sh -Model e5-small` (Linux/macOS), " +
        "or set EA_ONNX_MODEL_DIR to an already verified profile.";

    private static string? ResolveModelDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(ModelDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var fullPath = Path.GetFullPath(configured);
            return File.Exists(Path.Combine(fullPath, "onnx", "model_int8.onnx"))
                ? fullPath
                : null;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "models", "e5-small-int8");
            if (File.Exists(Path.Combine(candidate, "onnx", "model_int8.onnx")))
                return candidate;

            current = current.Parent;
        }

        return null;
    }
}

internal sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        if (OnnxTestPaths.ModelPath is null)
            Skip = OnnxTestPaths.SkipReason;
    }
}

internal sealed class TokenizerFactAttribute : FactAttribute
{
    public TokenizerFactAttribute()
    {
        if (OnnxTestPaths.TokenizerPath is null)
            Skip = OnnxTestPaths.SkipReason;
    }
}
