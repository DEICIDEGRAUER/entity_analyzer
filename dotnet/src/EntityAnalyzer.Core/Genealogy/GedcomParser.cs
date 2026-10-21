using System.Text;
using System.Text.RegularExpressions;
using EntityAnalyzer.Core.IO;

namespace EntityAnalyzer.Core.Genealogy;

/// <summary>
/// Терпимый разбор GEDCOM 5.5: записи INDI/FAM, имена со слэшами, события
/// (рождение/смерть/погребение/брак/развод), ссылки FAMC/FAMS/HUSB/WIFE/CHIL,
/// склейка строк CONC/CONT. Неизвестные теги молча пропускаются, битые ссылки
/// попадают в предупреждения. Кодировка файла — через DocumentReader.
/// </summary>
public static partial class GedcomParser
{
    [GeneratedRegex(@"^\s*(?<level>\d+)\s+(?:(?<xref>@[^@]+@)\s+)?(?<tag>[A-Za-z0-9_]+)(?:\s(?<value>.*))?$")]
    private static partial Regex Line();

    [GeneratedRegex(@"(?m)^0\s+(?:@[^@]+\@\s+)?(?:INDI|FAM|HEAD)\b")]
    private static partial Regex Signature();

    private enum EventKind { None, Birth, Death, Burial, Marriage, Divorce }

    /// <summary>Быстрая проверка, что текст похож на GEDCOM (индикатор для превью).</summary>
    public static bool LooksLikeGedcom(string text) =>
        !string.IsNullOrEmpty(text) && Signature().IsMatch(text);

    /// <summary>Прочитать файл (декодирование с фолбэком UTF-8 → CP1251) и разобрать.</summary>
    public static GedcomDocument ParseFile(string path) =>
        Parse(DocumentReader.DecodeText(File.ReadAllBytes(path)));

