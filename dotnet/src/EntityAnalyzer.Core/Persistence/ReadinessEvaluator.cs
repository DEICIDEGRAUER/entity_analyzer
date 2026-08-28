namespace EntityAnalyzer.Core.Persistence;

/// <summary>Outcome of the start-button readiness check.</summary>
public sealed record ReadinessResult(bool CanStart, IReadOnlyList<string> Blockers)
{
    public string Hint => CanStart ? "" : "⚠ " + string.Join("  ·  ", Blockers);
}

/// <summary>
/// Pure decision logic for whether analysis can start, with human-readable
/// blockers. Existence of the corpus directory is injected so the check is
/// deterministic and testable without touching the file system.
/// </summary>
public static class ReadinessEvaluator
{
    public static ReadinessResult Evaluate(
        string? corpusDir, bool corpusExists, int variantCount, bool isBusy)
    {
        var blockers = new List<string>(4);

        if (string.IsNullOrWhiteSpace(corpusDir))
            blockers.Add("Не выбрана папка корпуса");
        else if (!corpusExists)
            blockers.Add($"Папка корпуса не найдена: {corpusDir.Trim()}");

        if (variantCount <= 0)
            blockers.Add("Кодекс пуст — добавьте карточку на странице «Кодекс»");

        if (isBusy)
            blockers.Add("Анализ уже выполняется");

        return new ReadinessResult(blockers.Count == 0, blockers);
    }
}
