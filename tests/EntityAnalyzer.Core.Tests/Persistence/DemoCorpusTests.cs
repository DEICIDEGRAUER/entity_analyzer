using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R2-18: Демо-корпус «Попробовать за 30 секунд» ═══════════════
// Первый запуск не должен требовать понимания четырёх понятий до первой
// ценности. Встроенный микрокорпус: 5 текстов, упоминающих сущности
// встроенного кодекса, — полный цикл анализа без выбора папок.

public class DemoCorpusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea_demo_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Writes_exactly_five_nonempty_files()
    {
        var dir = DemoCorpus.WriteTo(_dir);

        Assert.Equal(dir, _dir);
        var files = Directory.GetFiles(dir, "*.txt");
        Assert.Equal(5, files.Length);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length > 200, $"{f} слишком мал для осмысленного анализа"));
    }

    [Fact]
    public void Texts_mention_entities_from_default_codex()
    {
        // Корпус обязан находить то, что знает встроенный кодекс, иначе
        // демо-прогон покажет пустые результаты и смысл онбординга пропадает.
        DemoCorpus.WriteTo(_dir);
        var all = string.Join("\n", Directory.GetFiles(_dir, "*.txt").Select(File.ReadAllText));

        Assert.Contains("Наг", all);
        Assert.Contains("Йеб", all);
        Assert.Contains("Азатот", all);
    }

    [Fact]
    public void Rewriting_existing_demo_is_safe()
    {
        DemoCorpus.WriteTo(_dir);
        File.WriteAllText(Path.Combine(_dir, "a_first.txt"), "мусор");
        DemoCorpus.WriteTo(_dir); // повторный вызов — идемпотентный

        var files = Directory.GetFiles(_dir, "*.txt");
        Assert.Equal(5, files.Length);
        Assert.DoesNotContain(files, f => f.EndsWith("a_first.txt"));
    }
}
