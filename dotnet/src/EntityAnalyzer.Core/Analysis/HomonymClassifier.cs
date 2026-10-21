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
/// «Наг»-имя / топоним / аббревиатура. Эвристика офлайн (без ML-пакетов)
/// использует локальное окно вокруг термина, а не всё предложение:
/// 1) аббревиатурный паттерн (все заглавные, в том числе с точками) —
///    сильный признак, но явный персональный или местный контекст его
///    перевешивает; 2) предлог места перед термином или рядом с ним
///    (в том числе через прилагательное/«город») → топоним; 3) слова
///    персонального контекста (жрец, сын, культ, голос, глаголы речи)
///    → личное имя; 4) по умолчанию — личное имя (доминирующий смысл в
///    корпусах Ктулху). Термин сопоставляется с учётом падежных окончаний.
/// Контракт готов к замене на обучаемую модель.
/// </summary>
public static class HomonymClassifier
{
    private const int ContextRadius = 4;

    // Силы признаков подобраны так, чтобы локальный контекст мог уточнить
    // все-заглавный термин, но пунктирная аббревиатура оставалась сильной.
    private const int AbbreviationScore = 30;
    private const int DottedAbbreviationScore = 45;
    private const int AdjacentPersonScore = 35;
    private const int NearPersonScore = 30;
    private const int WindowPersonScore = 25;
    private const int FarPersonScore = 20;
    private const int AdjacentPlaceCueScore = 34;
    private const int NearPlaceCueScore = 30;
    private const int WindowPlaceCueScore = 24;
    private const int FarPlaceCueScore = 18;
    private const int AdjacentPlacePrepositionScore = 40;
    private const int DelayedPlacePrepositionScore = 32;
    private const int DelayedPlacePrepositionWithCueScore = 35;

