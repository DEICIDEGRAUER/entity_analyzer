using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Filtering;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Variants;
using Xunit;

// ═══════════════ Структурный детектор перечислений ═══════════════

public class ListingPatternFilterTests
{
    private readonly ListingPatternFilter _f = new();

    [Fact]
    public void Azathoth_toc_garbage_is_rejected()
    {
        const string ctx =
            "Великие ужасы и тайны древнего мира Великие Древние и Древние Азатот Флейтист Танцор ) " +
            "• Куачиль Уттаус Тот, кому нет имени Багг-Шаш Ктугха Баохт З'укка-могг Штерот Абхолос Апоколотот...";
        Assert.True(_f.IsEnumeration(ctx));
    }

    [Fact]
    public void Narrative_sentence_is_not_enumeration()
    {
        const string ctx = "Жрец Наг и Йеб шептал в подземелье, и тьма отвечала ему из глубины.";
        Assert.False(_f.IsEnumeration(ctx));
    }

    [Fact]
    public void Bullet_run_of_names_is_rejected()
    {
        Assert.True(_f.IsEnumeration("Азатот • Ктулху • Хастур • Йог-Сотот • Шуб-Ниггурат"));
    }

    [Fact]
    public void Comma_name_list_is_rejected()
    {
        Assert.True(_f.IsEnumeration("Азатот, Ктулху, Хастур, Йог-Сотот, Шуб-Ниггурат, Цатоггуа"));
    }

    [Fact]
    public void Short_narrative_with_two_names_passes()
    {
        Assert.False(_f.IsEnumeration("Азатот и Наг правили бездной."));
    }

    [Fact]
    public void Newline_separated_name_rows_are_rejected()
    {
        var ctx = string.Join("\n", "Азатот", "Ктулху", "Хастур", "Йог-Сотот", "Шуб-Ниггурат");
        Assert.True(_f.IsEnumeration(ctx));
    }
}

// ═══════════════ Плотность сущностей v2 ═══════════════

