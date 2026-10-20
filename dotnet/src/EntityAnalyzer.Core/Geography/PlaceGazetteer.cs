using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.Core.Geography;

/// <summary>
/// Внутренняя база координат: встроенный JSON Мифов Ктулху и реальных мест,
/// дополняемый пользовательским файлом <c>user_places.json</c> в профиле.
/// Поиск терпим к падежным окончаниям и апострофам: «Иннсмуте» → «Innsmouth».
/// </summary>
public sealed class PlaceGazetteer
{
    private const string EmbeddedResource = "EntityAnalyzer.Core.Resources.default_place_gazetteer.json";

    /// <summary>Падежные окончания, отбрасываемые при сопоставлении.</summary>
    private static readonly string[] CaseSuffixes =
    [
        "ей", "ой", "ом", "ем", "ах", "ях", "ов", "ев", "ий", "ия", "ии", "ию", "ием",
        "а", "я", "у", "ю", "е", "ы", "и", "ь",
    ];

    private readonly List<GazetteerPlace> _places;
    private readonly Dictionary<string, GazetteerPlace> _index;

    private PlaceGazetteer(List<GazetteerPlace> places)
    {
        _places = places;
        _index = new Dictionary<string, GazetteerPlace>(StringComparer.Ordinal);
        foreach (var place in places)
            foreach (var form in Forms(place))
                _index.TryAdd(Normalize(form), place);
    }

    public IReadOnlyList<GazetteerPlace> Places => _places;

    /// <summary>Встроенная база без пользовательского файла (детерминированно для тестов).</summary>
    public static PlaceGazetteer LoadEmbedded() => FromJson(ReadEmbedded());

    /// <summary>Встроенная база плюс пользовательский файл профиля, если он есть.</summary>
    public static PlaceGazetteer Load()
    {
        var place = LoadEmbedded();
        var userPath = Path.Combine(ProfilePaths.DirectoryPath, "user_places.json");
        if (File.Exists(userPath))
        {
            try
            {
                place = Merge(place, FromJson(File.ReadAllText(userPath, Encoding.UTF8)));
            }
            catch
            {
                // Битый пользовательский файл не должен ронять географию.
            }
        }
        return place;
    }

    /// <summary>Разбор JSON; исключения конфигурации превращаются в пустую базу.</summary>
    public static PlaceGazetteer FromJson(string json)
    {
        var root = JsonSerializer.Deserialize<Root>(json, JsonOptions) ?? new Root();
        var places = new List<GazetteerPlace>();
        foreach (var entry in root.Places)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            var cls = PlaceClasses.TryParse(entry.Class) ?? PlaceClass.City;
            var point = entry is { Lat: { } lat, Lon: { } lon } ? new GeoPoint(lat, lon) : null;
            places.Add(new GazetteerPlace(
                entry.Name.Trim(),
                (entry.Aliases ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
                cls,
                point,
                entry.Fictional,
                entry.Note ?? ""));
        }
        return new PlaceGazetteer(places);
    }

    /// <summary>Слияние: пользовательские записи дополняют встроенные по имени.</summary>
    public static PlaceGazetteer Merge(PlaceGazetteer basePlaces, PlaceGazetteer user)
    {
        var merged = new List<GazetteerPlace>(basePlaces._places);
        var known = new HashSet<string>(basePlaces._places.Select(p => Normalize(p.Name)), StringComparer.Ordinal);
        foreach (var place in user._places)
            if (known.Add(Normalize(place.Name)))
                merged.Add(place);
        return new PlaceGazetteer(merged);
    }

    /// <summary>Найти место по написанию (с падежными формами и без апострофов).</summary>
    public bool TryMatch(string name, out GazetteerPlace place)
    {
        place = null!;
        var normalized = Normalize(name);
        if (normalized.Length == 0) return false;
        if (_index.TryGetValue(normalized, out var direct))
        {
            place = direct;
            return true;
        }
        foreach (var suffix in CaseSuffixes)
        {
            if (normalized.Length <= suffix.Length + 2) continue;
            if (!normalized.EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (_index.TryGetValue(normalized[..^suffix.Length], out var trimmed))
            {
                place = trimmed;
                return true;
            }
        }
        return false;
    }

    /// <summary>Нормализация для сопоставления: нижний регистр, ё→е, только буквы и цифры.</summary>
    public static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant().Replace('ё', 'е'))
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> Forms(GazetteerPlace place)
    {
        yield return place.Name;
        foreach (var alias in place.Aliases)
            yield return alias;
    }