    private static readonly HashSet<string> PlacePrepositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "в", "во", "на", "у", "к", "ко", "из", "от", "до", "под", "над", "возле", "около",
        "близ", "вблизи", "рядом", "среди", "через", "сквозь", "мимо", "вдоль",
    };

    // Основы, а не точные словоформы: «жреца», «жрецом», «сыном», «дочерью»
    // должны срабатывать так же, как исходные слова.
    private static readonly string[] PersonCueStems =
    [
        "жрец", "жриц", "сын", "дочер", "брат", "сестр", "культ",
        "имя", "имени", "голос", "лиц", "рук", "глас", "пророк", "ученик", "слуг",
        "сказа", "произн", "шепт", "восклиц", "молв",
        "господин", "госпож", "мистер", "миссис", "лорд", "цар", "корол",
        "человек", "муж", "жен", "отц", "мать", "матерью", "враг",
    ];

    private static readonly string[] PlaceCueStems =
    [
        "город", "страна", "страны", "страну", "страной", "стране", "странах",
        "земл", "пустын", "берег", "дорог", "путь", "пути", "путем", "путём",
        "порт", "дерев", "посел", "остров", "север", "юг", "запад", "восток",
        "окрест", "границ", "провинц", "столиц", "крепост", "храм",
        "прибы", "жил", "наход", "располож", "посет", "отправ",
    ];

    private static readonly HashSet<string> InflectionSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "а", "я", "у", "ю", "е", "и", "ы",
        "ом", "ем", "ой", "ей", "ах", "ях", "ам", "ям", "ов", "ев",
        "ий", "ия", "ию", "ием", "ыми", "ими", "его", "ого", "ему", "ому",
    };

    private static readonly char[] TokenTrimChars =
    [
        ' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?',
        '«', '»', '“', '”', '„', '‟', '"', '\'', '(', ')', '[', ']',
        '—', '–', '−', '…',
    ];

    public static HomonymSense Classify(string term, string sentenceContext)
    {
        if (string.IsNullOrWhiteSpace(term)) return HomonymSense.PersonName;

        var abbreviationScore = AbbreviationScoreFor(term);
        if (string.IsNullOrWhiteSpace(sentenceContext))
            return abbreviationScore > 0 ? HomonymSense.Abbreviation : HomonymSense.PersonName;

        var words = Tokenize(sentenceContext);
        var idx = FindTermIndex(words, term);
        if (idx < 0)
            return abbreviationScore > 0 ? HomonymSense.Abbreviation : HomonymSense.PersonName;

        var personScore = 0;
        var placeScore = 0;

        // Локальное окно: слова далеко от термина не должны перетягивать
        // решение, особенно если рядом есть явный предлог места.
        var from = Math.Max(0, idx - ContextRadius);
        var to = Math.Min(words.Count - 1, idx + ContextRadius);
        for (var i = from; i <= to; i++)
        {
            if (i == idx) continue;
            var distance = Math.Abs(i - idx);
            if (ContainsStem(words[i], PersonCueStems))
                personScore = Math.Max(personScore, PersonScoreForDistance(distance));
            if (ContainsStem(words[i], PlaceCueStems))
                placeScore = Math.Max(placeScore, PlaceCueScoreForDistance(distance));
        }

        // Предлог места непосредственно перед термином — самый сильный
        // местный признак. Если между предлогом и термином есть определение
        // или слово «город», предлог всё равно учитывается.
        if (idx > 0 && PlacePrepositions.Contains(words[idx - 1]))
        {
            placeScore = Math.Max(placeScore, AdjacentPlacePrepositionScore);
        }
        else
        {
            for (var distance = 2; distance <= 3 && idx - distance >= 0; distance++)
            {
                if (!PlacePrepositions.Contains(words[idx - distance])) continue;
                var hasPlaceCue = false;
                for (var j = idx - distance + 1; j < idx; j++)
                {
                    if (ContainsStem(words[j], PlaceCueStems))
                    {
                        hasPlaceCue = true;
                        break;
                    }
                }

                placeScore = Math.Max(placeScore,
                    hasPlaceCue ? DelayedPlacePrepositionWithCueScore : DelayedPlacePrepositionScore);
                break;
            }
        }

        // Сильный локальный контекст уточняет CAPS-омоним. При равенстве
        // явный контекст важнее простого аббревиатурного паттерна;
        // пунктирная аббревиатура остаётся сильнее обычных контекстных слов.
        if (placeScore > 0 && placeScore >= personScore && placeScore >= abbreviationScore)
            return HomonymSense.Toponym;
        if (personScore > 0 && personScore >= placeScore && personScore >= abbreviationScore)
            return HomonymSense.PersonName;
        if (abbreviationScore > 0)
            return HomonymSense.Abbreviation;
        return HomonymSense.PersonName;
    }

    private static int AbbreviationScoreFor(string term)
    {
        var letters = term.Where(char.IsLetter).ToList();
        if (letters.Count < 2 || !letters.All(char.IsUpper)) return 0;
        return term.Contains('.') ? DottedAbbreviationScore : AbbreviationScore;
    }

    private static List<string> Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(TokenTrimChars))
            .Where(w => w.Length > 0)
            .ToList();

    private static int FindTermIndex(List<string> words, string term)
    {
        var target = term.Trim(TokenTrimChars);
        if (target.Length == 0) return -1;

        for (var i = 0; i < words.Count; i++)
        {
            var token = words[i];
            if (string.Equals(token, target, StringComparison.OrdinalIgnoreCase))
                return i;

            // «Наг» в предложении может стоять в косвенном падеже:
            // «Нага», «Нагу», «Нагом», «Наге». Ограниченный набор
            // окончаний не даёт случайно сопоставить «Награда».
            if (token.Length > target.Length
                && token.StartsWith(target, StringComparison.OrdinalIgnoreCase)
                && InflectionSuffixes.Contains(token[target.Length..]))
                return i;
        }

        return -1;
    }

    private static bool ContainsStem(string token, string[] stems)
    {
        foreach (var stem in stems)
            if (token.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static int PersonScoreForDistance(int distance) => distance switch
    {
        1 => AdjacentPersonScore,
        2 => NearPersonScore,
        3 => WindowPersonScore,
        _ => FarPersonScore,
    };

    private static int PlaceCueScoreForDistance(int distance) => distance switch
    {
        1 => AdjacentPlaceCueScore,
        2 => NearPlaceCueScore,
        3 => WindowPlaceCueScore,
        _ => FarPlaceCueScore,
    };
}
