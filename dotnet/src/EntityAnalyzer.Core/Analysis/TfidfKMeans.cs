namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// R2-3 «Тематическая кластеризация корпуса»: TF-IDF по токенам +
/// k-means, полностью офлайн и без единого NuGet. Детерминизм: начальные
/// центроиды — максимально разнесённые документы (крайние точки), фикс
/// число итераций; одинаковый вход → одинаковые темы на любой машине.
/// </summary>
public static class TfidfKMeans
{
    public sealed record Result(
        IReadOnlyDictionary<string, int> Assignments,
        IReadOnlyDictionary<int, IReadOnlyList<string>> TopTermsPerTheme);

    private static readonly char[] Separators = { ' ', '\n', '\r', '\t' };

    /// <summary>Токенизация: слова ≥4 букв в нижнем регистре (служебные отсечены).</summary>
    public static IReadOnlyList<string> Tokenize(string text) =>
        [.. text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(',', '.', ';', ':', '!', '?', '«', '»', '—'))
            .Where(w => w.Length >= 4)
            .Select(w => w.ToLowerInvariant())];

    public static Result Cluster(IReadOnlyDictionary<string, string> documents, int k, int iterations = 25)
    {
        if (documents.Count == 0)
            return new Result(new Dictionary<string, int>(), new Dictionary<int, IReadOnlyList<string>>());

        // ── Словарь и частоты ──
        var docTokens = documents.ToDictionary(d => d.Key, d => Tokenize(d.Value), StringComparer.Ordinal);
        var vocabulary = docTokens.Values.SelectMany(t => t).Distinct().OrderBy(w => w, StringComparer.Ordinal).ToList();
        var vocabIndex = vocabulary.Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i);

        var df = new int[vocabulary.Count];
        var tf = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var (name, tokens) in docTokens)
        {
            var vector = new double[vocabulary.Count];
            foreach (var t in tokens) vector[vocabIndex[t]]++;
            tf[name] = vector;
        }

        Array.Clear(df);
        foreach (var tokens in docTokens.Values)
            foreach (var t in tokens.Distinct())
                df[vocabIndex[t]]++;

        var n = documents.Count;
        var vectors = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var (name, vector) in tf)
        {
            var total = vector.Sum();
            var v = new double[vocabulary.Count];
            for (var i = 0; i < v.Length; i++)
            {
                if (vector[i] == 0 || total == 0 || df[i] == 0) continue;
                var idf = Math.Log((double)n / df[i]) + 1.0;
                v[i] = vector[i] / total * idf;
            }
            vectors[name] = v;
        }

        // ── k-means: инициализация крайними точками ──
        var effectiveK = Math.Min(k, n);
        var names = vectors.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var centroids = new List<double[]> { (double[])vectors[names[0]].Clone() };
        while (centroids.Count < effectiveK)
        {
            // Дальнейший документ от уже выбранных центроидов.
            string? best = null;
            var bestDist = -1.0;
            foreach (var name in names)
            {
                if (centroids.Any(c => ReferenceEquals(c, vectors[name]))) continue;
                var d = centroids.Min(c => Distance(c, vectors[name]));
                if (!(d > bestDist)) continue;
                bestDist = d;
                best = name;
            }
            if (best is null) break;
            centroids.Add((double[])vectors[best].Clone());
        }

        var assignment = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var it = 0; it < iterations; it++)
        {
            var changed = false;
            foreach (var name in names)
            {
                var nearest = 0;
                var nearestDist = double.MaxValue;
                for (var c = 0; c < centroids.Count; c++)
                {
                    var d = Distance(centroids[c], vectors[name]);
                    if (!(d < nearestDist)) continue;
                    nearestDist = d;
                    nearest = c;
                }
                if (!assignment.TryGetValue(name, out var prev) || prev != nearest)
                {
                    assignment[name] = nearest;
                    changed = true;
                }
            }
            if (!changed && it > 0) break;

            // Обновление центроидов: среднее членов темы.
            for (var c = 0; c < centroids.Count; c++)
            {
                var members = names.Where(name => assignment.GetValueOrDefault(name) == c).ToList();
                if (members.Count == 0) continue;
                var mean = new double[vocabulary.Count];
                foreach (var m in members)
                    for (var i = 0; i < mean.Length; i++)
                        mean[i] += vectors[m][i];
                for (var i = 0; i < mean.Length; i++) mean[i] /= members.Count;
                centroids[c] = mean;
            }
        }

        // ── Топ-термины тем: ближайшие слова к центроиду ──
        var topTerms = new Dictionary<int, IReadOnlyList<string>>();
        for (var c = 0; c < centroids.Count; c++)
        {
            topTerms[c] = [.. Enumerable.Range(0, vocabulary.Count)
                .OrderByDescending(i => centroids[c][i])
                .Take(5)
                .Where(i => centroids[c][i] > 0)
                .Select(i => vocabulary[i])];
        }

        return new Result(assignment, topTerms);
    }

    private static double Distance(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }
        return sum;
    }
}
