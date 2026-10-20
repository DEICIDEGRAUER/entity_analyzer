namespace EntityAnalyzer.Core.Genealogy;

/// <summary>
/// Тип родственной связи. Он задаётся явно: биологическое родство не
/// считается «обычным» значением по умолчанию для приёмных, опекунских,
/// крёстных и вымышленных связей.
/// </summary>
public enum FamilyRelationKind
{
    Unknown,
    Biological,
    Adoptive,
    Foster,
    Guardian,
    Godparent,
    Named,
    Origin,
    Partner,
}

/// <summary>Статус достоверности утверждения о связи или персоне.</summary>
public enum FamilyConfidence
{
    Unknown = 0,
    Proven = 1,
    Likely = 2,
    Hypothesis = 3,
    Disputed = 4,
}

/// <summary>Вариант выбора уверенности для ComboBox в редакторе связей.</summary>
public sealed record ConfidenceChoice(FamilyConfidence Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Подписи, классификация и визуальные сокращения типов родства.</summary>
public static class FamilyRelationKinds
{
    public static string Label(FamilyRelationKind kind) => kind switch
    {
        FamilyRelationKind.Biological => "биологическая",
        FamilyRelationKind.Adoptive => "приёмная",
        FamilyRelationKind.Foster => "воспитание",
        FamilyRelationKind.Guardian => "опека",
        FamilyRelationKind.Godparent => "крёстная",
        FamilyRelationKind.Named => "названая",
        FamilyRelationKind.Origin => "порождение",
        FamilyRelationKind.Partner => "партнёрство",
        _ => "не указана",
    };

    public static string ShortLabel(FamilyRelationKind kind) => kind switch
    {
        FamilyRelationKind.Biological => "биол.",
        FamilyRelationKind.Adoptive => "приёмн.",
        FamilyRelationKind.Foster => "воспит.",
        FamilyRelationKind.Guardian => "опека",
        FamilyRelationKind.Godparent => "крёстн.",
        FamilyRelationKind.Named => "назван.",
        FamilyRelationKind.Origin => "порожд.",
        FamilyRelationKind.Partner => "партн.",
        _ => "связь",
    };

    /// <summary>Может ли связь участвовать в поколениях как «родитель → ребёнок».</summary>
    public static bool IsParentLike(FamilyRelationKind kind) =>
        kind is FamilyRelationKind.Biological
            or FamilyRelationKind.Adoptive
            or FamilyRelationKind.Foster
            or FamilyRelationKind.Guardian
            or FamilyRelationKind.Godparent
            or FamilyRelationKind.Named
            or FamilyRelationKind.Origin
            or FamilyRelationKind.Unknown;

    public static bool IsPartner(FamilyRelationKind kind) => kind == FamilyRelationKind.Partner;

    /// <summary>
    /// Определяет тип родства по идентификатору и подписям типа связи.
    /// Это позволяет пользовательским связям («приёмный отец», «опекун»)
    /// работать в древе без жёсткого перечисления всех возможных ID.
    /// </summary>
    public static FamilyRelationKind Classify(string? relationId, string? forward, string? reverse = null)
    {
        var id = (relationId ?? "").Trim().ToLowerInvariant();
        switch (id)
        {
            case "parent":
            case "biological":
            case "birth":
                return FamilyRelationKind.Biological;
            case "adopt":
            case "adopted":
            case "adoption":
                return FamilyRelationKind.Adoptive;
            case "foster":
                return FamilyRelationKind.Foster;
            case "guardian":
            case "guard":
                return FamilyRelationKind.Guardian;
            case "godparent":
            case "godfather":
            case "godmother":
                return FamilyRelationKind.Godparent;
            case "named":
            case "godchild":
                return FamilyRelationKind.Named;
            case "spawn":
            case "origin":
            case "created":
                return FamilyRelationKind.Origin;
            case "partner":
            case "spouse":
            case "marriage":
            case "married":
                return FamilyRelationKind.Partner;
        }

        var text = Normalize($"{forward} {reverse}");
        if (text.Length == 0) return FamilyRelationKind.Unknown;

        // Сначала более specific-типы: «крёстный отец» не должен стать «отцом».
        if (ContainsAny(text, "крестн", "крёстн", "godparent", "godfather", "godmother"))
            return FamilyRelationKind.Godparent;
        if (ContainsAny(text, "назван", "названый", "названая", "named"))
            return FamilyRelationKind.Named;
        if (ContainsAny(text, "усынов", "удочер", "adopt"))
            return FamilyRelationKind.Adoptive;
        if (ContainsAny(text, "приемн", "приёмн", "воспитан", "foster"))
            return FamilyRelationKind.Foster;
        if (ContainsAny(text, "опек", "попечит", "guardian", "guard"))
            return FamilyRelationKind.Guardian;
        if (ContainsAny(text, "партнер", "партнёр", "супруг", "брак", "partner", "spouse", "marri", "wife", "husband"))
            return FamilyRelationKind.Partner;
        if (ContainsAny(text, "порожд", "порожда", "созда", "spawn", "origin", "created"))
            return FamilyRelationKind.Origin;
        if (ContainsAny(text, "родител", "отец", "мать", "parent", "father", "mother", "биолог", "biological"))
            return FamilyRelationKind.Biological;

        return FamilyRelationKind.Unknown;
    }

    /// <summary>Тип родительской связи из GEDCOM PEDI.</summary>
    public static FamilyRelationKind FromPedigree(string? pedigree)
    {
        var value = (pedigree ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "adopted" or "adopt" or "adoption" => FamilyRelationKind.Adoptive,
            "foster" => FamilyRelationKind.Foster,
            "guardian" => FamilyRelationKind.Guardian,
            "godparent" => FamilyRelationKind.Godparent,
            "named" => FamilyRelationKind.Named,
            "birth" or "natural" or "biological" or "" => FamilyRelationKind.Biological,
            _ => FamilyRelationKind.Unknown,
        };
    }

    /// <summary>Подпись ребра с типом, если он не биологический.</summary>
    public static string DisplayLabel(string label, FamilyRelationKind kind)
    {
        if (label.Length == 0) label = Label(kind);
        if (kind is FamilyRelationKind.Biological or FamilyRelationKind.Unknown)
            return label;

        var shortLabel = ShortLabel(kind).TrimEnd('.', ' ');
        if (Normalize(label).Contains(Normalize(shortLabel), StringComparison.Ordinal))
            return label;
        return $"{label} · {ShortLabel(kind)}";
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return text.ToLowerInvariant().Replace('ё', 'е').Trim();
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.Ordinal));
}