    public static GedcomDocument Parse(string text)
    {
        var doc = new GedcomDocument();
        var personIds = new HashSet<string>(StringComparer.Ordinal);
        var familyIds = new HashSet<string>(StringComparer.Ordinal);
        GedcomPerson? person = null;
        GedcomFamily? family = null;
        var eventKind = EventKind.None;
        Action<string>? stringSink = null;
        var stringValue = "";
        Action<GedcomDate?>? dateSink = null;
        var dateRaw = "";
        string? childFamilyId = null;
        var nameIsPrimary = true;

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var m = Line().Match(rawLine);
            if (!m.Success)
                continue;

            var tag = m.Groups["tag"].Value.ToUpperInvariant();
            var value = m.Groups["value"].Success ? m.Groups["value"].Value.Trim() : "";
            var xref = m.Groups["xref"].Success ? Clean(m.Groups["xref"].Value) : null;

            // Склейка длинных строк: CONC — без разделителя, CONT — с переводом строки.
            if (tag is "CONC" or "CONT")
            {
                if (dateSink is not null)
                {
                    dateRaw = tag == "CONC" ? dateRaw + value : dateRaw + " " + value;
                    dateSink(GedcomDateParser.Parse(dateRaw));
                }
                else if (stringSink is not null)
                {
                    stringValue = tag == "CONC" ? stringValue + value : stringValue + "\n" + value;
                    stringSink(stringValue);
                }
                continue;
            }

            stringSink = null;
            stringValue = "";
            dateSink = null;
            dateRaw = "";

            if (m.Groups["level"].Value == "0")
            {
                person = null;
                family = null;
                eventKind = EventKind.None;
                childFamilyId = null;
                nameIsPrimary = true;
                if (xref is null)
                    continue;
                if (tag == "INDI")
                {
                    // Повторный xref в битом/склеенном файле не должен ронять индексы.
                    if (!personIds.Add(xref))
                    {
                        doc.Warnings.Add($"запись человека @{xref}@ встречается больше одного раза; повторная пропущена");
                        continue;
                    }
                    person = new GedcomPerson { Id = xref };
                    doc.Persons.Add(person);
                }
                else if (tag == "FAM")
                {
                    if (!familyIds.Add(xref))
                    {
                        doc.Warnings.Add($"запись семьи @{xref}@ встречается больше одного раза; повторная пропущена");
                        continue;
                    }
                    family = new GedcomFamily { Id = xref };
                    doc.Families.Add(family);
                }
                continue;
            }

            if (person is not null)
            {
                if (tag is not ("FAMC" or "PEDI" or "QUAY"))
                    childFamilyId = null;

                switch (tag)
                {
                    case "NAME":
                        if (person.Name.Length == 0)
                        {
                            nameIsPrimary = true;
                            stringValue = value;
                            stringSink = v => ApplyName(person, v);
                            ApplyName(person, value);
                        }
                        else
                        {
                            nameIsPrimary = false;
                            var alternate = FormatName(value);
                            if (alternate.Length > 0 &&
                                !string.Equals(alternate, person.Name, StringComparison.OrdinalIgnoreCase) &&
                                !person.AlternateNames.Contains(alternate, StringComparer.OrdinalIgnoreCase))
                                person.AlternateNames.Add(alternate);
                        }
                        break;
                    case "GIVN":
                        if (!nameIsPrimary) break;
                        person.Given = value;
                        person.Name = (person.Given + " " + person.Surname).Trim();
                        break;
                    case "SURN":
                        if (!nameIsPrimary) break;
                        person.Surname = value;
                        person.Name = (person.Given + " " + person.Surname).Trim();
                        break;
                    case "SEX":
                        person.Sex = value.Length > 0 ? char.ToUpperInvariant(value[0]).ToString() : "U";
                        break;
                    case "OCCU":
                    case "TITL":
                        stringValue = value;
                        stringSink = v => person.Occupation = v;
                        person.Occupation = value;
                        break;
                    case "NOTE":
                    case "TEXT":
                        stringValue = value;
                        stringSink = v => person.Note = v;
                        person.Note = value;
                        break;
                    case "BIRT":
                        eventKind = EventKind.Birth;
                        break;
                    case "DEAT":
                        eventKind = EventKind.Death;
                        break;
                    case "BURI":
                        eventKind = EventKind.Burial;
                        break;
                    case "FAMC":
                        if (value.Length > 0)
                        {
                            var familyId = Clean(value);
                            if (!person.ChildOf.Contains(familyId, StringComparer.Ordinal))
                                person.ChildOf.Add(familyId);
                            childFamilyId = familyId;
                        }
                        break;
                    case "PEDI":
                        if (childFamilyId is not null && value.Length > 0)
                            person.ChildPedigree[childFamilyId] = value;
                        break;
                    case "QUAY":
                        if (childFamilyId is not null && value.Length > 0)
                            person.ChildConfidence[childFamilyId] = value;
                        break;
                    case "FAMS":
                        if (value.Length > 0) person.SpouseOf.Add(Clean(value));
                        break;
                    case "DATE":
                        dateRaw = value;
                        dateSink = d => AssignEventDate(person, eventKind, d);
                        dateSink(GedcomDateParser.Parse(value));
                        break;
                    case "PLAC":
                        stringValue = value;
                        stringSink = v => AssignEventPlace(person, eventKind, v);
                        AssignEventPlace(person, eventKind, value);
                        break;
                    default:
                        break;
                }
                continue;
            }

            if (family is not null)
            {
                switch (tag)
                {
                    case "HUSB":
                        family.HusbandId = Clean(value);
                        break;
                    case "WIFE":
                        family.WifeId = Clean(value);
                        break;
                    case "CHIL":
                        if (value.Length > 0) family.Children.Add(Clean(value));
                        break;
                    case "MARR":
                        eventKind = EventKind.Marriage;
                        break;
                    case "DIV":
                        eventKind = EventKind.Divorce;
                        break;
                    case "DATE":
                        dateRaw = value;
                        dateSink = d => AssignFamilyDate(family, eventKind, d);
                        dateSink(GedcomDateParser.Parse(value));
                        break;
                    case "PLAC":
                        if (eventKind == EventKind.Marriage) family.MarriagePlace = value;
                        break;
                    default:
                        break;
                }
            }
        }

