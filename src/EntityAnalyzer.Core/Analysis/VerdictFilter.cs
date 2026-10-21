using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// R2-13: фильтр цитат по пользовательскому вердикту. Вынесен в ядро,
/// чтобы режимы «Непроверенные / Подтверждённые / Ложные / Спорные» не
/// зависели от WPF и проверялись без запуска окна.
/// </summary>
public static class VerdictFilter
{
    public const string All = "Все";
    public const string Unreviewed = "Непроверенные";
    public const string Confirmed = "Подтверждённые";
    public const string Rejected = "Ложные";
    public const string Uncertain = "Спорные";

    /// <summary>Режим ограничивает выборку именно вердиктом (не «Все» и не тип совпадения).</summary>
    public static bool IsVerdictMode(string mode) =>
        mode is Unreviewed or Confirmed or Rejected or Uncertain;

    /// <summary>
    /// Пропускает ли вердикт выбранный режим. Не-вердиктные режимы
    /// («Точные», «Fuzzy», «Тема…») не фильтруются здесь.
    /// </summary>
    public static bool Matches(VerdictKind? verdict, string mode) => mode switch
    {
        Unreviewed => verdict is null,
        Confirmed => verdict == VerdictKind.Confirmed,
        Rejected => verdict == VerdictKind.Rejected,
        Uncertain => verdict == VerdictKind.Uncertain,
        _ => true,
    };
}
