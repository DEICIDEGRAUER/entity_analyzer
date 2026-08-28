using System.Text;

namespace EntityAnalyzer.Core.Redaction;

/// <summary>Запись карты соответствий: плейсхолдер → оригинал.</summary>
public sealed record RedactionMapEntry(string Placeholder, string Original);

/// <summary>
/// R2-22 «Режим редакции»: обезличивание текста — найденные упоминания
/// заменяются плейсхолдерами [ENTITY_N]. Одна сущность = один плейсхолдер
/// на весь документ (обратная деанонимизация по карте, которая хранится
/// отдельно от редактируемой копии). Пересекающиеся диапазоны: первый
/// побеждает. Выход за границы текста игнорируется безопасно.
/// </summary>
public static class Redactor
{
    public static (string Redacted, IReadOnlyList<RedactionMapEntry> Map) Apply(
        string text, IEnumerable<(int Start, int Length, string Entity)> spans)
    {
        // Сортировка по позиции; пересечения отсекаем жадно.
        var ordered = spans
            .Where(s => s.Start >= 0 && s.Length > 0 && s.Start + s.Length <= text.Length)
            .OrderBy(s => s.Start)
            .ToList();

        var placeholderByEntity = new Dictionary<string, string>(StringComparer.Ordinal);
        var map = new List<RedactionMapEntry>();
        var sb = new StringBuilder(text.Length);
        var pos = 0;

        foreach (var (start, length, entity) in ordered)
        {
            if (start < pos) continue; // пересечение с уже принятым диапазоном

            if (!placeholderByEntity.TryGetValue(entity, out var placeholder))
            {
                placeholder = $"[ENTITY_{placeholderByEntity.Count + 1}]";
                placeholderByEntity[entity] = placeholder;
                map.Add(new RedactionMapEntry(placeholder, entity));
            }

            sb.Append(text, pos, start - pos);
            sb.Append(placeholder);
            pos = start + length;
        }

        sb.Append(text, pos, text.Length - pos);
        return (sb.ToString(), map);
    }
}
