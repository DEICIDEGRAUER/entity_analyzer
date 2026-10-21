using EntityAnalyzer.Core.Persistence;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityAnalyzer.Core.Codex;

/// <summary>Data kind of a schema field.</summary>
public enum FieldType
{
    Text,
    MultiLine,
    Number,
    Date,
    Flag,
    Choice,
    Link,
    List,
}

/// <summary>Schema of one card field inside a category.</summary>
public sealed class FieldSchema
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public FieldType Type { get; set; } = FieldType.Text;
    public bool Required { get; set; }
    public int Order { get; set; }
    /// <summary>Vocabulary for Choice fields.</summary>
    public List<string> Choices { get; set; } = [];
}

/// <summary>A user-editable card category («Божество», «Город», «Артефакт», …).</summary>
public sealed class CardType
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "◈";
    public string Color { get; set; } = "#C9A227";
    public ObservableCollection<FieldSchema> Fields { get; set; } = [];

    public CardType() { }

    public CardType(string id, string name, string icon, string color)
    {
        Id = id;
        Name = name;
        Icon = icon;
        Color = color;
    }

    public override string ToString() => Icon + "  " + Name;
}

/// <summary>Typed directed relation between cards.</summary>
public sealed class RelationType
{
    public string Id { get; set; } = "";
    public string ForwardLabel { get; set; } = "";
    public string ReverseLabel { get; set; } = "";

    public RelationType() { }

    public RelationType(string id, string forward, string reverse)
    {
        Id = id;
        ForwardLabel = forward;
        ReverseLabel = reverse;
    }

    public override string ToString() => ForwardLabel;
}

/// <summary>Polymorphic field value; only the slot matching the schema type is filled.</summary>
public sealed class FieldValue
{
    public string? Text { get; set; }
    public double? Number { get; set; }
    public DateTime? Date { get; set; }
    public bool? Flag { get; set; }
    public string? Choice { get; set; }
    public string? LinkCardId { get; set; }
    public string? LinkCardName { get; set; }
    public List<string>? Items { get; set; }

    public static FieldValue Empty => new();
    public static FieldValue FromText(string s) => new() { Text = s };
    public static FieldValue FromNumber(double d) => new() { Number = d };
    public static FieldValue FromDate(DateTime d) => new() { Date = d };
    public static FieldValue FromFlag(bool b) => new() { Flag = b };
    public static FieldValue FromChoice(string c) => new() { Choice = c };
    public static FieldValue FromLink(string cardId, string cardName) =>
        new() { LinkCardId = cardId, LinkCardName = cardName };
    public static FieldValue FromList(List<string> items) => new() { Items = items };

    /// <summary>Human-readable single-line representation.</summary>
    public string Display()
    {
        if (Text is not null) return Text;
        if (Number is not null) return Number.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        if (Date is not null) return Date.Value.ToString("dd.MM.yyyy");
        if (Flag is not null) return Flag.Value ? "да" : "нет";
        if (Choice is not null) return Choice;
        if (LinkCardId is not null) return "→ " + (LinkCardName ?? LinkCardId);
        if (Items is not null) return string.Join(", ", Items);
        return "";
    }
}

/// <summary>Directed typed link between two cards.</summary>
public sealed class CardLink
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public string RelationId { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>
    /// Evidence/confidence id: unknown, proven, likely, hypothesis, disputed.
    /// Empty in old documents is treated as unknown.
    /// </summary>
    public string Confidence { get; set; } = "";

    public CardLink() { }

    public CardLink(string fromId, string toId, string relationId, string note = "", string confidence = "")
    {
        FromId = fromId;
        ToId = toId;
        RelationId = relationId;
        Note = note;
        Confidence = confidence;
    }
}

