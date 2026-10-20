using System.Net.Http;
using System.Text;
using System.Text.Json;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.Core.Geography;

/// <summary>
/// Опциональный онлайн-геокодер для реальных мест, которых нет во внутренней
/// базе: запросы к Nominatim (OpenStreetMap) с кэшем в профиле и ограничением
/// «не чаще одного запроса в секунду». Сеть используется только по явному
/// действию пользователя — автоматической привязки координат нет.
/// </summary>
public sealed class NominatimGeocoder
{
    private const string Endpoint = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&q=";

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly Dictionary<string, GeoPoint?> _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public NominatimGeocoder(HttpClient http, string cachePath)
    {
        _http = http;
        _cachePath = cachePath;
        _cache = LoadCache(cachePath);
    }

    /// <summary>Геокодер по умолчанию: кэш в профиле, вежливый User-Agent.</summary>
    public static NominatimGeocoder CreateDefault()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EntityAnalyzer/1.0 (geography module; offline-first app)");
        return new NominatimGeocoder(http, Path.Combine(ProfilePaths.DirectoryPath, "geo_cache.json"));
    }

    public int CachedCount => _cache.Count;

    /// <summary>Посмотреть ответ в кэше (в том числе отрицательный).</summary>
    public bool TryGetCached(string query, out GeoPoint? point)
    {
        if (_cache.TryGetValue(Key(query), out point)) return true;
        point = null;
        return false;
    }

    /// <summary>Найти координаты; null — не найдено (ответ тоже кэшируется).</summary>
    public async Task<GeoPoint?> LookupAsync(string query, CancellationToken ct = default)
    {
        var key = Key(query);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastRequestUtc);
            if (wait > TimeSpan.Zero && _lastRequestUtc != DateTime.MinValue)
                await Task.Delay(wait, ct).ConfigureAwait(false);

            var url = Endpoint + Uri.EscapeDataString(query);
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            _lastRequestUtc = DateTime.UtcNow;
            var point = Parse(json);
            _cache[key] = point;
            SaveCache();
            return point;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Сеть недоступна — не кэшируем ошибку, чтобы можно было повторить.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Разбор ответа Nominatim (публичный для тестов).</summary>
    public static GeoPoint? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;
            var first = doc.RootElement[0];
            if (!first.TryGetProperty("lat", out var latEl) || !first.TryGetProperty("lon", out var lonEl))
                return null;
            var latText = latEl.ValueKind == JsonValueKind.String ? latEl.GetString() : latEl.ToString();
            var lonText = lonEl.ValueKind == JsonValueKind.String ? lonEl.GetString() : lonEl.ToString();
            if (!double.TryParse(latText, System.Globalization.CultureInfo.InvariantCulture, out var lat)) return null;
            if (!double.TryParse(lonText, System.Globalization.CultureInfo.InvariantCulture, out var lon)) return null;
            return new GeoPoint(lat, lon);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Key(string query) => query.Trim().ToLowerInvariant();

    private static Dictionary<string, GeoPoint?> LoadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, GeoPoint?>(StringComparer.Ordinal);
            var json = File.ReadAllText(path, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, GeoPoint?>>(json);
            return parsed is null
                ? new Dictionary<string, GeoPoint?>(StringComparer.Ordinal)
                : new Dictionary<string, GeoPoint?>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, GeoPoint?>(StringComparer.Ordinal);
        }
    }

    private void SaveCache()
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache), Encoding.UTF8);
        }
        catch
        {
            // Кэш — вспомогательный: сбой записи не должен ломать геокодирование.
        }
    }
}
