using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-24 «Авточерновики статей кодекса»: из данных прогона собирается
/// черновик статьи карточки (встречаемость + характерные контексты);
/// человек правит и утверждает. Офлайн-first: без внешних сервисов.
/// </summary>
[Collection("LogSequential")]
public class CodexDraftGeneratorTests
{
    private static AnalysisSession MakeSession()
    {
        var f1 = new FileResult("a.txt", "a.txt",
            [
                new Mention(1, "Ктулху", "Ктулху", 3, "Спит Ктулху в Р'льехе."),
                new Mention(2, "Ктулху", "Ктулху", 9, "Культ Ктулху ждёт звёзд."),
            ], 2, 40, 5000, 4.0);
        var f2 = new FileResult("b.txt", "b.txt",
            [new Mention(1, "Ктулху", "Ктулху", 2, "Зов Ктулху слышен во сне.")],
            1, 40, 3000, 3.3);
        return new AnalysisSession([f1, f2],
            new AnalysisSummary(3, 2, 1, 8000, new Dictionary<string, int> { ["Ктулху"] = 3 }),
            new QualityReport(5, 0, 0, 0, 0, 0, 3, 0, 0, 0, [], "0s"),
            "light", DateTimeOffset.Now, TimeSpan.Zero);
    }

    [Fact]
    public void Draft_contains_header_stats_and_contexts()
    {
        var draft = CodexDraftGenerator.Build(MakeSession(), "Ктулху");
        Assert.Contains("## Ктулху", draft);
        Assert.Contains("черновик", draft.ToLowerInvariant());
        Assert.Contains("3 упоминания", draft); // сводка
        Assert.Contains("a.txt", draft);        // встречаемость
        Assert.Contains("b.txt", draft);
        Assert.Contains("Спит Ктулху в Р'льехе.", draft); // контексты
        Assert.Contains("требует редактуры", draft);      // статус
    }

    [Fact]
    public void Contexts_are_limited_and_numbered()
    {
        var session = MakeSession();
        var draft = CodexDraftGenerator.Build(session, "Ктулху", maxContexts: 2);
        // Три контекста в сессии, лимит 2 → ровно два нумерованных пункта.
        Assert.Contains("1.", draft);
        Assert.Contains("2.", draft);
        Assert.DoesNotContain("3.", draft);
    }

    [Fact]
    public void Unknown_entity_gives_empty_draft_marker()
    {
        var draft = CodexDraftGenerator.Build(MakeSession(), "Гипотетическая сущность");
        Assert.Contains("Упоминаний не найдено", draft);
    }
}
