using EntityAnalyzer.Core.Exporting;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

/// <summary>
/// Экспорт пользовательских данных в Markdown (item 22/26/30): цитатник
/// группируется по сущностям, многострочный контекст цитируется построчно,
/// дневник печатает привязки и метки времени.
/// </summary>
public class UserDataExportersTests
{
    private static QuoteRecord Quote(string entity, string context, int line = 7, string file = "a.txt") =>
        new(DateTimeOffset.Parse("2026-10-19T12:00:00+03:00"), $"key|{line}|{entity}", true,
            "hash", line, "вар.", entity, file, @"C:\corpus\" + file, line, context);

    [Fact]
    public void Quote_book_groups_by_entity_and_attributes_source()
    {
        var md = QuoteBookMarkdown.Build(
            [Quote("Йеб", "Йеб шептал в подземелье."), Quote("Наг и Йеб", "Культ Нага процветал.", 3)],
            DateTimeOffset.Parse("2026-10-19T12:30:00+03:00"));

        Assert.Contains("# Цитатник", md);
        Assert.Contains("> Цитат: 2", md);
        Assert.Contains("## Йеб", md);
        Assert.Contains("## Наг и Йеб", md);
        Assert.Contains("> Йеб шептал в подземелье.", md);
        Assert.Contains("(вар.), стр. 7, a.txt", md);
        Assert.Contains("отмечено 2026-10-19", md);
    }

    [Fact]
    public void Quote_book_prefixes_every_line_of_multiline_context()
    {
        var md = QuoteBookMarkdown.Build(
            [Quote("Йеб", "Первая строка.\nВторая строка.")],
            DateTimeOffset.Now);

        Assert.Contains("> Первая строка.", md);
        Assert.Contains("> Вторая строка.", md);
    }

    [Fact]
    public void Diary_prints_title_binding_body_and_timestamps()
    {
        var note = new StoredNote(1,
            DateTimeOffset.Parse("2026-10-18T21:05:00+03:00"),
            DateTimeOffset.Parse("2026-10-19T09:40:00+03:00"),
            "О культе Нага", "Проверить связи с Йебом.",
            NoteBindingKind.Pattern, "cult-tokens", "Культ и ритуалы");

        var md = DiaryMarkdown.Build([note], DateTimeOffset.Parse("2026-10-19T12:30:00+03:00"));

        Assert.Contains("# Дневник исследователя", md);
        Assert.Contains("> Заметок: 1", md);
        Assert.Contains("## О культе Нага", md);
        Assert.Contains("*Создано: 2026-10-18 21:05 · обновлено: 2026-10-19 09:40*", md);
        Assert.Contains("*Привязка: паттерн «Культ и ритуалы»*", md);
        Assert.Contains("Проверить связи с Йебом.", md);
    }

    [Fact]
    public void Diary_falls_back_for_empty_title_and_no_binding()
    {
        var note = new StoredNote(2,
            DateTimeOffset.Parse("2026-10-19T08:00:00+03:00"),
            DateTimeOffset.Parse("2026-10-19T08:00:00+03:00"),
            "   ", "Мысль без заголовка.", NoteBindingKind.None, "", "");

        var md = DiaryMarkdown.Build([note], DateTimeOffset.Now);

        Assert.Contains("## Заметка от 2026-10-19 08:00", md);
        Assert.DoesNotContain("Привязка:", md);
        Assert.Contains("Мысль без заголовка.", md);
    }
}
