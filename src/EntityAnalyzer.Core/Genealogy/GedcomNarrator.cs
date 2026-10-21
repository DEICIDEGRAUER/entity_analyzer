using System.Text;

namespace EntityAnalyzer.Core.Genealogy;

/// <summary>
/// Авточитаемое описание GEDCOM: сводка, «родовой обзор» с отступами по
/// поколениям, карточки людей (рождение/смерть/родители/браки/дети/заметки)
/// и замечания разбора. Текст строится без интерфейса — годится и для
/// предпросмотра, и для экспорта в Markdown/на печать.
/// </summary>
public static class GedcomNarrator
{
    /// <summary>Сводка: сколько людей, семей, датировок и полных пар.</summary>
    public static string Summary(GedcomDocument doc)
    {
        var withBirth = doc.Persons.Count(p => p.Birth is not null);
        var withDeath = doc.Persons.Count(p => p.Death is not null);
        var childRefs = ChildIds(doc);
        var roots = doc.Persons.Count(p => !childRefs.Contains(p.Id));
        var couples = doc.Families.Count(f => f.HusbandId is not null && f.WifeId is not null);
        var alternateNames = doc.Persons.Sum(p => p.AlternateNames.Count);
        var typedParentages = doc.Persons.Sum(p => p.ChildPedigree.Count(kv =>
            FamilyRelationKinds.FromPedigree(kv.Value) != FamilyRelationKind.Biological));
        return $"Людей: {doc.Persons.Count} · семей: {doc.Families.Count} · полных пар: {couples} · " +
               $"с датой рождения: {withBirth} · с датой смерти: {withDeath} · родоначальников: {roots}" +
               (alternateNames > 0 ? $" · альтернативных имён: {alternateNames}" : "") +
               (typedParentages > 0 ? $" · особых родительских связей: {typedParentages}" : "");
    }

    /// <summary>Одна строка: «Иван Смирнов (1900—1972)».</summary>
    public static string PersonTitle(GedcomPerson p)
    {
        var years = YearsLine(p);
        return years.Length == 0 ? p.DisplayName : $"{p.DisplayName} ({years})";
    }

    private static string YearsLine(GedcomPerson p)
    {
        var b = p.Birth?.Year;
        var d = p.Death?.Year;
        if (b is null && d is null) return "";
        if (b is not null && d is not null) return $"{b}—{d}";
        return b is not null ? $"{b}—" : $"† {d}";
    }

    /// <summary>Родовой обзор: родоначальники сверху, дети с отступом по поколениям.</summary>
    public static string Outline(GedcomDocument doc)
    {
        var sb = new StringBuilder();
        var childRefs = ChildIds(doc);
        var visitedPersons = new HashSet<string>(StringComparer.Ordinal);
        var visitedFamilies = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in doc.Persons.Where(p => !childRefs.Contains(p.Id)))
            PrintPerson(sb, doc, root, 0, visitedPersons, visitedFamilies);

        // Персоны, потерявшиеся из-за битых ссылок FAMC, печатаются последней волной.
        foreach (var p in doc.Persons)
            PrintPerson(sb, doc, p, 0, visitedPersons, visitedFamilies);

