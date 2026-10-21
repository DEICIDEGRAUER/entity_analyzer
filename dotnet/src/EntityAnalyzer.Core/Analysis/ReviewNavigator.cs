namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// Сводка проверки корпуса: сколько цитат уже размечено и в скольких файлах
/// ещё остались непроверенные находки. Считается по текущим значениям
/// <see cref="Mention.Verdict"/>, поэтому перед вызовом вердикты должны быть
/// применены к сессии.
/// </summary>
public readonly record struct ReviewSummary(int Total, int Reviewed, int FilesWithUnreviewed)
{
    public int Unreviewed => Total - Reviewed;

    /// <summary>Все цитаты корпуса получили вердикт (пустой корпус тоже считается завершённым).</summary>
    public bool IsComplete => Unreviewed == 0;
}

/// <summary>
/// Переход по файлам для проверки. Логика вынесена из WPF, чтобы её можно
/// было покрыть тестами без запуска окна.
/// </summary>
public static class ReviewNavigator
{
    /// <summary>
    /// Подсчитать прогресс по набору файлов. Файлы без единого упоминания
    /// не считаются работой, но и не мешают: они просто не попадают в
    /// <see cref="ReviewSummary.FilesWithUnreviewed"/>.
    /// </summary>
    public static ReviewSummary Summarize(IEnumerable<FileResult> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var total = 0;
        var reviewed = 0;
        var filesWithUnreviewed = 0;

        foreach (var file in files)
        {
            var unreviewedInFile = 0;
            foreach (var mention in file.Mentions)
            {
                total++;
                if (mention.HasVerdict) reviewed++;
                else unreviewedInFile++;
            }

            if (unreviewedInFile > 0) filesWithUnreviewed++;
        }

        return new ReviewSummary(total, reviewed, filesWithUnreviewed);
    }

    /// <summary>
    /// Следующий файл с хотя бы одной непроверенной цитатой после
    /// <paramref name="current"/>; в конце списка поиск продолжается с начала.
    /// Если текущего файла нет в списке, поиск начинается с первого элемента.
    /// Возвращает <c>null</c>, когда все цитаты проверены.
    /// </summary>
    public static FileResult? NextFileWithUnreviewed(
        IReadOnlyList<FileResult> files,
        FileResult? current)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return null;

        var start = -1;
        if (current is not null)
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (!ReferenceEquals(files[i], current)) continue;
                start = i;
                break;
            }
        }

        for (var step = 1; step <= files.Count; step++)
        {
            var candidate = files[(start + step) % files.Count];
            if (candidate.Mentions.Any(m => !m.HasVerdict)) return candidate;
        }

        return null;
    }
}