        BuildWarnings(doc);
        return doc;
    }

    /// <summary>«@F1@» → «F1».</summary>
    private static string Clean(string xref) => xref.Trim().Trim('@');

    private static void ApplyName(GedcomPerson person, string value)
    {
        var text = value.Trim();
        if (text.Contains('/'))
        {
            var first = text.IndexOf('/');
            var second = text.IndexOf('/', first + 1);
            var given = text[..first].Trim();
            var surname = second > first ? text[(first + 1)..second].Trim() : text[(first + 1)..].Trim();
            if (surname.Length > 0) person.Surname = surname;
            if (given.Length > 0) person.Given = given;
            person.Name = (person.Given + " " + person.Surname).Trim();
            if (person.Name.Length == 0)
                person.Name = text.Replace("/", " ").Trim();
        }
        else
        {
            if (person.Given.Length == 0 && person.Surname.Length == 0)
                person.Name = text;
        }
    }

    /// <summary>Приводит NAME к читаемому виду, не меняя основное имя персоны.</summary>
    private static string FormatName(string value)
    {
        var text = value.Trim();
        if (!text.Contains('/')) return text;
        var first = text.IndexOf('/');
        var second = text.IndexOf('/', first + 1);
        var given = text[..first].Trim();
        var surname = second > first ? text[(first + 1)..second].Trim() : text[(first + 1)..].Trim();
        var formatted = (given + " " + surname).Trim();
        return formatted.Length > 0 ? formatted : text.Replace("/", " ").Trim();
    }

    private static void AssignEventDate(GedcomPerson person, EventKind kind, GedcomDate? date)
    {
        switch (kind)
        {
            case EventKind.Birth:
                person.Birth = date;
                break;
            case EventKind.Death:
            case EventKind.Burial:
                person.Death ??= date;
                break;
            default:
                break;
        }
    }

    private static void AssignEventPlace(GedcomPerson person, EventKind kind, string place)
    {
        switch (kind)
        {
            case EventKind.Birth:
                person.BirthPlace = place;
                break;
            case EventKind.Death:
            case EventKind.Burial:
                if (person.DeathPlace.Length == 0) person.DeathPlace = place;
                break;
            default:
                break;
        }
    }

    private static void AssignFamilyDate(GedcomFamily family, EventKind kind, GedcomDate? date)
    {
        if (kind == EventKind.Marriage) family.Marriage = date;
        else if (kind == EventKind.Divorce) family.Divorce = date;
    }

    private static void BuildWarnings(GedcomDocument doc)
    {
        var personIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in doc.Persons)
        {
            if (!personIds.Add(p.Id))
                doc.Warnings.Add($"запись человека @{p.Id}@ встречается больше одного раза");
        }
        var familyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in doc.Families)
        {
            if (!familyIds.Add(f.Id))
                doc.Warnings.Add($"запись семьи @{f.Id}@ встречается больше одного раза");
        }

        foreach (var p in doc.Persons)
        {
            foreach (var fam in p.ChildOf)
                if (!familyIds.Contains(fam))
                    doc.Warnings.Add($"у @{p.Id}@ ссылка на несуществующую семью @{fam}@ (FAMC)");
            foreach (var fam in p.SpouseOf)
                if (!familyIds.Contains(fam))
                    doc.Warnings.Add($"у @{p.Id}@ ссылка на несуществующую семью @{fam}@ (FAMS)");
        }
        foreach (var f in doc.Families)
        {
            foreach (var spouse in f.SpouseIds())
                if (!personIds.Contains(spouse))
                    doc.Warnings.Add($"в семье @{f.Id}@ неизвестный супруг @{spouse}@");
            foreach (var child in f.Children)
                if (!personIds.Contains(child))
                    doc.Warnings.Add($"в семье @{f.Id}@ неизвестный ребёнок @{child}@");
        }
    }
}

