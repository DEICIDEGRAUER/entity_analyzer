using System.Text;

namespace EntityAnalyzer.Core.Variants;

/// <summary>
/// Генератор русских падежных форм имён: согласная основа, -й, -а/-я, -ь,
/// дефисные составные имена (склоняется последняя часть), множественное число.
/// Латиница не склоняется. Детерминированно, без исключений.
/// </summary>
public static class RussianMorphology
{
    private const string Vowels = "аеёиоуыэюя";
    private const string Hissing = "жшчщ"; // после шипящих творительный -ем/-ей

    public static IEnumerable<string> Decline(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;
        if (!IsRussian(name)) yield break;

        foreach (var form in DeclineWord(name))
            if (form.Length >= 3 && !form.Equals(name, StringComparison.Ordinal))
                yield return form;
    }

    private static bool IsRussian(string s)
    {
        var hasCyrillic = false;
        foreach (var c in s)
        {
            if (c is >= 'А' and <= 'я' or 'Ё' or 'ё') hasCyrillic = true;
            else if (char.IsLetter(c) && c != '-' && c != '\'' && c != ' ') return false; // латиница → нет
        }
        return hasCyrillic;
    }

    private static IEnumerable<string> DeclineWord(string name)
    {
        // Дефисное имя: склоняем только последнюю часть («Шуб-Ниггурат» → «Шуб-Ниггурата»).
        var hyphen = name.LastIndexOf('-');
        if (hyphen > 0 && hyphen < name.Length - 1)
        {
            var head = name[..(hyphen + 1)];
            var tail = name[(hyphen + 1)..];
            foreach (var t in DeclineSimple(tail))
                yield return head + t;
            yield break;
        }

        foreach (var form in DeclineSimple(name))
            yield return form;
    }

    private static IEnumerable<string> DeclineSimple(string w)
    {
        if (w.Length == 0) yield break;
        var last = char.ToLowerInvariant(w[^1]);
        var stem = w[..^1];

        switch (last)
        {
            case 'а':
                yield return stem + "ы"; yield return stem + "е"; yield return stem + "у";
                yield return stem + "ой"; yield return stem + "е";
                yield return stem + "ами"; yield return stem + "ах";
                break;

            case 'я':
                yield return stem + "и"; yield return stem + "е"; yield return stem + "ю";
                yield return stem + "ей"; yield return stem + "ями"; yield return stem + "ях";
                break;

            case 'ь':
                yield return stem + "я"; yield return stem + "ю"; yield return stem + "ем";
                yield return stem + "е"; yield return stem + "и"; yield return stem + "ей";
                break;

            case 'й':
                yield return stem + "я"; yield return stem + "ю"; yield return stem + "ем";
                yield return stem + "е"; yield return stem + "и"; yield return stem + "ев";
                break;

            case 'о':
            case 'е':
                // Средний род на -о/-е обычно не склоняется — пропускаем.
                break;

            default:
                if (!Vowels.Contains(last) && last != 'ъ' && last != 'ы')
                {
                    // Твёрдая согласная основа: Наг → Нага, Нагу, Нагом…
                    yield return w + "а";
                    yield return w + "у";
                    yield return w + (Hissing.Contains(last) ? "ем" : "ом");
                    yield return w + "е";
                    yield return w + "и";
                    yield return w + "ов";
                    yield return w + "ам";
                    yield return w + "ами";
                    yield return w + "ах";
                }
                break;
        }
    }

    /// <summary>Все формы одного слова через пробел для составных канонов («Наг и Йеб»).</summary>
    public static IEnumerable<string> DeclineCompound(string canon)
    {
        if (string.IsNullOrWhiteSpace(canon)) yield break;
        var parts = canon.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { foreach (var f in Decline(canon)) yield return f; yield break; }

        // Склоняем каждое значимое слово независимо: «Наг», «Йеб» из «Наг и Йеб».
        foreach (var part in parts)
        {
            if (part.Length < 3 || IsConjunction(part)) continue;
            foreach (var f in Decline(part))
                yield return f;
        }
    }

    private static bool IsConjunction(string w) =>
        w.Equals("и", StringComparison.OrdinalIgnoreCase) ||
        w.Equals("и", StringComparison.OrdinalIgnoreCase);
}