/// <summary>Единая шкала уверенности, подписи и разбор значений GEDCOM QUAY.</summary>
public static class FamilyConfidenceScale
{
    public static IReadOnlyList<ConfidenceChoice> Choices { get; } =
    [
        new(FamilyConfidence.Unknown, "Не указана"),
        new(FamilyConfidence.Proven, "Достоверно"),
        new(FamilyConfidence.Likely, "Вероятно"),
        new(FamilyConfidence.Hypothesis, "Гипотеза"),
        new(FamilyConfidence.Disputed, "Противоречиво"),
    ];

    public static string Label(FamilyConfidence level) => level switch
    {
        FamilyConfidence.Proven => "достоверно",
        FamilyConfidence.Likely => "вероятно",
        FamilyConfidence.Hypothesis => "гипотеза",
        FamilyConfidence.Disputed => "противоречиво",
        _ => "не указана",
    };

    public static string ShortLabel(FamilyConfidence level) => level switch
    {
        FamilyConfidence.Proven => "дост.",
        FamilyConfidence.Likely => "вер.",
        FamilyConfidence.Hypothesis => "гип.",
        FamilyConfidence.Disputed => "спор",
        _ => "",
    };

    public static string Id(FamilyConfidence level) => level switch
    {
        FamilyConfidence.Proven => "proven",
        FamilyConfidence.Likely => "likely",
        FamilyConfidence.Hypothesis => "hypothesis",
        FamilyConfidence.Disputed => "disputed",
        _ => "unknown",
    };

    public static FamilyConfidence Parse(string? id)
    {
        var value = (id ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "proven" or "достоверно" => FamilyConfidence.Proven,
            "likely" or "вероятно" => FamilyConfidence.Likely,
            "hypothesis" or "гипотеза" => FamilyConfidence.Hypothesis,
            "disputed" or "противоречиво" => FamilyConfidence.Disputed,
            _ => FamilyConfidence.Unknown,
        };
    }

    /// <summary>Берёт наиболее проблемный статус из нескольких утверждений.</summary>
    public static FamilyConfidence Combine(IEnumerable<FamilyConfidence> levels)
    {
        var result = FamilyConfidence.Unknown;
        foreach (var level in levels)
            if ((int)level > (int)result)
                result = level;
        return result;
    }

    /// <summary>GEDCOM QUAY: 0 — сомнительно, 1 — вопрос, 2 — косвенно, 3 — прямо.</summary>
    public static FamilyConfidence FromGedcomQuality(string? quality)
    {
        return (quality ?? "").Trim() switch
        {
            "0" => FamilyConfidence.Disputed,
            "1" => FamilyConfidence.Hypothesis,
            "2" => FamilyConfidence.Likely,
            "3" => FamilyConfidence.Proven,
            _ => FamilyConfidence.Unknown,
        };
    }
}
