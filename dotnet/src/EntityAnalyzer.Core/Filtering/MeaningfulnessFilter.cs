namespace EntityAnalyzer.Core.Filtering;

/// <summary>
/// «Осмысленность»: проза насыщена служебными словами (предлоги, союзы,
/// местоимения, вспомогательные глаголы), перечисления имён — почти свободны
/// от них. Контекст с долей служебных слов ниже порога — это список.
/// Короткие контексты не проверяются (слишком шумно).
/// </summary>
public sealed class MeaningfulnessFilter
{
    private readonly double _minFunctionWordRatio;
    private readonly int _minWords;

    public MeaningfulnessFilter(double minFunctionWordRatio = 0.06, int minWords = 6)
    {
        _minFunctionWordRatio = minFunctionWordRatio;
        _minWords = minWords;
    }

    public bool IsMeaningless(string context)
    {
        if (string.IsNullOrWhiteSpace(context)) return false;

        // QA N05: реплики с тире («— Стой! — сказал Наг.») — проза, а не перечень.
        if (LooksLikeDialogue(context)) return false;

        var words = Tokenize(context);
        if (words.Count < _minWords) return false;

        var function = 0;
        foreach (var w in words)
            if (FunctionWords.Contains(w)) function++;

        var ratio = (double)function / words.Count;
        return ratio < _minFunctionWordRatio;
    }

    /// <summary>
    /// Диалоговый контекст: тире-реплики или глаголы речи. Такой текст
    /// считается осмысленным независимо от доли служебных слов.
    /// </summary>
    internal static bool LooksLikeDialogue(string context)
    {
        // Тире-реплика: тире в начале строки или после пробела, за ним текст.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                context, @"(^|[\n\r\s])[—–─]\s+\S",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return true;

        foreach (var v in SpeechVerbs)
            if (context.Contains(v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Глаголы речи и атрибуции диалога (рус/англ, нижний регистр).</summary>
    internal static readonly HashSet<string> SpeechVerbs = new(StringComparer.Ordinal)
    {
        "сказал", "сказала", "сказали", "говорил", "говорила", "говорят",
        "спросил", "спросила", "ответил", "ответила", "крикнул", "крикнула",
        "прошептал", "прошептала", "промолвил", "промолвила", "воскликнул",
        "отметил", "заметил", "добавил", "начал", "кончил",
        "said", "says", "asked", "replied", "shouted", "whispered", "cried",
    };

    private static List<string> Tokenize(string s)
    {
        var list = new List<string>(32);
        var start = -1;
        for (var i = 0; i <= s.Length; i++)
        {
            var letter = i < s.Length && char.IsLetter(s[i]);
            if (letter && start < 0) start = i;
            else if (!letter && start >= 0) { list.Add(s[start..i]); start = -1; }
        }
        return list;
    }

    /// <summary>Служебные слова русского и английского (нижний регистр).</summary>
    internal static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "и","в","во","не","на","я","он","с","со","а","то","как","что","по","это","она",
        "этот","к","до","у","вы","за","бы","ее","его","от","о","из","им","там","для","уже","если",
        "или","ни","был","была","были","было","при","том","чтобы","же","все","тот","эта","так",
        "её","их","но","да","ну","ли","ведь","вот","под","над","об","без","через","между",
        "когда","там","себя","стал","стала","ещё","еще","даже","вдруг","тут","здесь","туда",
        "the","of","and","in","to","was","is","he","she","it","they","that","with","as","at",
        "by","for","on","from","but","or","not","his","her","its","their","be","been","were",
        "had","has","have","which","who","whom","this","these","those","there","then","than",
        "so","if","when","an","a","are","am","do","does","did","will","would","could","shall",
        "should","may","might","must","can","upon","into","over","under","about","after","before",
    };
}
