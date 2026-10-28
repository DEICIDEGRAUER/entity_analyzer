using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Core.Codex;

/// <summary>Bridges the codex to the analysis engine: cards → entity definitions.</summary>
public static class CodexSearchAdapter
{
    /// <summary>
    /// Converts enabled cards of every category into engine definitions.
    /// Aliases (incl. migrated titles/irregular forms) become Irregular;
    /// VariantFactory adds morphology on top when generateVariants is on.
    /// </summary>
    public static IEnumerable<EntityDefinition> ToDefinitions(
        CodexDocument doc,
        CompositeVariantMode compositeMode = CompositeVariantMode.Legacy)
    {
        foreach (var c in doc.Cards)
        {
            if (!c.Enabled) continue;

            // Имя карточки и канон ОБЯЗАНЫ участвовать в поиске наравне с алиасами:
            // исключение оставляло главное имя без точного совпадения и морфологии
            // (QA BUG-001: «Наг и Йеб» не искалось фразой и в падежах).
            var irregular = new List<string>();
            void AddVariant(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return;
                var v = s.Trim();
                if (v.Length > 0 && !irregular.Contains(v, StringComparer.OrdinalIgnoreCase))
                    irregular.Add(v);
            }
            foreach (var a in c.Aliases) AddVariant(a);
            AddVariant(c.Canonical);
            AddVariant(c.Name);

            var canon = string.IsNullOrWhiteSpace(c.Canonical) ? c.Name : c.Canonical;

            yield return new EntityDefinition(
                c.Name,
                canon,
                irregular,
                [],
                Enabled: true,
                Group: c.Group)
            {
                CompositeMode = compositeMode,
            };
        }
    }

    /// <summary>Card display name → category display name, for report grouping.</summary>
    public static Dictionary<string, string> TypeByName(CodexDocument doc)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in doc.Cards)
        {
            var typeName = doc.FindType(c.TypeId)?.Name ?? "Прочее";
            map[c.Name] = typeName;
            if (!string.Equals(c.Canonical, c.Name, StringComparison.Ordinal))
                map.TryAdd(c.Canonical, typeName);
        }
        return map;
    }

    /// <summary>Card name → category color, for UI badges and graphs.</summary>
    public static Dictionary<string, string> ColorByName(CodexDocument doc)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in doc.Cards)
        {
            var color = doc.FindType(c.TypeId)?.Color ?? "#C9A227";
            map[c.Name] = c.ColorMark ?? color;
            if (!string.Equals(c.Canonical, c.Name, StringComparison.Ordinal))
                map.TryAdd(c.Canonical, c.ColorMark ?? color);
        }
        return map;
    }
}
