using System.Text.RegularExpressions;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Уровень достоверности упоминания.</summary>
public enum ConfidenceLevel
{
    High,
    Medium,
    Low,
}

/// <summary>
/// R1-19 «Живой статус достоверности»: бейдж уверенности упоминания.
///
/// Правило (детерминированное, покрыто тестами):
///   • точное совпадение по словарю → High;
///   • нечёткое совпадение: процент из маркера «[fuzzy:N%]» в варианте —
///     N ≥ 85 → Medium, иначе Low; без распознанного процента → Low;
///   • повторы (Repeats &gt; 1) добавляют причину, уровень не поднимают.
///
/// Порча/морфология входят в словарь вариантов и пост-фактум неотличимы от
/// точного совпадения — если движок начнёт маркировать такие варианты,
/// правило расширяется здесь, в единственном месте.
/// </summary>
public sealed record ConfidenceBadge(ConfidenceLevel Level, string Label, IReadOnlyList<string> Reasons)
{
    private static readonly Regex FuzzyScore = new(@"\[fuzzy:(\d+)%\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ConfidenceBadge From(Mention mention)
    {
        var reasons = new List<string>();
        ConfidenceLevel level;

        if (mention.IsFuzzy)
        {
            var match = FuzzyScore.Match(mention.Variant);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var score) && score >= 85)
            {
                level = ConfidenceLevel.Medium;
                reasons.Add($"нечёткое совпадение {score}%");
            }
            else if (match.Success)
            {
                level = ConfidenceLevel.Low;
                reasons.Add($"нечёткое совпадение {match.Groups[1].Value}%");
            }
            else
            {
                level = ConfidenceLevel.Low;
                reasons.Add("нечёткое совпадение");
            }
        }
        else
        {
            level = ConfidenceLevel.High;
            reasons.Add("точное совпадение по словарю");
        }

        if (mention.Repeats > 1)
            reasons.Add($"повторы в файле: {mention.Repeats}");

        var label = level switch
        {
            ConfidenceLevel.High => "высокая",
            ConfidenceLevel.Medium => "средняя",
            _ => "низкая",
        };
        return new ConfidenceBadge(level, label, reasons);
    }

    /// <summary>
    /// Суффикс для строки интерфейса: высокая достоверность не шумит —
    /// пометка появляется только при отклонении от точного совпадения.
    /// </summary>
    public string ToSuffix()
    {
        if (Level == ConfidenceLevel.High) return string.Empty;
        return $" · достоверность: {Label} ({Reasons[0]})";
    }
}
