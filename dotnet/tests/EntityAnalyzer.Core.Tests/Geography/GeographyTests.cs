using System.Net;
using System.Net.Http;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Geography;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Geography;

/// <summary>
/// Группа 4 (item 19): классы мест, извлечение имён и контекста, внутренняя
/// база координат, агрегация с цитатами, HTML-карта и онлайн-геокодер.
/// </summary>
public class GeographyTests
{
    private static CorpusText T(string text, string file = "география.txt") =>
        new(file, @"C:\корпус\" + file, text);

    private static readonly PlaceHeads Heads = PlaceHeads.LoadEmbedded();
    private static readonly PlaceGazetteer Gazetteer = PlaceGazetteer.LoadEmbedded();

    [Fact]
    public void Embedded_gazetteer_contains_real_and_fictional_places()
    {
        Assert.True(Gazetteer.Places.Count >= 100);
        Assert.Contains(Gazetteer.Places, p => p.Fictional && p.Point is not null);
        Assert.Contains(Gazetteer.Places, p => !p.Fictional && p.Point is not null);
    }

    [Fact]
    public void Gazetteer_matches_inflected_and_apostrophe_forms()
    {
        Assert.True(Gazetteer.TryMatch("Иннсмуте", out var innsmouth));
        Assert.Equal("Innsmouth", innsmouth.Name);
        Assert.Equal(PlaceClass.Port, innsmouth.Class);
        Assert.True(innsmouth.Fictional);
        Assert.NotNull(innsmouth.Point);

        Assert.True(Gazetteer.TryMatch("R'lyeh", out var rlyeh));
        Assert.Equal(-47.15, rlyeh.Point!.Lat, 2);

        Assert.True(Gazetteer.TryMatch("Р’льех", out var rlyehRu));
        Assert.Equal("R'lyeh", rlyehRu.Name);

        Assert.False(Gazetteer.TryMatch("Несуществующее Место", out _));
    }

    [Fact]
    public void Place_heads_cover_all_declared_classes()
    {
        Assert.True(Heads.WordCount >= 100);
        foreach (var cls in PlaceClasses.All)
            Assert.NotEmpty(Heads.Words(cls));
        Assert.Contains("город", Heads.Words(PlaceClass.City));
        Assert.Contains("пещера", Heads.Words(PlaceClass.Cave));
    }

    [Fact]
    public void Extractor_finds_places_by_class_and_by_gazetteer()
    {
        var mentions = PlaceExtractor.Extract(
        [
            T("""
              Город Иннсмут стоит у моря.
              В Аркхеме есть университет.
              Иннсмут — порт в Массачусетсе.
              Деревня Глухово затерялась среди холмов.
              """)
        ], Heads, Gazetteer);

        var places = PlaceExtractor.Aggregate(mentions, Gazetteer);

        var innsmouth = places.Single(p => p.Name == "Innsmouth");
        Assert.Equal(PlaceClass.Port, innsmouth.Class);
        Assert.True(innsmouth.Fictional);
        Assert.NotNull(innsmouth.Point);
        Assert.True(innsmouth.Mentions >= 2);
        Assert.NotEmpty(innsmouth.Quotes);

        var arkham = places.Single(p => p.Name == "Arkham");
        Assert.Equal(PlaceClass.City, arkham.Class);
        Assert.Contains("Аркхеме", arkham.Variants);

        var unknown = places.Single(p => p.Name == "Глухово");
        Assert.Equal(PlaceClass.Village, unknown.Class);
        Assert.Null(unknown.Point);
        Assert.False(unknown.Fictional);
        Assert.Equal("деревня", PlaceClasses.Label(unknown.Class));
    }

    [Fact]
    public void Extractor_supports_name_before_class_with_dash()
    {
        var mentions = PlaceExtractor.Extract([T("Иннсмут — порт в Массачусетсе.")], Heads, Gazetteer);

        Assert.Contains(mentions, m => m.Name == "Иннсмут" && m.Class == PlaceClass.Port && m.PatternName == "Имя — класс");
    }

    [Fact]
    public void Aggregator_keeps_quotes_from_several_files_and_caps_them()
    {
        var mentions = new List<PlaceMention>();
        for (var i = 0; i < 12; i++)
            mentions.Add(new PlaceMention("Глухово", PlaceClass.Village, "Класс места",
                $"файл{i}.txt", $"C:\\корпус\\файл{i}.txt", i + 1, "Деревня Глухово."));

        var entry = Assert.Single(PlaceExtractor.Aggregate(mentions, Gazetteer));

        Assert.Equal(12, entry.Mentions);
        Assert.Equal(12, entry.FileCount);
        Assert.Equal(PlaceExtractor.MaxQuotesPerPlace, entry.Quotes.Count);
    }

    [Fact]
    public void Map_html_is_self_contained_leaflet_page_with_escaped_corpus_data()
    {
        var places = new List<PlaceEntry>
        {
            new("<script>alert(1)</script>", PlaceClass.City, true, new GeoPoint(10, 20), "заметка",
                3, 1, ["вариант"], [new PlaceQuote("<b>файл</b>", "путь", 7, "<img src=x onerror=alert(2)>")]),
            new("Без координат", PlaceClass.Planet, true, null, "вне карты", 1, 1, [], []),
        };

        var html = GeographyMapHtml.Render(places, "Карта <тест>");

        Assert.Contains("Карта &lt;тест&gt;", html);
        Assert.Contains("unpkg.com/leaflet", html);
        Assert.Contains("circleMarker", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("button.quote", html);
        Assert.Contains("quote-modal", html);
        Assert.Contains("data-snippet", html);
        Assert.Contains("Без координат", html);
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.DoesNotContain("<img src=x onerror", html);
    }

    [Fact]
    public async Task Nominatim_parses_response_and_caches_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ea-geo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var handler = new StubHandler("[{\"lat\":\"55.75\",\"lon\":\"37.61\"}]");
            var geocoder = new NominatimGeocoder(new HttpClient(handler), Path.Combine(dir, "cache.json"));

            var point = await geocoder.LookupAsync("Москва");
            Assert.NotNull(point);
            Assert.Equal(55.75, point!.Lat, 2);
            Assert.Equal(37.61, point.Lon, 2);
            Assert.Equal(1, handler.Calls);
            Assert.True(File.Exists(Path.Combine(dir, "cache.json")));

            var cached = await geocoder.LookupAsync("Москва");
            Assert.NotNull(cached);
            Assert.Equal(1, handler.Calls);
            Assert.True(geocoder.TryGetCached("москва", out var fromCache));
            Assert.Equal(55.75, fromCache!.Lat, 2);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("[{\"lat\":\"1.5\",\"lon\":\"-2.5\"}]", 1.5, -2.5)]
    [InlineData("[]", null, null)]
    [InlineData("not json", null, null)]
    [InlineData("[{\"display_name\":\"x\"}]", null, null)]
    public void Nominatim_parse_handles_edge_cases(string json, double? lat, double? lon)
    {
        var point = NominatimGeocoder.Parse(json);

        if (lat is null)
        {
            Assert.Null(point);
        }
        else
        {
            Assert.NotNull(point);
            Assert.Equal(lat.Value, point!.Lat, 3);
            Assert.Equal(lon!.Value, point.Lon, 3);
        }
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }
}