/// <summary>
/// Разбор дат GEDCOM в русское отображение: «12 JAN 1900» → «12 января 1900»,
/// «ABT 1900» → «ок. 1900», «BEF 1912» → «до 1912», «BET 1900 AND 1910» →
/// «между 1900 и 1910». Понимает английские и русские месяцы, даты «12.01.1900»
/// и пометки «BC» / «до н. э.». Непонятное значение сохраняется как есть.
/// </summary>
public static partial class GedcomDateParser
{
    private static readonly string[] MonthsRu =
    [
        "", "января", "февраля", "марта", "апреля", "мая", "июня",
        "июля", "августа", "сентября", "октября", "ноября", "декабря",
    ];

    private static readonly Dictionary<string, int> MonthsEn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JAN"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["APR"] = 4, ["MAY"] = 5, ["JUN"] = 6,
        ["JUL"] = 7, ["AUG"] = 8, ["SEP"] = 9, ["OCT"] = 10, ["NOV"] = 11, ["DEC"] = 12,
    };

    private static readonly string[] MonthsRuNom =
    [
        "", "январь", "февраль", "март", "апрель", "май", "июнь",
        "июль", "август", "сентябрь", "октябрь", "ноябрь", "декабрь",
    ];


    /// <summary>Разобрать строку даты; null — пустая строка.</summary>
    public static GedcomDate? Parse(string? raw)
    {
        var text = (raw ?? "").Trim().Trim('"');
        if (text.Length == 0) return null;

        var upper = text.ToUpperInvariant();
        var eraNegative = upper.Contains("BC") || upper.Contains("BCE") ||
                          upper.Contains("ДО Н. Э") || upper.Contains("ДО НАШЕЙ ЭР");

        if (upper.StartsWith("BET ", StringComparison.Ordinal) && upper.Contains(" AND ", StringComparison.Ordinal))
        {
            var body = text[4..];
            var pos = body.ToUpperInvariant().IndexOf(" AND ", StringComparison.Ordinal);
            var a = Parse(body[..pos]);
            var b = Parse(body[(pos + 5)..]);
            var display = a is not null && b is not null
                ? $"между {a.Display} и {b.Display}"
                : text;
            return new GedcomDate { Raw = text, Year = a?.Year, IsAbout = true, Qualifier = "BET", Display = display };
        }

        if (upper.StartsWith("FROM ", StringComparison.Ordinal) && upper.Contains(" TO ", StringComparison.Ordinal))
        {
            var body = text[5..];
            var pos = body.ToUpperInvariant().IndexOf(" TO ", StringComparison.Ordinal);
            var a = Parse(body[..pos]);
            var b = Parse(body[(pos + 4)..]);
            var display = a is not null && b is not null ? $"{a.Display} — {b.Display}" : text;
            return new GedcomDate { Raw = text, Year = a?.Year, IsAbout = true, Qualifier = "FROM", Display = display };
        }

        foreach (var (prefix, qualifier, about) in new[]
                 {
                     ("BEF ", "BEF", false), ("AFT ", "AFT", false),
                     ("ABT ", "ABT", true), ("EST ", "EST", true), ("CAL ", "CAL", true),
                     ("CIRCA ", "ABT", true), ("ОК. ", "ABT", true), ("ОКОЛО ", "ABT", true), ("ОК ", "ABT", true),
                 })
        {
            if (!upper.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var inner = Parse(text[prefix.Length..]);
            if (inner is null) break;
            var display = qualifier switch
            {
                "BEF" => $"до {inner.Display}",
                "AFT" => $"после {inner.Display}",
                _ => $"ок. {inner.Display}",
            };
            return new GedcomDate
            {
                Raw = text,
                Year = inner.Year,
                Month = inner.Month,
                Day = inner.Day,
                IsAbout = about,
                Qualifier = qualifier,
                Display = display,
            };
        }

        var tokens = text.Split([' ', '.', '/', ',', ';', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        int? year = null, month = null, day = null;
        foreach (var token in tokens)
        {
            var clean = token.TrimEnd('г', 'г', '.', 'т', 'ч', 'н', 'э', 'м', '—', '-');
            if (clean.Length == 0) continue;

            if (char.IsDigit(clean[0]))
            {
                var digits = new string([.. clean.TakeWhile(char.IsDigit)]);
                if (digits.Length is >= 3 and <= 4 && int.TryParse(digits, out var parsedYear))
                {
                    year ??= eraNegative ? -parsedYear : parsedYear;
                    continue;
                }
                if (digits.Length is >= 1 and <= 2 && int.TryParse(digits, out var small))
                {
                    if (eraNegative && year is null && (small > 12 || (day is null && month is null)))
                    {
                        year = -small;
                        continue;
                    }
                    if (month is null && small is >= 1 and <= 12 && day is not null)
                        month = small;
                    else if (day is null && small is >= 1 and <= 31)
                        day = small;
                    else if (month is null && small is >= 1 and <= 12)
                        month = small;
                    continue;
                }
                continue;
            }

            var key = clean.TrimEnd('.').ToUpperInvariant();
            if (key.Length >= 3)
            {
                if (MonthsEn.TryGetValue(key[..3], out var enMonth))
                {
                    month ??= enMonth;
                    continue;
                }
            }
            if (clean.Length >= 3 && MonthByNameRu(clean) is { } ruMonth)
                month ??= ruMonth;
        }

        // «12.01.1900» — порядок «день.месяц.год»; если месяц не найден, а день и год есть — считаем второй токен месяцем.
        if (month is null && tokens.Count(u => u.Any(char.IsDigit)) == 3)
        {
            var nums = tokens.Where(u => u.Any(char.IsDigit))
                .Select(u => new string([.. u.TakeWhile(char.IsDigit)]))
                .Where(u => u.Length > 0)
                .Select(int.Parse)
                .ToList();
            if (nums.Count == 3)
            {
                year = eraNegative ? -nums[2] : nums[2];
                if (nums[1] is >= 1 and <= 12) { day = nums[0]; month = nums[1]; }
                else { day = nums[1]; month = nums[0]; }
            }
        }

        if (year is null && month is null && day is null)
        {
            // Голый текст без цифр: «ЛЕТО 1890-х» не распознан — сохраняем как есть.
            return new GedcomDate { Raw = text, Display = text };
        }

        var display2 = BuildDisplay(day, month, year, eraNegative);
        return new GedcomDate
        {
            Raw = text,
            Year = year,
            Month = month,
            Day = day,
            IsAbout = false,
            Display = display2,
        };
    }

    private static int? MonthByNameRu(string token)
    {
        var t = token.TrimEnd('.').ToLowerInvariant().Replace('ё', 'е');
        if (t.Length < 3) return null;
        var stem = t[..3];
        return stem switch
        {
            "янв" => 1,
            "фев" => 2,
            "мар" => 3,
            "апр" => 4,
            "май" or "мая" => 5,
            "июн" => 6,
            "июл" => 7,
            "авг" => 8,
            "сен" => 9,
            "окт" => 10,
            "ноя" => 11,
            "дек" => 12,
            _ => null,
        };
    }

    private static string BuildDisplay(int? day, int? month, int? year, bool eraNegative)
    {
        var sb = new StringBuilder();
        if (day is { } d && month is { } m) sb.Append($"{d} {MonthsRu[m]}");
        else if (month is { } m2) sb.Append(MonthsRuNom[m2]);
        if (year is { } y)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Math.Abs(y));
            if (eraNegative || y < 0) sb.Append(" до н. э.");
        }
        return sb.Length == 0 ? "" : sb.ToString();
    }
}
