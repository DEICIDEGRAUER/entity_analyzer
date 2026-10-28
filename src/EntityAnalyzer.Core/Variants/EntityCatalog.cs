using EntityAnalyzer.Core.Matching;

namespace EntityAnalyzer.Core.Variants;

/// <summary>
/// One tracked entity with its canonical English name and curated lists.
/// Disabled entities stay in the catalog document but are excluded from search.
/// </summary>
public sealed record EntityDefinition(
    string Name,
    string Canon,
    IReadOnlyList<string> Irregular,
    IReadOnlyList<string> Titles,
    bool Enabled = true,
    string Group = "")
{
    /// <summary>
    /// Composite-only generator policy. Single names are unaffected. The
    /// default is <see cref="CompositeVariantMode.Legacy"/> until activation;
    /// full-phrase modes only emit approved standalone exceptions.
    /// </summary>
    public CompositeVariantMode CompositeMode { get; init; } =
        CompositeGeneratorPolicy.DefaultMode;

    public List<string> StandaloneExceptions { get; init; } =
        CompositeGeneratorPolicy.ExceptionsFor(Name).ToList();
}

/// <summary>Payload attached to every automaton hit.</summary>
public readonly record struct EntityRef(string Entity, string Variant);

/// <summary>
/// Builds the variant pool per entity and the Aho–Corasick automaton over it.
/// Variant generation can be disabled for fast re-runs on curated lists only.
/// </summary>
public sealed class EntityCatalog
{
    /// <summary>Everyday words whose collision with entity names demands extra care.</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "грот", "кот", "тит", "йод", "муп", "сон", "мир", "дом", "рот", "ток",
        "grot", "cat", "tit", "iod", "mup", "son", "mir", "dom", "rot", "tok"
    };

    public IReadOnlyDictionary<string, IReadOnlyList<string>> VariantsByEntity { get; }
    /// <summary>Original definitions keyed by entity name.</summary>
    public IReadOnlyDictionary<string, EntityDefinition> Definitions { get; }
    public IReadOnlyCollection<string> AllNames => VariantsByEntity.Keys.ToList();

    public EntityCatalog(IReadOnlyList<EntityDefinition> definitions, bool generateVariants = true)
    {
        var factory = new VariantFactory();
        var map = new Dictionary<string, IReadOnlyList<string>>(definitions.Count);
        var defMap = new Dictionary<string, EntityDefinition>(definitions.Count);

        foreach (var def in definitions)
        {
            if (!def.Enabled) continue; // выключенные сущности не участвуют в поиске
            defMap[def.Name] = def;
            var set = new OrderedSet();

            void Absorb(IEnumerable<string> source)
            {
                foreach (var s in source)
                    if (!string.IsNullOrWhiteSpace(s) && s.Length >= 3) set.Add(s.Trim());
            }

            Absorb(def.Irregular);
            Absorb(def.Titles);

            if (generateVariants)
            {
                var isCommon = IsCommonWordCandidate(def);
                var (narrow, wide) = factory.GenerateTwoPass(
                    def.Canon, def.Irregular, isCommon,
                    compositeMode: def.CompositeMode,
                    standaloneExceptions: def.StandaloneExceptions);
                Absorb(narrow);
                Absorb(wide);
                Absorb(VariantFactory.HyphenShapes(Transliterator.Basic(def.Canon)));

                // Морфология и для имени карточки, когда оно отличается от канона
                // (QA BUG-001: кодекс-путь с английским каноном терял падежи
                // русского имени — «Нага», «Нагу»…).
                if (!string.Equals(def.Name, def.Canon, StringComparison.OrdinalIgnoreCase)
                    && def.Name.Length >= 3)
                {
                    var (nameNarrow, nameWide) = factory.GenerateTwoPass(
                        def.Name, [], isCommon,
                        compositeMode: def.CompositeMode,
                        standaloneExceptions: def.StandaloneExceptions);
                    Absorb(nameNarrow);
                    Absorb(nameWide);
                }
            }

            map[def.Name] = set.Items;
        }

        VariantsByEntity = map;
        Definitions = defMap;
    }

    public AhoCorasick<EntityRef> BuildAutomaton()
    {
        var ac = new AhoCorasick<EntityRef>();
        foreach (var (entity, variants) in VariantsByEntity)
            foreach (var variant in variants)
                ac.Add(variant, new EntityRef(entity, variant));
        ac.Build();
        return ac;
    }

    public bool IsCommonWordEntity(string entityName)
    {
        if (!VariantsByEntity.TryGetValue(entityName, out var variants)) return false;
        foreach (var v in variants)
            if (CommonWords.Contains(v.ToLowerInvariant())) return true;
        return false;
    }

    /// <summary>Plain-text index of all variants for one entity (report parity feature).</summary>
    public string ExportVariantsText(string entityName)
    {
        if (!VariantsByEntity.TryGetValue(entityName, out var variants)) return string.Empty;
        return string.Join(Environment.NewLine, variants.OrderBy(v => v, StringComparer.Ordinal));
    }

    private static bool IsCommonWordCandidate(EntityDefinition def)
    {
        var basic = Transliterator.Basic(def.Canon).ToLowerInvariant();
        return CommonWords.Contains(basic) || def.Irregular.Any(v => CommonWords.Contains(v.ToLowerInvariant()));
    }

    private sealed class OrderedSet
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public List<string> Items { get; } = [];
        public void Add(string s)
        {
            if (_seen.Add(s)) Items.Add(s);
        }
    }
}
