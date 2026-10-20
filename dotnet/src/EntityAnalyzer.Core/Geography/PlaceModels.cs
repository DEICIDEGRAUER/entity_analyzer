namespace EntityAnalyzer.Core.Geography;

/// <summary>Класс места из плана группы 4 (item 19).</summary>
public enum PlaceClass
{
    City,
    Village,
    Port,
    Island,
    Mountain,
    Plateau,
    Desert,
    Lake,
    River,
    Sea,
    Ocean,
    Forest,
    Valley,
    Cave,
    Temple,
    Tower,
    Country,
    Continent,
    Planet,
}

/// <summary>Русские подписи классов и разбор значения JSON («city», «village»…).</summary>
public static class PlaceClasses
{
    public static readonly IReadOnlyList<PlaceClass> All = Enum.GetValues<PlaceClass>();

    public static string Label(PlaceClass cls) => cls switch
    {
        PlaceClass.City => "город",
        PlaceClass.Village => "деревня",
        PlaceClass.Port => "порт",
        PlaceClass.Island => "остров",
        PlaceClass.Mountain => "гора",
        PlaceClass.Plateau => "плато",
        PlaceClass.Desert => "пустыня",
        PlaceClass.Lake => "озеро",
        PlaceClass.River => "река",
        PlaceClass.Sea => "море",
        PlaceClass.Ocean => "океан",
        PlaceClass.Forest => "лес",
        PlaceClass.Valley => "долина",
        PlaceClass.Cave => "пещера",
        PlaceClass.Temple => "храм",
        PlaceClass.Tower => "башня",
        PlaceClass.Country => "страна",
        PlaceClass.Continent => "континент",
        PlaceClass.Planet => "планета",
        _ => cls.ToString().ToLowerInvariant(),
    };

    /// <summary>Разбор ключа JSON; неизвестный класс — null.</summary>
    public static PlaceClass? TryParse(string key) =>
        Enum.TryParse<PlaceClass>(key, ignoreCase: true, out var cls) && Enum.IsDefined(cls) ? cls : null;

    /// <summary>Цвет маркера на карте (общая палитра с кодексом).</summary>
    public static string Color(PlaceClass cls) => cls switch
    {
        PlaceClass.City => "#4a9eff",
        PlaceClass.Village => "#7ec8a9",
        PlaceClass.Port => "#5ec8d8",
        PlaceClass.Island => "#3fb6a8",
        PlaceClass.Mountain => "#b08968",
        PlaceClass.Plateau => "#c2a36b",
        PlaceClass.Desert => "#d9b45b",
        PlaceClass.Lake => "#6fa8dc",
        PlaceClass.River => "#7fb3d5",
        PlaceClass.Sea => "#4c86c6",
        PlaceClass.Ocean => "#33619a",
        PlaceClass.Forest => "#5f9e55",
        PlaceClass.Valley => "#8aa86b",
        PlaceClass.Cave => "#8c7b6b",
        PlaceClass.Temple => "#c9a227",
        PlaceClass.Tower => "#b58bd9",
        PlaceClass.Country => "#d97b7b",
        PlaceClass.Continent => "#e08f5a",
        PlaceClass.Planet => "#bd93f9",
        _ => "#9aa3b2",
    };
}

/// <summary>Координаты места.</summary>
public sealed record GeoPoint(double Lat, double Lon)
{
    public string Text => $"{Math.Abs(Lat):0.####}° {(Lat >= 0 ? "с. ш." : "ю. ш.")}, {Math.Abs(Lon):0.####}° {(Lon >= 0 ? "в. д." : "з. д.")}";
}

/// <summary>Сведение о месте во внутренней базе координат.</summary>
public sealed record GazetteerPlace(
    string Name,
    IReadOnlyList<string> Aliases,
    PlaceClass Class,
    GeoPoint? Point,
    bool Fictional,
    string Note)
{
    public string KindLabel => Fictional ? "вымышленное" : "реальное";
}

/// <summary>Найденное в тексте упоминание места (до агрегации).</summary>
public sealed record PlaceMention(
    string Name,
    PlaceClass Class,
    string PatternName,
    string File,
    string Path,
    int Line,
    string Snippet);

/// <summary>Цитата-упоминание места в корпусе.</summary>
public sealed record PlaceQuote(string File, string Path, int Line, string Snippet);

/// <summary>
/// Агрегированная запись географии: каноническое имя, класс, координаты
/// (если место есть в базе), счётчики и цитаты из корпуса.
/// </summary>
public sealed record PlaceEntry(
    string Name,
    PlaceClass Class,
    bool Fictional,
    GeoPoint? Point,
    string GazetteerNote,
    int Mentions,
    int FileCount,
    IReadOnlyList<string> Variants,
    IReadOnlyList<PlaceQuote> Quotes)
{
    /// <summary>Подпись для списка: «город · вымышленное».</summary>
    public string KindLabel => PlaceClasses.Label(Class) + (Fictional ? " · вымышленное" : " · реальное");
}
