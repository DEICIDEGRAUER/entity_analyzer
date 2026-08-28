namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// R1-24 «Смысловой поиск»: контракт эмбеддера — текст → вектор.
/// Офлайн-реализация (bag-of-words) встроена; семантические модели
/// (ONNX) подключаются этим же контрактом после согласования зависимости.
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
/// top-k поиск. Хранит векторы документов в памяти; персистенция —
/// через сериализацию вызывающим слоем.
/// </summary>
public sealed class VectorIndex
{
    private readonly IEmbedder _embedder;
    private readonly List<(string Id, double[] Vector)> _documents = [];

    public VectorIndex(IEmbedder embedder) => _embedder = embedder;

    public int Count => _documents.Count;

    public void Add(string id, string text) =>
        _documents.Add((id, Normalize(_embedder.Embed(text))));

    /// <summary>Top-k ближайших документов к запросу.</summary>
    public IReadOnlyList<VectorSearchResult> Search(string query, int topK = 10)
    {
        if (_documents.Count == 0 || topK <= 0) return [];
        var q = Normalize(_embedder.Embed(query));
        return [.. _documents
            .Select(d => new VectorSearchResult(d.Id, Cosine(q, d.Vector)))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Take(topK)];
    }

    private static double[] Normalize(double[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => x * x));
        if (norm < 1e-12) return v;
        return [.. v.Select(x => x / norm)];
    }

    private static double Cosine(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length && i < b.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}

/// <summary>Офлайн-эмбеддер: хешированный bag-of-words (без зависимостей).</summary>
public sealed class BagOfWordsEmbedder : IEmbedder
{
    public int Dimensions { get; }

    public BagOfWordsEmbedder(int dimensions = 256) => Dimensions = dimensions;

    public double[] Embed(string text)
    {
        var v = new double[Dimensions];
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim(',', '.', ';', ':', '!', '?').ToLowerInvariant();
            if (w.Length == 0) continue;
            v[Math.Abs(w.GetHashCode()) % Dimensions] += 1;
        }
        return v;
    }
}
