using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityAnalyzer.Core.Semantics;

/// <summary>Класс токена паттерна.</summary>
public enum TokenKind
{
    /// <summary>Точное слово (без учёта регистра, нормализованная форма).</summary>
    Literal,
    /// <summary>Слово из именованного словаря (epithets, abstract, ritual, titles, periods…).</summary>
    Lexicon,
    /// <summary>Имя собственное: известная сущность или слово с заглавной буквы не в начале предложения.</summary>
    Proper,
    /// <summary>Регулярное выражение по слову.</summary>
    Regex,
    /// <summary>Любое слово.</summary>
    AnyWord,
    /// <summary>Любой из вложенных вариантов (Options); потребляет слово.</summary>
    AnyOf,
    /// <summary>
    /// Негативный страж нулевой ширины: слово, стоящее непосредственно ПЕРЕД
    /// следующим потребляющим токеном, не должно соответствовать Options[0].
    /// </summary>
    Not,
    /// <summary>
    /// Слово с заглавной буквы в любой позиции предложения (в отличие от Proper,
    /// которому нужна известная сущность или заглавная НЕ в начале предложения).
    /// Для конструкций вида «Год Красной Луны», «Час Воющей Собаки».
    /// </summary>
    Capitalized,
}

/// <summary>Один элемент последовательности паттерна.</summary>
public sealed record SemanticToken(
    TokenKind Kind,
    string Value,
    bool Required = true,
    IReadOnlyList<SemanticToken>? Options = null,
    int Min = 1,
    int Max = 1)
{
    public static SemanticToken Lit(string word) => new(TokenKind.Literal, word);
    public static SemanticToken Lex(string name) => new(TokenKind.Lexicon, name);
    public static SemanticToken Prop() => new(TokenKind.Proper, "");
    public static SemanticToken Rx(string pattern) => new(TokenKind.Regex, pattern);
    public static SemanticToken Any() => new(TokenKind.AnyWord, "");
    public static SemanticToken Cap(int min = 1, int max = 1) =>
        new(TokenKind.Capitalized, "", Min: min, Max: Math.Max(max, min));
    public static SemanticToken AnyOf(params SemanticToken[] options) =>
        new(TokenKind.AnyOf, "", Options: options);
    public static SemanticToken NotBefore(SemanticToken inner) =>
        new(TokenKind.Not, "", Options: [inner]);
}

/// <summary>
/// Декларативный паттерн: упорядоченная последовательность классов токенов.
/// Window — максимум «пустых» слов между соседними совпадениями;
/// TotalWindow — при задании ограничивает размах всего паттерна
/// (расстояние в словах от первого до последнего токена).
/// Токен с Required=false может отсутствовать.
/// Задаётся в JSON-конфигурации; новые категории — без изменения кода ядра.
/// </summary>
public sealed record SemanticPattern(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<SemanticToken> Tokens,
    int Window = 4,
    bool Enabled = true,
    string Branch = "",
    int? TotalWindow = null);

// ─── JSON-контракты конфигурации (camelCase) ───

internal sealed class PatternFileDto
{
    [JsonPropertyName("patterns")] public List<PatternDto> Patterns { get; set; } = [];
}

internal sealed class PatternDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("branch")] public string Branch { get; set; } = "";
    [JsonPropertyName("window")] public int Window { get; set; } = 4;
    [JsonPropertyName("total_window")] public int? TotalWindow { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("tokens")] public List<TokenDto> Tokens { get; set; } = [];

    public SemanticPattern ToModel() => new(Id, Name, Description,
        [.. Tokens.Select(t => t.ToModel())],
        Math.Max(1, Window), Enabled, Branch,
        TotalWindow is > 0 ? TotalWindow : null);

    public static PatternDto FromModel(SemanticPattern p) => new()
    {
        Id = p.Id, Name = p.Name, Description = p.Description, Branch = p.Branch,
        Window = p.Window, Enabled = p.Enabled,
        TotalWindow = p.TotalWindow,
        Tokens = [.. p.Tokens.Select(TokenDto.FromModel)],
    };
}

internal sealed class TokenDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "anyword";
    [JsonPropertyName("value")] public string Value { get; set; } = "";

    /// <summary>null = true (по умолчанию); false пишется в JSON явно.</summary>
    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Required { get; set; }

    /// <summary>Повторы токена подряд: min..max (по умолчанию 1..1).</summary>
    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Min { get; set; }

    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Max { get; set; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TokenDto>? Options { get; set; }

    public SemanticToken ToModel()
    {
        var kind = ParseKind(Kind);
        var options = kind is TokenKind.AnyOf or TokenKind.Not
            ? (Options ?? []).Select(o => o.ToModel()).ToList()
            : null;
        var min = Math.Max(0, Min ?? 1);
        var max = Math.Max(min, Max ?? min);
        return new SemanticToken(kind, Value, Required ?? true, options, min, max);
    }

    public static TokenDto FromModel(SemanticToken t) => new()
    {
        Kind = SerializeKind(t.Kind),
        Value = t.Value,
        Required = t.Required ? null : false,
        Min = t.Min == 1 ? null : t.Min,
        Max = t.Max == 1 ? null : t.Max,
        Options = t.Options is null ? null : [.. t.Options.Select(FromModel)],
    };

    /// <summary>Канонические имена в JSON (any_of, а не anyof).</summary>
    private static string SerializeKind(TokenKind k) => k switch
    {
        TokenKind.AnyOf => "any_of",
        TokenKind.AnyWord => "anyword",
        TokenKind.Literal => "literal",
        TokenKind.Lexicon => "lexicon",
        TokenKind.Proper => "proper",
        TokenKind.Regex => "regex",
        TokenKind.Not => "not",
        TokenKind.Capitalized => "capitalized",
        _ => "anyword",
    };

    private static TokenKind ParseKind(string s) => s?.ToLowerInvariant() switch
    {
        "literal" => TokenKind.Literal,
        "lexicon" => TokenKind.Lexicon,
        "proper" => TokenKind.Proper,
        "regex" => TokenKind.Regex,
        "any_of" or "anyof" => TokenKind.AnyOf,
        "not" => TokenKind.Not,
        "capitalized" or "cap" => TokenKind.Capitalized,
        _ => TokenKind.AnyWord,
    };
}

/// <summary>Сериализация/чтение файлов паттернов.</summary>
public static class SemanticPatternJson
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static List<SemanticPattern> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<PatternFileDto>(json, Opts) ?? new PatternFileDto();
        return [.. file.Patterns.Select(d => d.ToModel()).Where(p => p.Id.Length > 0 && p.Tokens.Count > 0)];
    }

    /// <summary>
    /// Разбор массива токенов из JSON — для редактора паттернов
    /// (поле Options у any_of/not и предпросмотр всей цепочки).
    /// </summary>
    public static List<SemanticToken> ParseTokensArray(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<TokenDto>>(json, Opts) ?? [];
        return [.. dtos.Select(d => d.ToModel())];
    }

    /// <summary>Сериализация массива токенов в JSON (для редактора).</summary>
    public static string SerializeTokensArray(IReadOnlyList<SemanticToken> tokens) =>
        JsonSerializer.Serialize(tokens.Select(TokenDto.FromModel).ToList(), Opts);

    public static string Serialize(IEnumerable<SemanticPattern> patterns) =>
        JsonSerializer.Serialize(new PatternFileDto
        {
            Patterns = [.. patterns.Select(PatternDto.FromModel)],
        }, Opts);
}
