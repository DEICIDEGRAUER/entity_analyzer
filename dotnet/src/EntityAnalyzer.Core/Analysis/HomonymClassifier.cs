namespace EntityAnalyzer.Core.Analysis;

/// <summary>Смысл омонима: личное имя, топоним или аббревиатура.</summary>
public enum HomonymSense
{
    PersonName,
    Toponym,
    Abbreviation,
}

/// <summary>
/// R1-25 «Классификатор омонимии»: контекстная модель различает
/// «Наг»-имя / топоним / аббревиатура. Эвристика офлайн (без ML-пакетов):
/// 1) все заглавные + точка → аббревиатура; 2) предлог «в/на/у/к/из/от»
/// перед словом → топоним; 3) персональный контекст (жрец, жрица, сын,
/// дочь, культ, имя, голос и т.п. рядом) → личное имя; 4) по умолчанию —
/// личное имя (доминирующий смысл в корпусах Ктулху). Контракт готов к
/// замене на обучаемую модель.
/// </summary>
public static class HomonymClassifier
{
    private static readonly HashSet<string> PlacePrepositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "в", "во", "на", "у", "к", "ко", "из", "от", "до", "под", "над", "возле", "около",
    };

    private static readonly HashSet<string> PersonContextWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "жрец", "жрецы", "жрица", "сын", "дочь", "брат", "сестра", "культ",
        "имя", "голос", "лицо", "рука", "глас", "пророк", "ученик", "слуга",
        "сказал", "произнёс", "шептал", "воскликнул", "молвил",
    };

    public static HomonymSense Classify(string term, string sentenceContext)
    {
        if (string.IsNullOrWhiteSpace(term)) return HomonymSense.PersonName;

        // 1. Аббревиатура: все буквы заглавные (и слово не одно-буквенное).
        var letters = term.Where(char.IsLetter).ToList();
        if (letters.Count >= 2 && letters.All(char.IsUpper))
            return HomonymSense.Abbreviation;

        // Разбор контекста: слово перед термином + окно после.
        var words = sentenceContext.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(',', '.', ';', ':', '!', '?', '«', '»'))
            .Where(w => w.Length > 0)
            .ToList();
        var idx = FindTermIndex(words, term);

        // 2. Предлог места непосредственно перед термином → топоним.
        if (idx > 0 && PlacePrepositions.Contains(words[idx - 1]))
            return HomonymSense.Toponym;

        // 3. Персональный контекст в предложении → личное имя.
        foreach (var w in words)
            if (PersonContextWords.Contains(w.TrimEnd('.')))
                return HomonymSense.PersonName;

        return HomonymSense.PersonName;
    }

    private static int FindTermIndex(List<string> words, string term)
    {
        for (var i = 0; i < words.Count; i++)
            if (string.Equals(words[i].TrimEnd('.', ','), term.TrimEnd('.', ','), StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
