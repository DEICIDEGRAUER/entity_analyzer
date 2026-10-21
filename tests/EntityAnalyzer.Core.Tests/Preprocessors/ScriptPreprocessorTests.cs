using EntityAnalyzer.Cli;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Text;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Preprocessors;

/// <summary>
/// R1-13 (Roslyn, согласовано 2026-08-26): пользовательские C#-скрипты
/// препроцессоров (.csx). Загрузчик живёт в CLI; ядро остаётся без зависимостей.
/// Конвенция: последним выражением скрипт возвращает экземпляр ITextPreprocessor.
/// </summary>
public class ScriptPreprocessorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea_csx_" + Guid.NewGuid().ToString("N"));

    public ScriptPreprocessorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        // Реестр — глобальное состояние: убрать зарегистрированные имена.
        TextPreprocessorRegistry.Remove("strip-digits");
        TextPreprocessorRegistry.Remove("bad-name");
    }

    private string WriteScript(string code, string name = "s.csx")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, code);
        return path;
    }

    public const string ValidScript = """
        using EntityAnalyzer.Core.Text;
        using System.Linq;

        public class StripDigits : ITextPreprocessor
        {
            public string Name => "strip-digits";
            public string Process(string text, string filePath) =>
                string.Concat(text.Where(c => !char.IsDigit(c)));
        }

        new StripDigits()
        """;

    [Fact]
    public async Task Valid_script_loads_and_processes()
    {
        var r = await ScriptPreprocessorLoader.LoadAsync(WriteScript(ValidScript));
        Assert.True(r.IsOk, r.Error);
        Assert.Equal("strip-digits", r.Preprocessor!.Name);
        Assert.Equal("страница ", r.Preprocessor.Process("страница 34", "f.txt"));
    }

    [Fact]
    public async Task Script_without_instance_expression_fails_clearly()
    {
        var code = """
            using EntityAnalyzer.Core.Text;
            public class Broken : ITextPreprocessor
            {
                public string Name => "broken";
                public string Process(string text, string filePath) => text;
            }
            """;
        var r = await ScriptPreprocessorLoader.LoadAsync(WriteScript(code));
        Assert.False(r.IsOk);
        Assert.Contains("ITextPreprocessor", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Syntax_error_reports_diagnostic()
    {
        var r = await ScriptPreprocessorLoader.LoadAsync(WriteScript("this is not csharp !!!"));
        Assert.False(r.IsOk);
        Assert.Contains("error", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_file_fails()
    {
        var r = await ScriptPreprocessorLoader.LoadAsync(Path.Combine(_dir, "nope.csx"));
        Assert.False(r.IsOk);
        Assert.Contains("не найден", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Builtin_name_conflict_is_rejected()
    {
        var code = ValidScript.Replace("\"strip-digits\"", "\"page-numbers\"");
        var r = await ScriptPreprocessorLoader.LoadAsync(WriteScript(code));
        Assert.False(r.IsOk);
        Assert.Contains("page-numbers", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registered_script_resolves_through_registry()
    {
        // Регистрация под именем скрипта делает его доступным для --preprocess.
        Assert.True(TextPreprocessorRegistry.Register("strip-digits", () => new DelegatePp()));
        var r = TextPreprocessorRegistry.Resolve(["strip-digits"]);
        Assert.True(r.IsOk);
        Assert.Equal("delegate", r.Preprocessors![0].Process("x", "f"));
        Assert.False(TextPreprocessorRegistry.Remove("page-numbers")); // встроенное не удаляется
        Assert.True(TextPreprocessorRegistry.Remove("strip-digits"));
    }

    [Fact]
    public async Task Engine_applies_script_preprocessor_end_to_end()
    {
        var load = await ScriptPreprocessorLoader.LoadAsync(WriteScript(ValidScript));
        Assert.True(load.IsOk, load.Error);

        var corpus = Path.Combine(_dir, "corpus");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, "t1.txt"), "Год Красной Луны наступил в 1928 году.");

        var options = new AnalyzerOptions
        {
            Corpus = { Dir = corpus },
            Output = { Dir = Path.Combine(corpus, "_out") },
            Cache = { Enabled = false },
        };
        options.Processing.Preprocessors = ["strip-digits"];
        var catalog = new EntityCatalog([new EntityDefinition("X", "Xx", [], [])]);
        var engine = new AnalysisEngine(options, catalog, "extended");
        var session = engine.Run(CancellationToken.None);

        // Скрипт применился: цифры года вырезаны из текста до поиска.
        Assert.Equal(0, session.Summary.TotalMentions);
    }

    private sealed class DelegatePp : ITextPreprocessor
    {
        public string Name => "strip-digits";
        public string Process(string text, string filePath) => "delegate";
    }
}
