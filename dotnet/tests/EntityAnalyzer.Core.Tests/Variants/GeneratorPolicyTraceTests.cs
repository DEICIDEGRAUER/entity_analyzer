using System.Text.RegularExpressions;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Variants;

// Enforces the frozen v18.2 generator trace as part of the normal test suite.
// This does not rerun the corpus: the full corpus comparison is a separate doc /
// script selfcheck. It catches silent edits to the 198 traceable row-level
// events after they have been accepted under criterion v2.
public class GeneratorPolicyTraceTests
{
    private static readonly string TracePath = FindTracePath();
    private static readonly IReadOnlySet<string> AllowedClassification =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "approved_addition",
            "same_context_re_attribution",
            "nested_span_improvement",
            "standalone_suppression",
        };

    [Fact]
    public void Composite_generator_default_is_full_phrase_plus_exceptions()
    {
        Assert.Equal(CompositeVariantMode.FullPhrasePlusExceptions, CompositeGeneratorPolicy.DefaultMode);
    }

    [Fact]
    public void Codex_adapter_default_mode_follows_the_policy()
    {
        // The activation switch is only real if the pipeline sees it:
        // ToDefinitions used to pin Legacy in its own optional parameter,
        // which made CompositeGeneratorPolicy.DefaultMode a silent no-op.
        var doc = CodexDocument.CreateEmpty();
        doc.Cards.Add(Card.Create("deity", "Наг и Йеб"));

        var def = CodexSearchAdapter.ToDefinitions(doc).Single();
        Assert.Equal(
            CompositeVariantMode.FullPhrasePlusExceptions,
            CompositeGeneratorPolicy.DefaultMode);
        Assert.Equal(CompositeGeneratorPolicy.DefaultMode, def.CompositeMode);

        var catalog = new EntityCatalog([def], generateVariants: true);
        var pool = catalog.VariantsByEntity["Наг и Йеб"];
        Assert.Contains("Нага и Йеба", pool);        // full-phrase declension is live
        Assert.DoesNotContain("Йебам", pool);        // legacy per-component tail is gone
    }

    [Fact]
    public void Frozen_trace_csv_content_is_pinned()
    {
        var bytes = File.ReadAllBytes(TracePath);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        // Existing file uses CRLF, but the pin accepts a normal LF checkout too.
        var actual = Hash(bytes);
        var lf = Hash(System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));
        Assert.True(
            actual == PinnedTraceHashCrlf || actual == PinnedTraceHashLf,
            $"Frozen trace hash {actual} is not the pinned trace Hash (expected CRLF={PinnedTraceHashCrlf} or LF={PinnedTraceHashLf}).");
    }

    [Fact]
    public void Frozen_trace_has_the_expected_header()
    {
        Assert.Equal(
            new[]
            {
                "kind", "entity", "legacyVariant", "plusVariant", "plusEntity",
                "line", "offset", "file", "contextHash", "classification",
            },
            ReadHeader());
    }

    [Fact]
    public void Frozen_trace_classifies_every_row_as_a_traceable_event()
    {
        var rows = ReadRows(skipHeader: true);
        Assert.Equal(198, rows.Count);

        foreach (var row in rows)
        {
            var kindText = row["kind"];
            var entity = row["entity"];
            if (!Enum.TryParse<TraceKind>(kindText, ignoreCase: true, out var kind))
            {
                Assert.Fail($"Unknown trace kind '{kindText}'");
            }
            var classification = row["classification"];
            Assert.True(
                AllowedClassification.Contains(classification),
                $"Unknown classification '{classification}' in {kindText}/{entity}");
            Assert.False(string.IsNullOrWhiteSpace(entity));
            Assert.False(string.IsNullOrWhiteSpace(row["plusVariant"]));
            Assert.False(string.IsNullOrWhiteSpace(row["file"]));
            Assert.Matches(HexHash, row["contextHash"]);
            Assert.True(int.Parse(row["line"]) > 0, $"line must be positive in {kindText}/{entity}");
            Assert.True(long.Parse(row["offset"]) >= 0, $"offset must be non-negative in {kindText}/{entity}");

            if (kind == TraceKind.Added)
            {
                Assert.Equal(row["entity"], row["plusEntity"]);
            }
        }
    }

    [Fact]
    public void Frozen_trace_has_exactly_5_nested_improvements_and_no_standalone_suppression()
    {
        var rows = ReadRows(skipHeader: true);
        var removed = rows.Where(r => r["kind"] == "removed").ToList();
        Assert.Equal(5, removed.Count);
        Assert.All(removed, r => Assert.Equal("nested_span_improvement", r["classification"]));
        Assert.DoesNotContain(rows, r => r["classification"] == "standalone_suppression");
    }

    [Fact]
    public void Frozen_trace_has_exactly_29_reattributions_and_164_approved_additions()
    {
        var rows = ReadRows(skipHeader: true);
        var added = rows.Where(r => r["kind"] == "added").ToList();
        Assert.Equal(193, added.Count);
        Assert.Equal(29, added.Count(r => r["classification"] == "same_context_re_attribution"));
        Assert.Equal(164, added.Count(r => r["classification"] == "approved_addition"));
        Assert.Equal(0, added.Count(r => r["classification"] == "standalone_suppression"));
    }

    private enum TraceKind
    {
        Added,
        Removed,
    }

    private const string PinnedTraceHashCrlf =
        "1B72B940802750B2E0C3861BCEA2366767E721B534BDD2DC6FC2B763FD358816";
    private const string PinnedTraceHashLf =
        "9592952847A0F39B37BED9EDEC9A60731C96A392B304B3DE9C6209C14FD9BAF0";

    private static string Hash(byte[] bytes)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(bytes));
    }

    private static string FindTracePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "dotnet",
                "docs",
                "OUTPUT_GENERATOR_TRACE.csv");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }

        Assert.Fail("Could not locate dotnet/docs/OUTPUT_GENERATOR_TRACE.csv under repository root");
        return string.Empty;
    }

    private static List<string> ReadHeader()
    {
        using var reader = new StreamReader(TracePath);
        return ParseValues(reader.ReadLine()!);
    }

    private static List<Dictionary<string, string>> ReadRows(bool skipHeader = false)
    {
        using var reader = new StreamReader(TracePath);
        var rows = new List<Dictionary<string, string>>();
        var header = ParseValues(reader.ReadLine()!);

        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                var values = ParseValues(line);
                Assert.True(
                    values.Count == header.Count,
                    $"Frozen trace row has {values.Count} columns; expected {header.Count}: {line}");
                rows.Add(header
                    .Select((name, index) => (name, value: values[index]))
                    .ToDictionary(kvp => kvp.name, kvp => kvp.value));
            }
        }

        return rows;
    }

    private static List<string> ParseValues(string line)
    {
        var values = new List<string>();
        var value = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    value.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(ch);
            }
        }

        values.Add(value.ToString());
        return values;
    }

    private static readonly Regex HexHash = new("^[0-9A-F]{64}$", RegexOptions.Compiled);
}