/// <summary>Universal world-card: deity, artifact, city, race, event…</summary>
public sealed class Card
{
    public string Id { get; set; } = NewId();
    public string TypeId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Canonical spelling used in reports (may equal Name).</summary>
    public string Canonical { get; set; } = "";
    /// <summary>All searchable spellings (aliases always include Name and Canonical).</summary>
    public List<string> Aliases { get; set; } = [];
    /// <summary>Free-form lore text (Markdown).</summary>
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    /// <summary>Optional color mark (#RRGGBB), independent from the category color.</summary>
    public string? ColorMark { get; set; }
    public string? ImagePath { get; set; }
    public string Group { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Schema-driven values keyed by FieldSchema.Id.</summary>
    public Dictionary<string, FieldValue> Fields { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    public static Card Create(string typeId, string name)
    {
        var c = new Card { TypeId = typeId, Name = name, Canonical = name };
        c.Aliases.Add(name);
        return c;
    }

    public static string NewId() => "c_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>UI- and UIA-friendly display (combos, lists).</summary>
    public override string ToString() => Name;

    /// <summary>Adds an alias unless it duplicates an existing one (case-insensitive).</summary>
    public void AddAlias(string alias)
    {
        var a = alias.Trim();
        if (a.Length == 0) return;
        foreach (var existing in Aliases)
            if (string.Equals(existing, a, StringComparison.OrdinalIgnoreCase)) return;
        Aliases.Add(a);
    }
}

/// <summary>Versioned codex document: categories, relation types, cards, links.</summary>
public sealed class CodexDocument
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public ObservableCollection<CardType> Types { get; set; } = [];
    public ObservableCollection<RelationType> Relations { get; set; } = [];
    public ObservableCollection<Card> Cards { get; set; } = [];
    public ObservableCollection<CardLink> Links { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

        /// <summary>
    /// R1-26: безопасный импорт карточек из пака. Карточки с уже существующими
    /// именами (без учёта регистра) пропускаются; недостающие категории
    /// добавляются. Возвращает число добавленных карточек.
    /// </summary>
    public int MergeFrom(CodexDocument other)
    {
        foreach (var t in other.Types)
            if (!Types.Any(x => string.Equals(x.Id, t.Id, StringComparison.OrdinalIgnoreCase)))
                Types.Add(t);

        var existing = Cards.Select(c => c.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var c in other.Cards)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            if (!existing.Add(c.Name.Trim())) continue;
            Cards.Add(c);
            added++;
        }
        return added;
    }

public static CodexDocument CreateEmpty()
    {
        var doc = new CodexDocument();
        SeedBuiltinTypes(doc);
        return doc;
    }

    /// <summary>Fills in the built-in categories and relation vocabulary when absent.</summary>
    public static void SeedBuiltinTypes(CodexDocument doc)
    {
        if (doc.Types.Count == 0)
        {
            doc.Types.Add(new CardType("deity", "Божество", "🜏", "#C9A227"));
            doc.Types.Add(new CardType("artifact", "Артефакт", "⚱", "#B07FD9"));
            doc.Types.Add(new CardType("location", "Локация", "🏛", "#7FB2D9"));
            doc.Types.Add(new CardType("race", "Раса / Народ", "🧬", "#7FD9A5"));
            doc.Types.Add(new CardType("cult", "Культ", "🕯", "#D97F7F"));
            doc.Types.Add(new CardType("event", "Событие", "⚡", "#E8C468"));
            doc.Types.Add(new CardType("person", "Персонаж", "👤", "#D9A57F"));
            doc.Types.Add(new CardType("work", "Произведение", "📜", "#A5A5A5"));

            doc.Types[0].Fields.Add(new FieldSchema { Id = "f_domain", Name = "Домен", Type = FieldType.Text });
            doc.Types[0].Fields.Add(new FieldSchema { Id = "f_origin", Name = "Происхождение", Type = FieldType.MultiLine });

            doc.Types[1].Fields.Add(new FieldSchema { Id = "f_owner", Name = "Владелец", Type = FieldType.Link });
            doc.Types[1].Fields.Add(new FieldSchema { Id = "f_material", Name = "Материал", Type = FieldType.Choice,
                Choices = ["камень", "металл", "пергамент", "плоть", "иной"] });

            doc.Types[2].Fields.Add(new FieldSchema { Id = "f_region", Name = "Регион", Type = FieldType.Text });
            doc.Types[2].Fields.Add(new FieldSchema { Id = "f_founded", Name = "Основан", Type = FieldType.Number });

            doc.Types[3].Fields.Add(new FieldSchema { Id = "f_habitat", Name = "Ареал", Type = FieldType.Text });
            doc.Types[3].Fields.Add(new FieldSchema { Id = "f_traits", Name = "Черты", Type = FieldType.List });
        }

        if (doc.Relations.Count == 0)
        {
            doc.Relations.Add(new RelationType("owns", "владеет", "принадлежит"));
            doc.Relations.Add(new RelationType("located", "находится в", "содержит"));
            doc.Relations.Add(new RelationType("serves", "служит", "повелевает"));
            doc.Relations.Add(new RelationType("enemy", "враждует с", "враждует с"));
            doc.Relations.Add(new RelationType("spawn", "порождает", "порождён от"));
            doc.Relations.Add(new RelationType("parent", "родитель", "потомок"));
            doc.Relations.Add(new RelationType("ally", "союзник", "союзник"));
            doc.Relations.Add(new RelationType("cooccur", "упоминается вместе с", "упоминается вместе с"));
        }

        // Forward-compat: тип родства — не «настройка для избранных». Старые
        // кодексы получают словарь приёмных/опекунских/крёстных/партнёрских
        // связей, но существующие типы и их подписи не перезаписываются.
        Ensure("adopt", "усыновляет", "приёмный ребёнок");
        Ensure("foster", "берёт на воспитание", "воспитанник");
        Ensure("guardian", "опекун", "подопечный");
        Ensure("godparent", "крёстный", "крестник");
        Ensure("named", "названый родитель", "названый ребёнок");
        Ensure("partner", "партнёр", "партнёр");

        void Ensure(string id, string forward, string reverse)
        {
            if (doc.Relations.Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)))
                return;
            doc.Relations.Add(new RelationType(id, forward, reverse));
        }
    }

    /// <summary>Loss-free migration from the flat entity catalog (v1).</summary>
    public static CodexDocument MigrateFromV1(CatalogDocument v1)
    {
        var doc = CreateEmpty();
        foreach (var e in v1.Entities)
        {
            var card = Card.Create("deity", e.Name);
            card.Canonical = string.IsNullOrWhiteSpace(e.Canon) ? e.Name : e.Canon;
            card.Group = e.Group;
            card.Enabled = e.Enabled;
            card.AddAlias(card.Canonical);
            foreach (var t in e.Titles) card.AddAlias(t);
            foreach (var i in e.Irregular) card.AddAlias(i);
            doc.Cards.Add(card);
        }
        return doc;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static CodexDocument FromJson(string json)
    {
        var doc = JsonSerializer.Deserialize<CodexDocument>(json, JsonOpts)
                  ?? throw new JsonException("codex document is null");
        SeedBuiltinTypes(doc); // forward-compat: old files get the standard vocabulary
        return doc;
    }

    public Card? FindCard(string id) => Cards.FirstOrDefault(c => c.Id == id);
    public CardType? FindType(string id) => Types.FirstOrDefault(t => t.Id == id);
    public RelationType? FindRelation(string id) => Relations.FirstOrDefault(r => r.Id == id);
}