public class ListingDensityV2Tests
{
    private static ListingDensityFilter Create() =>
        new(new HashSet<string>(["бог", "древний", "культ"], StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Substring_does_not_count_as_other_entity()
    {
        // «Награда» содержит «наг», но это не упоминание Нага.
        var f = Create();
        var names = new[] { "Наг и Йеб", "Йеб" };
        Assert.False(f.IsListing(
            "За отвагу воину дали награда и славу вечную.", "Азатот", names));
    }

    [Fact]
    public void Strong_structure_beats_signal_word_rescue()
    {
        // Перечисление имён со словом «древний» внутри — всё равно список.
        var f = Create();
        var names = new[] { "Ктулху", "Хастур" };
        var ctx = "древний Азатот, Ктулху, Хастур, Йог-Сотот, Шуб-Ниггурат, Цатоггуа, Гхатанзоа";
        Assert.True(f.IsListing(ctx, "Азатот", names));
    }

    [Fact]
    public void Crowded_context_still_rejected()
    {
        var f = Create();
        var names = new[] { "Ктулху", "Хастур", "Йог-Сотот", "Шуб-Ниггурат" };
        Assert.True(f.IsListing("Алтарь Ктулху Хастур Йог-Сотот Шуб-Ниггурат был разрушен.", "Азатот", names));
    }
}

// ═══════════════ Чёрный список фраз ═══════════════

public class BlacklistPhraseFilterTests
{
    private readonly BlacklistPhraseFilter _f = new(new[]
    {
        "великие ужасы и тайны древнего мира",
        "оглавление",
        "table of contents",
    });

    [Fact]
    public void Known_phrase_at_head_rejected()
    {
        Assert.True(_f.IsBlacklisted(
            "Великие ужасы и тайны древнего мира Великие Древние Азатот Флейтист Ктулху"));
    }

    [Fact]
    public void Phrase_deep_in_long_text_ignored()
    {
        var tail = new string('х', 300) + " великие ужасы и тайны древнего мира";
        Assert.False(_f.IsBlacklisted(tail));
    }

    [Fact]
    public void Normal_text_passes()
    {
        Assert.False(_f.IsBlacklisted("Азатот спал в центре вселенной, окружённый свитой."));
    }
}

// ═══════════════ Осмысленность контекста ═══════════════

public class MeaningfulnessFilterTests
{
    private readonly MeaningfulnessFilter _f = new();

    [Fact]
    public void Pure_name_list_rejected()
    {
        Assert.True(_f.IsMeaningless("Азатот Ктулху Хастур Йог-Сотот Шуб-Ниггурат Цатоггуа Гхатанзоа"));
    }

    [Fact]
    public void Normal_russian_sentence_passes()
    {
        Assert.False(_f.IsMeaningless("Жрец сказал, что Азатот спит в центре всех миров, и его песня не должна смолкнуть."));
    }

    [Fact]
    public void Short_contexts_are_never_rejected_by_this_filter()
    {
        Assert.False(_f.IsMeaningless("Азатот Ктулху Хастур"));
    }
}

// ═══════════════ Межфайловая дедупликация ═══════════════

public class CrossFileDeduplicationTests
{
    private static Mention M(string ctx, int num = 1) =>
        new(num, "Азатот", "Азатот", 1, ctx);

    [Fact]
    public void Identical_contexts_across_files_grouped_into_first_with_repeats()
    {
        var files = new List<FileResult>
        {
            new("a.txt", "a", [M("Одинаковый контекст про Азатота")], 1, 10, 100, 0),
            new("b.txt", "b", [M("Одинаковый   контекст про Азатота")], 1, 10, 100, 0),
            new("c.txt", "c", [M("одинаковый контекст про азатота")], 1, 10, 100, 0),
        };

        var (result, grouped) = ContextDeduplicator.Apply(files);

        Assert.Equal(2, grouped);
        Assert.Single(result[0].Mentions);
        Assert.Equal(3, result[0].Mentions[0].Repeats);
        Assert.Empty(result[1].Mentions);
        Assert.Empty(result[2].Mentions);
    }

    [Fact]
    public void Different_contexts_survive()
    {
        var files = new List<FileResult>
        {
            new("a.txt", "a", [M("Первый контекст"), M("Второй контекст", 2)], 2, 10, 100, 0),
            new("b.txt", "b", [M("Совершенно иной текст о сущности")], 1, 10, 100, 0),
        };

        var (result, grouped) = ContextDeduplicator.Apply(files);

        Assert.Equal(0, grouped);
        Assert.Equal(2, result[0].Mentions.Count);
        Assert.Single(result[1].Mentions);
    }

    [Fact]
    public void Different_entities_in_identical_context_survive()
    {
        const string context = "Толстой и Пушкин беседовали в библиотеке";
        var files = new List<FileResult>
        {
            new("a.txt", "a",
            [
                M(context),
                new Mention(2, "Пушкин", "Пушкин", 1, context),
            ], 2, 10, 100, 0),
        };

        var (result, grouped) = ContextDeduplicator.Apply(files);

        Assert.Equal(0, grouped);
        Assert.Equal(2, result[0].Mentions.Count);
        Assert.Equal(
            new[] { "Азатот", "Пушкин" },
            result[0].Mentions.Select(m => m.Entity));
    }

    [Fact]
    public void Mention_repeats_defaults_to_one()
    {
        Assert.Equal(1, new Mention(1, "E", "V", 1, "ctx").Repeats);
    }
}

// ═══════════════ Включение/отключение сущностей ═══════════════

public class EntityToggleTests
{
    [Fact]
    public void Enabled_defaults_to_true_and_roundtrips_through_json()
    {
        var doc = CatalogDocument.FromEntities(
            [new EntityDefinition("Наг и Йеб", "Nug and Yeb", [], [])]);
        var restored = CatalogDocument.FromJson(doc.ToJson());
        Assert.True(restored.Entities[0].Enabled);
    }

    [Fact]
    public void Disabled_flag_survives_json_roundtrip()
    {
        var doc = CatalogDocument.FromEntities(
        [
            new EntityDefinition("A", "Aa", [], [], Enabled: false),
            new EntityDefinition("B", "Bb", [], [], Enabled: true),
        ]);
        var restored = CatalogDocument.FromJson(doc.ToJson());
        Assert.False(restored.Entities[0].Enabled);
        Assert.True(restored.Entities[1].Enabled);
    }

    [Fact]
    public void Legacy_json_without_enabled_field_loads_as_enabled()
    {
        const string legacy = """{"version":1,"entities":[{"name":"X","canon":"Xx","irregular":[],"titles":[]}]}""";
        Assert.True(CatalogDocument.FromJson(legacy).Entities[0].Enabled);
    }

    [Fact]
    public void Disabled_entity_excluded_from_search_catalog()
    {
        var catalog = new EntityCatalog(
        [
            new EntityDefinition("Включённая", "Included", ["включённая"], []),
            new EntityDefinition("Выключенная", "Disabled", ["выключенная"], [], Enabled: false),
        ]);

        Assert.Contains("Включённая", catalog.AllNames);
        Assert.DoesNotContain("Выключенная", catalog.AllNames);
    }
}

// ═══════════════ Переключатели фильтров ═══════════════

public class FilterToggleOptionsTests
{
    [Fact]
    public void All_context_filters_default_enabled()
    {
        var p = new ProcessingOptions();
        Assert.True(p.BoundaryFiltersEnabled);
        Assert.True(p.ListingDensityEnabled);
        Assert.True(p.BlacklistPhrasesEnabled);
        Assert.True(p.MeaningfulnessEnabled);
        Assert.True(p.CommonWordRuleEnabled);
        Assert.True(p.TitleValidationEnabled);
        Assert.True(p.CrossFileDeduplication);
    }

    [Fact]
    public void Default_blacklist_contains_toc_markers()
    {
        var p = new ProcessingOptions();
        Assert.Contains(p.ListingBlacklistPhrases, x => x.Contains("великие ужасы", StringComparison.OrdinalIgnoreCase));
    }
}