        return sb.ToString().TrimEnd();
    }

    private static void PrintPerson(StringBuilder sb, GedcomDocument doc, GedcomPerson person, int depth,
        HashSet<string> visitedPersons, HashSet<string> visitedFamilies)
    {
        if (!visitedPersons.Add(person.Id)) return;

        for (var i = 0; i < depth; i++) sb.Append("    ");
        sb.Append("• ").Append(PersonTitle(person));

        var families = FamiliesOf(doc, person).Where(f => visitedFamilies.Add(f.Id)).ToList();
        foreach (var family in families)
        {
            var spouse = doc.Person(family.HusbandId == person.Id ? family.WifeId : family.HusbandId);
            if (spouse is not null && !visitedPersons.Contains(spouse.Id))
                sb.Append("  ∞ ").Append(PersonTitle(spouse));
        }
        sb.AppendLine();

        foreach (var family in families)
        {
            var spouse = doc.Person(family.HusbandId == person.Id ? family.WifeId : family.HusbandId);
            if (spouse is not null && visitedPersons.Add(spouse.Id))
            {
                // Дети других браков супруга — то же поколение.
                foreach (var other in FamiliesOf(doc, spouse).Where(f => visitedFamilies.Add(f.Id)))
                    PrintChildren(sb, doc, other, depth + 1, visitedPersons, visitedFamilies);
            }
            PrintChildren(sb, doc, family, depth + 1, visitedPersons, visitedFamilies);
        }
    }

    private static void PrintChildren(StringBuilder sb, GedcomDocument doc, GedcomFamily family, int depth,
        HashSet<string> visitedPersons, HashSet<string> visitedFamilies)
    {
        foreach (var child in family.Children
                     .Select(doc.Person)
                     .Where(c => c is not null)
                     .OrderBy(c => c!.Birth?.Year ?? int.MaxValue)
                     .ThenBy(c => c!.Id, StringComparer.Ordinal))
        {
            PrintPerson(sb, doc, child!, depth, visitedPersons, visitedFamilies);
        }
    }

    private static IEnumerable<GedcomFamily> FamiliesOf(GedcomDocument doc, GedcomPerson person)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in person.SpouseOf)
        {
            var family = doc.Family(id);
            if (family is not null && seen.Add(id))
                yield return family;
        }
        foreach (var family in doc.Families)
        {
            if (seen.Contains(family.Id)) continue;
            if (family.HusbandId == person.Id || family.WifeId == person.Id)
                yield return family;
        }
    }

    /// <summary>Карточка человека: события, родители, браки, дети, заметка.</summary>
    public static string PersonCard(GedcomDocument doc, GedcomPerson p)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PersonTitle(p));
        if (p.AlternateNames.Count > 0)
            sb.AppendLine("  другие имена: " + string.Join("; ", p.AlternateNames));
        if (p.Birth is not null || p.BirthPlace.Length > 0)
            sb.AppendLine("  рождение: " + Join(p.Birth?.Display, p.BirthPlace));
        if (p.Death is not null || p.DeathPlace.Length > 0)
            sb.AppendLine("  смерть: " + Join(p.Death?.Display, p.DeathPlace));
        if (p.Occupation.Length > 0)
            sb.AppendLine("  занятие: " + p.Occupation);

        var parents = new List<string>();
        foreach (var famId in p.ChildOf)
        {
            var family = doc.Family(famId);
            if (family is null) continue;
            var kind = FamilyRelationKinds.FromPedigree(p.ChildPedigree.GetValueOrDefault(famId));
            var confidence = FamilyConfidenceScale.FromGedcomQuality(p.ChildConfidence.GetValueOrDefault(famId));
            var note = kind is FamilyRelationKind.Biological or FamilyRelationKind.Unknown
                ? ""
                : $" ({FamilyRelationKinds.Label(kind)})";
            if (confidence != FamilyConfidence.Unknown)
                note += $" [{FamilyConfidenceScale.Label(confidence)}]";
            foreach (var spouseId in family.SpouseIds())
            {
                var parent = doc.Person(spouseId);
                if (parent is not null) parents.Add(parent.DisplayName + note);
            }
        }
        if (parents.Count > 0) sb.AppendLine("  родители: " + string.Join(", ", parents));

        foreach (var family in FamiliesOf(doc, p))
        {
            var spouseId = family.HusbandId == p.Id ? family.WifeId : family.HusbandId;
            var spouse = doc.Person(spouseId);
            var line = "  брак: " + (spouse?.DisplayName ?? "неизвестный супруг");
            if (family.Marriage is not null || family.MarriagePlace.Length > 0)
                line += " (" + Join(family.Marriage?.Display, family.MarriagePlace) + ")";
            if (family.Divorce is not null && !family.Divorce.IsEmpty)
                line += "; развод: " + family.Divorce.Display;
            sb.AppendLine(line);

            var children = family.Children.Select(doc.Person).Where(c => c is not null).ToList();
            if (children.Count > 0)
                sb.AppendLine("  дети: " + string.Join(", ", children.Select(c => c!.DisplayName)));
        }

        if (p.Note.Length > 0)
            sb.AppendLine("  примечание: " + p.Note.Replace("\n", " / "));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Полный читаемый текст документа: сводка, обзор, карточки, замечания.</summary>
    public static string ToReadableText(GedcomDocument doc, bool includePersonCards = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("СЕМЕЙНОЕ ДРЕВО — ЧИТАЕМОЕ ОПИСАНИЕ ФАЙЛА GEDCOM");
        sb.AppendLine(Summary(doc));
        sb.AppendLine();
        sb.AppendLine("РОДОВОЙ ОБЗОР");
        sb.AppendLine(Outline(doc));

        if (includePersonCards)
        {
            sb.AppendLine();
            sb.AppendLine("КАРТОЧКИ");
            foreach (var p in doc.Persons)
            {
                sb.AppendLine();
                sb.AppendLine(PersonCard(doc, p));
            }
        }

        if (doc.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("ЗАМЕЧАНИЯ РАЗБОРА");
            foreach (var w in doc.Warnings)
                sb.AppendLine("! " + w);
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string Join(string? a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b;
        return b.Length == 0 ? a : a + ", " + b;
    }

    private static HashSet<string> ChildIds(GedcomDocument doc)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in doc.Families)
            foreach (var child in family.Children)
                set.Add(child);
        return set;
    }
}