    private static string ReadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResource)
            ?? throw new InvalidOperationException("Не найден ресурс базы мест: " + EmbeddedResource);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class Root
    {
        [JsonPropertyName("places")]
        public List<Entry> Places { get; set; } = [];
    }

    private sealed class Entry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("aliases")]
        public List<string>? Aliases { get; set; }

        [JsonPropertyName("class")]
        public string Class { get; set; } = "city";

        [JsonPropertyName("lat")]
        public double? Lat { get; set; }

        [JsonPropertyName("lon")]
        public double? Lon { get; set; }

        [JsonPropertyName("fictional")]
        public bool Fictional { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}

/// <summary>
/// Классы мест и их словари: встроенный JSON дополняется пользовательским
/// <c>user_place_heads.json</c> — новые слова добавляются к существующим классам.
/// </summary>
public sealed class PlaceHeads
{
    private const string EmbeddedResource = "EntityAnalyzer.Core.Resources.default_place_heads.json";

    private readonly Dictionary<PlaceClass, List<string>> _classes = [];

    public IReadOnlyList<string> Words(PlaceClass cls) =>
        _classes.TryGetValue(cls, out var list) ? list : [];

    public IEnumerable<PlaceClass> PresentClasses => _classes.Keys.OrderBy(c => c);

    public int WordCount => _classes.Values.Sum(v => v.Count);

    /// <summary>Встроенные словари без пользовательского файла (детерминированно для тестов).</summary>
    public static PlaceHeads LoadEmbedded() => FromJson(ReadEmbedded());

    public static PlaceHeads Load()
    {
        var heads = LoadEmbedded();
        var userPath = Path.Combine(ProfilePaths.DirectoryPath, "user_place_heads.json");
        if (File.Exists(userPath))
        {
            try
            {
                heads = Merge(heads, FromJson(File.ReadAllText(userPath, Encoding.UTF8)));
            }
            catch
            {
                // Битый пользовательский словарь не должен ронять географию.
            }
        }
        return heads;
    }

    public static PlaceHeads FromJson(string json)
    {
        var root = JsonSerializer.Deserialize<HeadRoot>(json, JsonOptions) ?? new HeadRoot();
        var classes = new Dictionary<PlaceClass, List<string>>();
        foreach (var (key, words) in root.Classes)
        {
            var cls = PlaceClasses.TryParse(key);
            if (cls is null) continue;
            var list = new List<string>();
            foreach (var word in words)
            {
                var trimmed = (word ?? "").Trim();
                if (trimmed.Length > 0)
                    list.Add(trimmed.ToLowerInvariant());
            }
            classes[cls.Value] = list;
        }
        return new PlaceHeads(classes);
    }

    private PlaceHeads(Dictionary<PlaceClass, List<string>> classes) => _classes = classes;

    private static PlaceHeads Merge(PlaceHeads a, PlaceHeads b)
    {
        var merged = new Dictionary<PlaceClass, List<string>>();
        foreach (var cls in a._classes.Keys.Union(b._classes.Keys))
        {
            var words = new List<string>();
            if (a._classes.TryGetValue(cls, out var wa)) words.AddRange(wa);
            if (b._classes.TryGetValue(cls, out var wb))
                foreach (var word in wb)
                    if (!words.Contains(word, StringComparer.OrdinalIgnoreCase))
                        words.Add(word);
            merged[cls] = words;
        }
        return new PlaceHeads(merged);
    }

    private static string ReadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResource)
            ?? throw new InvalidOperationException("Не найден ресурс классов мест: " + EmbeddedResource);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class HeadRoot
    {
        [JsonPropertyName("classes")]
        public Dictionary<string, List<string>> Classes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
