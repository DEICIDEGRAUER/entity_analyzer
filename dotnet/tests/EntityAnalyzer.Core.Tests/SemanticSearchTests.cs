using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R1-24 «Смысловой поиск»: свой векторный индекс (косинусная близость,
/// top-k) + подключаемый IEmbedder; офлайн-эмбеддер на bag-of-words —
/// без ONNX/NuGet; семантические модели подключаются тем же контрактом.
/// </summary>
public class SemanticSearchTests
{
    private sealed class BagOfWordsEmbedder : IEmbedder
    {
        public int Dimensions => 6;
        public double[] Embed(string text)
        {
            var v = new double[Dimensions];
            foreach (var w in text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                v[StableBucket(w)] += 1;
            return v;
        }

        // Детерминированная корзина (FNV-1a): string.GetHashCode() в .NET
        // рандомизирован в каждом процессе — тест мигал по составу корзин
        // и «культ алтарь жрец» иногда обгонял точное совпадение.
        private static int StableBucket(string word)
        {
            var h = 2166136261u;
            foreach (var c in word)
            {
                h ^= c;
                h *= 16777619u;
            }
            return (int)(h % 6);
        }
    }

    [Fact]
    public void Empty_index_returns_no_results()
    {
        var index = new VectorIndex(new BagOfWordsEmbedder());
        Assert.Empty(index.Search("запрос", topK: 5));
    }

    [Fact]
    public void Exact_word_match_ranks_first()
    {
        var index = new VectorIndex(new BagOfWordsEmbedder());
        index.Add("doc-sea", "море волны шторм");
        index.Add("doc-cult", "культ алтарь жрец");
        var results = index.Search("шторм волны", topK: 2);
        Assert.Equal("doc-sea", results[0].Id);
    }

    [Fact]
    public void TopK_limits_results()
    {
        var index = new VectorIndex(new BagOfWordsEmbedder());
        for (var i = 0; i < 10; i++) index.Add($"doc{i}", $"документ номер {i}");
        Assert.Equal(3, index.Search("документ", topK: 3).Count);
    }

    [Fact]
    public void Results_are_sorted_by_similarity()
    {
        var index = new VectorIndex(new BagOfWordsEmbedder());
        index.Add("weak", "совсем другой текст");
        index.Add("strong", "море море море");
        var results = index.Search("море", topK: 2);
        Assert.True(results[0].Score >= results[1].Score);
        Assert.Equal("strong", results[0].Id);
    }
}

/// <summary>
/// R1-25 «Классификатор омонимии»: контекстные признаки различают
/// «Наг»-имя / топоним / аббревиатура — эвристика офлайн, без ML.
/// </summary>
public class HomonymClassifierTests
{
    [Fact]
    public void Abbreviation_pattern_detected()
    {
        // Все заглавные + точка → аббревиатура.
        var sense = HomonymClassifier.Classify("НАГ", "Данные НАГ. собраны комиссией.");
        Assert.Equal(HomonymSense.Abbreviation, sense);
    }

    [Fact]
    public void Preposition_in_pattern_suggests_toponym()
    {
        // «в/на/у + слово» — типичный топонимный контекст.
        var sense = HomonymClassifier.Classify("Наг", "Мы прибыли в Наг поздно вечером.");
        Assert.Equal(HomonymSense.Toponym, sense);
    }

    [Fact]
    public void Personal_context_suggests_name()
    {
        var sense = HomonymClassifier.Classify("Наг", "Жрец Наг произнёс проклятие.");
        Assert.Equal(HomonymSense.PersonName, sense);
    }

    [Fact]
    public void Neutral_context_defaults_to_person_name()
    {
        var sense = HomonymClassifier.Classify("Наг", "Упоминание Наг встречается здесь.");
        Assert.Equal(HomonymSense.PersonName, sense);
    }

    [Fact]
    public void Dotted_abbreviation_is_detected()
    {
        var sense = HomonymClassifier.Classify("Н.А.Г.", "Жрец Н.А.Г. произнёс проклятие.");
        Assert.Equal(HomonymSense.Abbreviation, sense);
    }

    [Fact]
    public void Strong_personal_context_overrides_all_caps()
    {
        // CAPS-паттерн — не приговор: «жрец ... произнёс» прямо указывает
        // на персональное употребление.
        var sense = HomonymClassifier.Classify("НАГ", "Жрец НАГ произнёс проклятие.");
        Assert.Equal(HomonymSense.PersonName, sense);
    }

    [Fact]
    public void Inflected_term_is_matched()
    {
        var sense = HomonymClassifier.Classify("Наг", "Жрец Нага произнёс проклятие.");
        Assert.Equal(HomonymSense.PersonName, sense);
    }

    [Fact]
    public void Place_preposition_works_through_place_phrase()
    {
        var sense = HomonymClassifier.Classify(
            "Наг", "Мы прибыли в древний город Наг поздно вечером.");
        Assert.Equal(HomonymSense.Toponym, sense);
    }

    [Fact]
    public void Place_noun_suggests_toponym_without_preposition()
    {
        // Местный контекст уточняет и CAPS-форму, которая без него
        // считалась бы аббревиатурой.
        var sense = HomonymClassifier.Classify("НАГ", "Город НАГ раскинулся на севере.");
        Assert.Equal(HomonymSense.Toponym, sense);
    }

    [Fact]
    public void Distant_personal_word_does_not_override_near_place_context()
    {
        // «Жрец» стоит далеко и не должен перебивать «в Наг».
        var sense = HomonymClassifier.Classify(
            "Наг", "Жрец сказал, что караван прибыл в Наг.");
        Assert.Equal(HomonymSense.Toponym, sense);
    }

    [Fact]
    public void Punctuation_and_speech_verb_around_term_suggest_person()
    {
        var sense = HomonymClassifier.Classify("Наг", "«Наг», — сказал жрец.");
        Assert.Equal(HomonymSense.PersonName, sense);
    }

    [Fact]
    public void Empty_context_keeps_caps_abbreviation()
    {
        Assert.Equal(HomonymSense.Abbreviation, HomonymClassifier.Classify("НАГ", ""));
        Assert.Equal(HomonymSense.PersonName, HomonymClassifier.Classify("Наг", ""));
        Assert.Equal(HomonymSense.PersonName, HomonymClassifier.Classify("", "Наг"));
    }
}
