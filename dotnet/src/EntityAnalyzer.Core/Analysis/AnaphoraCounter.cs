namespace EntityAnalyzer.Core.Analysis;

/// <summary>Косвенное упоминание: местоимение, привязанное к сущности.</summary>
public sealed record AnaphoraHit(
    string Pronoun,
    int Offset,
    int SentenceIndex,
    string ResolvedEntity,
    string Context);

/// <summary>
/// R2-4 «Анафора-режим»: после явного упоминания ближайшие местоимения
/// («он/она/оно/они») в окне предложений засчитываются как косвенные
/// упоминания той же сущности — привязка к ближайшей предыдущей. Наивная
/// эвристика без разбора: честный нижний предел «плотности присутствия».
/// Слой опционален и выключен по умолчанию (воспроизводимость прогонов).
/// </summary>
public static class AnaphoraCounter
{
    private static readonly HashSet<string> Pronouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "он", "она", "оно", "они",
    };

    public static IReadOnlyList<AnaphoraHit> Count(
        string normalizedText, IReadOnlyList<Mention> explicitMentions, int windowSentences = 2)
    {
        if (explicitMentions.Count == 0 || normalizedText.Length == 0)
            return [];

        // Границы предложений по точкам (нормализованный текст однострочный).
        var sentenceEnds = new List<int>();
        for (var i = 0; i < normalizedText.Length; i++)
            if (normalizedText[i] == '.' || normalizedText[i] == '!' || normalizedText[i] == '?')
                sentenceEnds.Add(i);

        int SentenceOf(int offset)
        {
            var lo = 0;
            while (lo < sentenceEnds.Count && sentenceEnds[lo] < offset) lo++;
            return lo;
        }

        var ordered = explicitMentions.OrderBy(m => m.Offset).ToList();
        var hits = new List<AnaphoraHit>();

        foreach (var m in ordered)
        {
            var startSentence = SentenceOf((int)m.Offset);
            var scanFrom = (int)(m.Offset + m.Variant.Length);
            if (scanFrom >= normalizedText.Length) continue;

            for (var s = startSentence + 1; s <= startSentence + windowSentences; s++)
            {
                var begin = s - 1 < 0 ? 0 : sentenceEnds[s - 1] + 1;
                if (s - 1 >= sentenceEnds.Count) break;
                var end = s < sentenceEnds.Count ? sentenceEnds[s] : normalizedText.Length;
                if (begin >= end) continue;

                var words = normalizedText[begin..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var pos = begin;
                foreach (var w in words)
                {
                    var wordStart = normalizedText.IndexOf(w, pos, StringComparison.Ordinal);
                    if (wordStart < 0) { pos += w.Length + 1; continue; }
                    var bare = w.Trim(',', '.', ';', ':', '!', '?');
                    if (Pronouns.Contains(bare))
                        hits.Add(new AnaphoraHit(
                            bare.ToLowerInvariant(), wordStart, s, m.Entity,
                            normalizedText[Math.Max(0, begin)..Math.Min(normalizedText.Length, end)].Trim()));
                    pos = wordStart + w.Length;
                }
            }
        }

        return [.. hits.OrderBy(h => h.Offset)];
    }
}
