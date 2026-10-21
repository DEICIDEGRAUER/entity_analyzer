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
    // Независимая реализация FNV-1a для проверки стабильности корзин.
    private static int StableBucket(string word, int dimensions)
    {
        var hash = 2166136261u;
        unchecked
        {
            foreach (var c in word)
            {
                hash ^= c;
                hash *= 16777619u;
            }
        }

        return (int)(hash % (uint)dimensions);
    }

    private static void AssertVectorEqual(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i]);
    }

    private sealed class MapEmbedder : IEmbedder
    {
        private readonly Dictionary<string, double[]> _vectors;

        public int Dimensions { get; }

        public MapEmbedder(int dimensions, params (string Text, double[] Vector)[] entries)
        {
            Dimensions = dimensions;
            _vectors = entries.ToDictionary(e => e.Text, e => e.Vector, StringComparer.Ordinal);
        }

        public double[] Embed(string text) =>
            _vectors.TryGetValue(text, out var vector) ? [.. vector] : new double[Dimensions];
    }

    private sealed class ScriptedEmbedder : IEmbedder
    {
        private readonly Func<string, double[]> _embed;

        public int Dimensions { get; }

        public ScriptedEmbedder(int dimensions, Func<string, double[]> embed)
        {
            Dimensions = dimensions;
            _embed = embed;
        }

        public double[] Embed(string text) => _embed(text);
    }

    [Fact]
    public void Empty_index_returns_no_results()
    {
        var index = new VectorIndex(new BagOfWordsEmbedder(64));
        Assert.Empty(index.Search("запрос", topK: 5));
    }

    [Fact]
    public void Exact_word_match_ranks_first()
    {
        var index = new VectorIndex(new BagOfWordsEmbedder(64));
        index.Add("doc-sea", "море волны шторм");
        index.Add("doc-cult", "культ алтарь жрец");
        var results = index.Search("шторм волны", topK: 2);
        Assert.Equal("doc-sea", results[0].Id);
    }

    [Fact]
    public void TopK_limits_results()
    {
        var index = new VectorIndex(new BagOfWordsEmbedder(64));
        for (var i = 0; i < 10; i++) index.Add($"doc{i}", $"документ номер {i}");
        Assert.Equal(3, index.Search("документ", topK: 3).Count);
    }

    [Fact]
    public void Results_are_sorted_by_similarity()
    {
        var index = new VectorIndex(new BagOfWordsEmbedder(64));
        index.Add("weak", "совсем другой текст");
        index.Add("strong", "море море море");
        var results = index.Search("море", topK: 2);
        Assert.True(results[0].Score >= results[1].Score);
        Assert.Equal("strong", results[0].Id);
    }

    [Fact]
    public void BagOfWords_embedding_is_process_stable()
    {
        var embedder = new BagOfWordsEmbedder(64);
        var expected = new double[64];
        expected[StableBucket("море", 64)] = 2;
        expected[StableBucket("шторм", 64)] = 1;

        var actual = embedder.Embed("Море, море! Шторм?");
        AssertVectorEqual(expected, actual);
        AssertVectorEqual(actual, embedder.Embed("море море шторм"));
    }

    [Fact]
    public void BagOfWords_empty_text_is_zero_vector()
    {
        var embedder = new BagOfWordsEmbedder(16);
        AssertVectorEqual(new double[16], embedder.Embed(""));
        AssertVectorEqual(new double[16], embedder.Embed(" ,.!?—"));
    }

    [Fact]
    public void BagOfWords_rejects_non_positive_dimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BagOfWordsEmbedder(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BagOfWordsEmbedder(-1));
    }

    [Fact]
    public void Empty_query_and_nonpositive_topk_return_no_results()
    {
        var index = new VectorIndex(new BagOfWordsEmbedder(64));
        index.Add("doc", "море");
        Assert.Empty(index.Search(""));
        Assert.Empty(index.Search(null!));
        Assert.Empty(index.Search("   ", 5));
        Assert.Empty(index.Search(" ,.!?—", 5));
        Assert.Empty(index.Search("море", 0));
        Assert.Empty(index.Search("море", -1));
    }

    [Fact]
    public void Duplicate_id_replaces_existing_document()
    {
        var embedder = new MapEmbedder(2,
            ("one", [1.0, 0.0]),
            ("two", [0.0, 1.0]));
        var index = new VectorIndex(embedder);

        index.Add("same", "one");
        index.Add("same", "two");

        Assert.Equal(1, index.Count);
        var two = Assert.Single(index.Search("two", 1));
        Assert.Equal("same", two.Id);
        Assert.Equal(1.0, two.Score, 12);
        Assert.Equal(0.0, Assert.Single(index.Search("one", 1)).Score, 12);
    }

    [Fact]
    public void Ties_are_broken_by_ordinal_id()
    {
        var embedder = new MapEmbedder(1,
            ("same", [1.0]),
            ("other", [0.0]));
        var index = new VectorIndex(embedder);
        index.Add("b", "same");
        index.Add("a", "same");
        index.Add("c", "other");

        var ids = index.Search("same", 2).Select(r => r.Id).ToArray();
        Assert.Equal(new[] { "a", "b" }, ids);
    }

    [Fact]
    public void Index_rejects_invalid_embedder_dimensions()
    {
        Assert.Throws<ArgumentNullException>(() => new VectorIndex(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VectorIndex(new ScriptedEmbedder(0, _ => [])));
    }

    [Fact]
    public void Index_rejects_invalid_embedding_vectors()
    {
        var wrongLength = new VectorIndex(new ScriptedEmbedder(2, _ => [1.0, 2.0, 3.0]));
        Assert.Throws<InvalidOperationException>(() => wrongLength.Add("id", "text"));

        var notFinite = new VectorIndex(new ScriptedEmbedder(2, _ => [double.NaN, 0.0]));
        Assert.Throws<InvalidOperationException>(() => notFinite.Add("id", "text"));

        var infinite = new VectorIndex(new ScriptedEmbedder(2, _ => [double.PositiveInfinity, 0.0]));
        Assert.Throws<InvalidOperationException>(() => infinite.Add("id", "text"));

        var valid = new VectorIndex(new BagOfWordsEmbedder(8));
        Assert.Throws<ArgumentException>(() => valid.Add(" ", "text"));
        Assert.Throws<ArgumentNullException>(() => valid.Add("id", null!));
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
