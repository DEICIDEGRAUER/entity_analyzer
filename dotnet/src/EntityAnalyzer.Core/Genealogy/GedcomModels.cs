namespace EntityAnalyzer.Core.Genealogy;

/// <summary>
/// Дата GEDCOM: сырое значение плюс разобранные части (год/месяц/день),
/// признак приблизительности и русское отображение. Разбор терпимый:
/// непонятная строка сохраняется как есть, части остаются пустыми.
/// </summary>
public sealed class GedcomDate
{
    public string Raw { get; init; } = "";

    /// <summary>Год события (может быть отрицательным для «до н. э.»).</summary>
    public int? Year { get; init; }

    public int? Month { get; init; }
    public int? Day { get; init; }

    /// <summary>ABT/EST/CAL — приблизительная дата («ок.»).</summary>
    public bool IsAbout { get; init; }

    /// <summary>BEF/AFT/BET — оценочная дата («до»/«после»/«между»).</summary>
    public string Qualifier { get; init; } = "";

    /// <summary>Русское отображение: «ок. 12 января 1900», «после 1912», «1900».</summary>
    public string Display { get; init; } = "";

    /// <summary>Есть ли хоть какая-то дата (сырая строка не пуста).</summary>
    public bool IsEmpty => Raw.Length == 0;

    /// <summary>Год в виде текста или пустая строка.</summary>
    public string YearText => Year is { } y ? (y < 0 ? $"{Math.Abs(y)} до н. э." : y.ToString()) : "";
}

/// <summary>Человек из файла GEDCOM.</summary>
public sealed class GedcomPerson
{
    /// <summary>Идентификатор записи без обрамляющих @ (например, «I1»).</summary>
    public string Id { get; init; } = "";

    /// <summary>Полное имя в читаемом виде: «Иван Смирнов».</summary>
    public string Name { get; set; } = "";

    public string Given { get; set; } = "";
    public string Surname { get; set; } = "";

    /// <summary>M/F/U — как в GEDCOM.</summary>
    public string Sex { get; set; } = "U";

    /// <summary>Дополнительные имена (NAME, кроме первого): «Джон Смит».</summary>
    public List<string> AlternateNames { get; } = [];

    public GedcomDate? Birth { get; set; }
    public GedcomDate? Death { get; set; }
    public string BirthPlace { get; set; } = "";
    public string DeathPlace { get; set; } = "";
    public string Occupation { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>Семьи, где человек — ребёнок (FAMC).</summary>
    public List<string> ChildOf { get; } = [];

    /// <summary>PEDI по каждой семье: birth, adopted, foster, guardian…</summary>
    public Dictionary<string, string> ChildPedigree { get; } = new(StringComparer.Ordinal);

    /// <summary>QUAY по каждой семье: 0–3, если источник это указал.</summary>
    public Dictionary<string, string> ChildConfidence { get; } = new(StringComparer.Ordinal);

    /// <summary>Семьи, где человек — супруг (FAMS).</summary>
    public List<string> SpouseOf { get; } = [];

    /// <summary>Дробное имя для подписи, если полное пустое.</summary>
    public string DisplayName => Name.Length > 0 ? Name : Id;

    public override string ToString() => DisplayName;
}

/// <summary>Семья из файла GEDCOM: супруги, дети, брак.</summary>
public sealed class GedcomFamily
{
    public string Id { get; init; } = "";
    public string? HusbandId { get; set; }
    public string? WifeId { get; set; }
    public List<string> Children { get; } = [];
    public GedcomDate? Marriage { get; set; }
    public string MarriagePlace { get; set; } = "";
    public GedcomDate? Divorce { get; set; }

    /// <summary>Супруги, как они есть в записи (для текста).</summary>
    public IEnumerable<string> SpouseIds()
    {
        if (HusbandId is not null) yield return HusbandId;
        if (WifeId is not null) yield return WifeId;
    }
}

/// <summary>Разобранный документ GEDCOM: все люди и семьи плюс статистика разбора.</summary>
public sealed class GedcomDocument
{
    public List<GedcomPerson> Persons { get; } = [];
    public List<GedcomFamily> Families { get; } = [];

    /// <summary>Ссылки на несуществующие записи и прочие замечания разбора.</summary>
    public List<string> Warnings { get; } = [];

    private Dictionary<string, GedcomPerson>? _personIndex;
    private Dictionary<string, GedcomFamily>? _familyIndex;

    public IReadOnlyDictionary<string, GedcomPerson> PersonIndex =>
        _personIndex ??= Persons.ToDictionary(p => p.Id, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, GedcomFamily> FamilyIndex =>
        _familyIndex ??= Families.ToDictionary(f => f.Id, StringComparer.Ordinal);

    public GedcomPerson? Person(string? id) =>
        id is not null && PersonIndex.TryGetValue(id, out var p) ? p : null;

    public GedcomFamily? Family(string? id) =>
        id is not null && FamilyIndex.TryGetValue(id, out var f) ? f : null;
}
