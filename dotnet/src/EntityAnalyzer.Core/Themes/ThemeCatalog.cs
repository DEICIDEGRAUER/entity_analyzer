namespace EntityAnalyzer.Core.Themes;

/// <summary>
/// R3-1 (item 52): тематический словарь — тема и её слова (леммы/основы).
/// Встроенные словари дополняются пользовательскими словами
/// (user_themes.json); тема может быть и целиком пользовательской.
/// </summary>
public sealed class ThemeDictionary
{
    public string Name { get; set; } = "";
    public List<string> Words { get; set; } = [];
    public bool Builtin { get; set; }

    public ThemeDictionary() { }

    public ThemeDictionary(string name, IEnumerable<string> words, bool builtin = false)
    {
        Name = name;
        Words = [.. words];
        Builtin = builtin;
    }

    public override string ToString() => Name;
}

/// <summary>
/// R3-1 (item 52): встроенные тематические словари «География»,
/// «Архитектура», «Культ», «Битвы», «Пророчества» и их слияние
/// с пользовательскими словами из user_themes.json.
/// </summary>
public static class ThemeCatalog
{
    public static IReadOnlyList<ThemeDictionary> Builtin { get; } =
    [
        new("География",
        [
            "море", "океан", "берег", "побережье", "остров", "гора", "скала", "холм",
            "лес", "поле", "равнина", "долина", "река", "озеро", "болото", "пустыня",
            "дорога", "пещера", "грот", "залив", "бухта", "туман", "земля", "край",
        ], builtin: true),

        new("Архитектура",
        [
            "здание", "башня", "храм", "стена", "ворота", "дверь", "лестница", "зал",
            "комната", "коридор", "подземелье", "крыша", "колонна", "арка", "мост",
            "крепость", "цитадель", "фундамент", "окно", "порог", "галерея", "ниша", "кладка",
        ], builtin: true),

        new("Культ",
        [
            "культ", "обряд", "ритуал", "жертва", "алтарь", "жрец", "жрица", "молитва",
            "поклонение", "почитание", "святилище", "таинство", "посвящение", "символ",
            "идол", "статуя", "гимн", "секта", "верующие",
        ], builtin: true),

        new("Битвы",
        [
            "битва", "бой", "сражение", "война", "армия", "воин", "меч", "копьё", "щит",
            "кровь", "удар", "враг", "победа", "осада", "штурм", "легион", "отряд",
            "рать", "сеча", "поражение", "латы", "стрела",
        ], builtin: true),

        new("Пророчества",
        [
            "пророчество", "предсказание", "знамение", "звезда", "судьба", "рок",
            "грядущее", "предвестник", "вестник", "видение", "сон", "знак", "голос",
            "провидение", "откровение", "прорицание", "оракул",
        ], builtin: true),
    ];

    /// <summary>
    /// Встроенные словари + пользовательские слова; неизвестные темы из
    /// user_themes.json добавляются как собственные словари в конце.
    /// </summary>
    public static List<ThemeDictionary> Merge(IReadOnlyDictionary<string, List<string>>? userWords)
    {
        var result = new List<ThemeDictionary>();
        foreach (var builtin in Builtin)
        {
            var words = new List<string>(builtin.Words);
            if (userWords is not null && userWords.TryGetValue(builtin.Name, out var extra))
            {
                foreach (var w in extra)
                {
                    var word = (w ?? "").Trim();
                    if (word.Length == 0) continue;
                    if (!words.Any(x => string.Equals(x, word, StringComparison.OrdinalIgnoreCase)))
                        words.Add(word);
                }
            }
            result.Add(new ThemeDictionary(builtin.Name, words, builtin: true));
        }

        if (userWords is not null)
        {
            foreach (var (name, words) in userWords)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (result.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new ThemeDictionary(name,
                    words.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()),
                    builtin: false));
            }
        }
        return result;
    }
}
