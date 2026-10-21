using System.IO;
using System.Windows.Media;
using EntityAnalyzer.Core.Codex;

namespace EntityAnalyzer.App.Services;

/// <summary>
/// Цветовое кодирование сущностей по пяти семантическим ветвям.
/// Порядок разрешения: точное совпадение имени/алиаса карточки → её тип;
/// ключевые слова типа/группы; детерминированный хеш для пользовательских
/// категорий (стабильный цвет внутри сеанса). Возвращает кисть из палитры.
/// </summary>
public static class BranchResolver
{
    private static readonly Dictionary<string, string> EntityTypeByName =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Индексировать кодекс: имя/алиас → имя категории.</summary>
    public static void Initialize(CodexDocument doc)
    {
        EntityTypeByName.Clear();
        foreach (var card in doc.Cards)
        {
            var typeName = doc.Types.FirstOrDefault(t => t.Id == card.TypeId)?.Name ?? card.TypeId;
            if (!EntityTypeByName.TryAdd(card.Name, typeName))
                EntityTypeByName[card.Name] = typeName;
            EntityTypeByName.TryAdd(card.Canonical, typeName);
            foreach (var alias in card.Aliases)
                EntityTypeByName.TryAdd(alias, typeName);
        }
    }

    /// <summary>Кисть ветви для типа/группы/имени сущности.</summary>
    public static Brush Resolve(object? value) => ByIndex(ResolveIndex(value));

    /// <summary>
    /// Индекс ветви 0..4 для фильтров графа (R2-7): категория карточки →
    /// ключевые слова → детерминированный хеш. Стабилен внутри сеанса.
    /// </summary>
    public static int ResolveIndex(object? value)
    {
        var key = value?.ToString()?.Trim() ?? "";
        if (key.Length == 0) return 0;

        // 1) Известная сущность — ветвь её категории.
        if (EntityTypeByName.TryGetValue(key, out var type))
            return KeywordsToIndex(type);

        // 2) Ключевые слова категории/группы.
        return KeywordsToIndex(key);
    }

    private static int KeywordsToIndex(string s)
    {
        if (Contains(s, "deity", "god", "божеств", "древн", "внешн", "пантеон")) return 0;
        if (Contains(s, "location", "city", "place", "локаци", "город", "мест", "стран")) return 1;
        if (Contains(s, "artifact", "item", "relic", "артефакт", "предмет", "реликв")) return 2;
        if (Contains(s, "person", "persona", "character", "персона", "личност", "персонаж", "человек")) return 3;
        if (Contains(s, "book", "cult", "tome", "text", "книг", "культ", "текст", "свиток")) return 4;

        // 3) Пользовательская категория без ключевых слов — стабильный
        //    детерминированный выбор из пяти ветвей.
        unchecked
        {
            var h = 17;
            foreach (var ch in s.ToLowerInvariant()) h = h * 31 + ch;
            return ((h % 5) + 5) % 5;
        }
    }

    private static Brush ByIndex(int index) => index switch
    {
        0 => Palette.Branch1,
        1 => Palette.Branch2,
        2 => Palette.Branch3,
        3 => Palette.Branch4,
        _ => Palette.Branch5,
    };

    private static bool Contains(string haystack, params string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Item 14: сбросить кэш кистей после смены темы или цветов ветвей.</summary>
    public static void InvalidatePalette() => Palette.Invalidate();

    /// <summary>Доступ к кистям палитры через ресурсы приложения (кэш до сброса темы).</summary>
    private static class Palette
    {
        private static readonly Brush[] Cache = new Brush[5];

        public static Brush Branch1 => Get(0);
        public static Brush Branch2 => Get(1);
        public static Brush Branch3 => Get(2);
        public static Brush Branch4 => Get(3);
        public static Brush Branch5 => Get(4);

        public static void Invalidate()
        {
            for (var i = 0; i < Cache.Length; i++)
                Cache[i] = null!;
        }

        private static Brush Get(int index) =>
            Cache[index] ??= Load("BrushBranch" + (index + 1));

        private static Brush Load(string key) =>
            System.Windows.Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x73, 0x73, 0x70));
    }
}
