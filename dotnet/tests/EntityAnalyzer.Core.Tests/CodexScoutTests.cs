using EntityAnalyzer.Core.Discovery;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

// ═══════════════ R1-1: «Кодекс-скаут» ═══════════════
// Корневое ограничение продукта: «ищу только тех, кого уже знаю». Скаут
// расширяет знание пользователя о корпусе: частотные капитализированные
// последовательности, НЕ покрытые кодексом, → ранжированные кандидаты
// в карточки. MVP — отчёт scout.txt с топ-N.

public class CodexScoutTests
{
    private static Dictionary<string, string> Docs(params (string Name, string Text)[] items) =>
        items.ToDictionary(x => x.Name, x => x.Text);

    [Fact]
    public void Frequent_unknown_name_is_discovered()
    {
        var docs = Docs(
            ("a.txt", "Жрец Голхоротепа открыл врата. Голхоротеп шептал до рассвета."),
            ("b.txt", "Тогда снова Голхоротеп звал из глубины."));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "наг", "йеб" };

        var found = CodexScout.Discover(docs, known, minFrequency: 2);

        var cand = Assert.Single(found, c => c.Phrase == "Голхоротеп");
        Assert.Equal(2, cand.Frequency);
        Assert.Equal(2, cand.FileCount);
    }

    [Fact]
    public void Known_entities_are_not_candidates()
    {
        var docs = Docs(("a.txt", "Наг и Йеб правили. Наг судил. Йеб спал."));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "наг", "йеб", "наг и йеб" };

        var found = CodexScout.Discover(docs, known, minFrequency: 1);

        Assert.DoesNotContain(found, c => c.Phrase.Contains("Наг", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, c => c.Phrase.Contains("Йеб", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sentence_initial_only_words_are_ignored()
    {
        // «Жрецы» капитализируется только в начале предложения — это не имя.
        var docs = Docs(
            ("a.txt", "Жрецы пришли. Жрецы ушли. Жрецы вернулись ночью."));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var found = CodexScout.Discover(docs, known, minFrequency: 2);

        Assert.DoesNotContain(found, c => c.Phrase == "Жрецы");
    }

    [Fact]
    public void Mid_sentence_capitalized_word_is_candidate()
    {
        // Одна и та же форма дважды и хотя бы раз в середине предложения.
        var docs = Docs(("a.txt", "Культ Зот-Оммега процветал в тени. В летописях Зот-Оммега названа проклятой."));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var found = CodexScout.Discover(docs, known, minFrequency: 2);

        Assert.Contains(found, c => c.Phrase == "Зот-Оммега");
    }

    [Fact]
    public void Multiword_titles_are_captured_as_sequence()
    {
        var docs = Docs(
            ("a.txt", "Ритуал Воющей Собаки исполняли в полночь. Образ Воющей Собаки высечен на вратах."),
            ("b.txt", "Снова упомянут культ Воющей Собаки."));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var found = CodexScout.Discover(docs, known, minFrequency: 3);

        Assert.Contains(found, c => c.Phrase == "Воющей Собаки");
    }

    [Fact]
    public void Top_limit_and_sorting_by_frequency()
    {
        var text = string.Join(" ", Enumerable.Repeat("Алтарь Ксиамба стоял.", 10)) + " " +
                   string.Join(" ", Enumerable.Repeat("Храм Уллатэра открыт.", 5));
        var docs = Docs(("a.txt", text));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var found = CodexScout.Discover(docs, known, top: 1, minFrequency: 2);

        // Биграм и униграмма равны по частоте (10); тай-брейк алфавитом выводит
        // «Алтарь Ксиамба» — легитимный кандидат-название артефакта.
        Assert.Equal("Алтарь Ксиамба", Assert.Single(found).Phrase);
    }

    [Fact]
    public void Examples_reference_files()
    {
        var docs = Docs(
            ("летопись.txt", "Имя Таррбис звучало в совете."),
            ("хроника.txt", "Таррбис подписал договор."));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var found = CodexScout.Discover(docs, known, minFrequency: 2);

        var cand = Assert.Single(found, c => c.Phrase == "Таррбис");
        Assert.Contains("летопись.txt", cand.Examples);
        Assert.Contains("хроника.txt", cand.Examples);
    }

    [Fact]
    public void Report_is_written_sorted_and_limited()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ea_scout_" + Guid.NewGuid().ToString("N"));
        try
        {
            var text = string.Join(" ", Enumerable.Repeat("Башня Иштаргада высока.", 6)) + " " +
                       string.Join(" ", Enumerable.Repeat("Мост Керел-Дуна стар.", 3));
            var candidates = CodexScout.Discover(Docs(("a.txt", text)), new HashSet<string>(), top: 10, minFrequency: 2);
            var path = CodexScout.WriteReport(candidates, dir);

            var lines = File.ReadAllLines(path);
            Assert.True(lines[0].Contains("СКАУТ"), "заголовок отчёта должен объяснять назначение");
            Assert.True(lines.Length >= 3, "заголовок + минимум две строки кандидатов");
            var freqLine = lines.First(l => l.Contains("Иштаргада"));
            var rareLine = lines.First(l => l.Contains("Керел-Дуна"));
            Assert.True(Array.IndexOf(lines, freqLine) < Array.IndexOf(lines, rareLine),
                "более частотный кандидат выше в отчёте");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
