namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// R1-24 «Смысловой поиск»: контракт эмбеддера — текст → вектор.
/// <see cref="Dimensions"/> должен быть положительным, а <see cref="Embed"/>
/// — возвращать массив ровно такой длины с конечными значениями.
/// Реализация должна быть детерминированной: одинаковый текст даёт
/// одинаковый вектор независимо от процесса и текущей культуры. ONNX-модели
/// подключаются этим же контрактом отдельным opt-in проектом.
/// </summary>
public interface IEmbedder
{
    int Dimensions { get; }
    double[] Embed(string text);
}

/// <summary>Результат поиска: документ и его близость к запросу.</summary>
public sealed record VectorSearchResult(string Id, double Score);

/// <summary>
/// Векторный индекс «свой» (реестр, п. R1-24): косинусная близость,
/// top-k поиск. Хранит нормализованные векторы документов в памяти.
/// Повторный <see cref="Add"/> с тем же id заменяет документ (upsert).
/// Tie-break детерминированный: сначала score, затем id по ordinal.
/// Экземпляр не потокобезопасен; персистенция — через сериализацию
/// вызывающим слоем.
/// </summary>
public sealed class VectorIndex
{
    private readonly IEmbedder _embedder;
    private readonly Dictionary<string, double[]> _documents = new(StringComparer.Ordinal);

    public VectorIndex(IEmbedder embedder)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        if (embedder.Dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(embedder),
                "Эмбеддер должен возвращать вектор положительной размерности.");
        _embedder = embedder;
    }

    public int Count => _documents.Count;

    /// <summary>Добавляет документ или заменяет вектор с тем же id.</summary>
    public void Add(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id документа не может быть пустым.", nameof(id));

        _documents[id] = EmbedNormalized(text);
    }

    /// <summary>
    /// Top-k ближайших документов к запросу. Пустой запрос и topK ≤ 0
    /// возвращают пустой результат, а не произвольный список нулевых score.
    /// </summary>
    public IReadOnlyList<VectorSearchResult> Search(string query, int topK = 10)
    {
        if (_documents.Count == 0 || topK <= 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var q = EmbedNormalized(query);
        if (!q.Any(value => value != 0)) return [];

        return [.. _documents
            .Select(d => new VectorSearchResult(d.Key, Cosine(q, d.Value)))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Take(topK)];
    }

    private double[] EmbedNormalized(string text)
    {
        if (text is null)
            throw new ArgumentNullException(nameof(text));

        var vector = _embedder.Embed(text)
            ?? throw new InvalidOperationException("Эмбеддер вернул null вместо вектора.");
        if (vector.Length != _embedder.Dimensions)
            throw new InvalidOperationException(
                $"Эмбеддер вернул вектор длины {vector.Length}, ожидалась {_embedder.Dimensions}.");
        foreach (var value in vector)
            if (!double.IsFinite(value))
                throw new InvalidOperationException("Эмбеддер вернул NaN или бесконечность.");

        return Normalize(vector);
    }

    private static double[] Normalize(double[] vector)
    {
        var normSquared = 0.0;
        foreach (var value in vector) normSquared += value * value;

        var result = new double[vector.Length];
        if (normSquared < 1e-24) return result;

        var norm = Math.Sqrt(normSquared);
        for (var i = 0; i < vector.Length; i++)
            result[i] = vector[i] / norm;
        return result;
    }

    private static double Cosine(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length && i < b.Length; i++) sum += a[i] * b[i];
        return Math.Clamp(sum, -1.0, 1.0);
    }
}

/// <summary>
/// Офлайн-эмбеддер: хешированный bag-of-words без внешних зависимостей.
/// Токенизация не зависит от культуры и пунктуации; корзина считается
/// стабильным FNV-1a по UTF-16, а не рандомизированным string.GetHashCode().
/// Это дефолтный эмбеддер; ONNX подключается отдельно как opt-in.
/// </summary>
public sealed class BagOfWordsEmbedder : IEmbedder
{
    private static readonly char[] TokenTrimChars =
    [
        ',', '.', ';', ':', '!', '?', '«', '»', '“', '”', '„', '‟',
        '"', '\'', '(', ')', '[', ']', '{', '}', '—', '–', '−', '…',
    ];

    public int Dimensions { get; }

    public BagOfWordsEmbedder(int dimensions = 256)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions),
                "Размерность bag-of-words должна быть положительной.");
        Dimensions = dimensions;
    }

    public double[] Embed(string text)
    {
        if (text is null)
            throw new ArgumentNullException(nameof(text));

        var vector = new double[Dimensions];
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim(TokenTrimChars);
            if (token.Length == 0) continue;

            var word = token.ToLowerInvariant();
            vector[StableBucket(word)] += 1;
        }

        return vector;
    }

    private int StableBucket(string word)
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

        return (int)(hash % (uint)Dimensions);
    }
}
