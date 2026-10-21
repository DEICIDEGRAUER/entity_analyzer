using System.Net;
using System.Net.Http;
using System.Text;
using EntityAnalyzer.Cli;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Headless;

/// <summary>
/// R1-12: интеграционные тесты headless HTTP-хоста в постоянном наборе.
/// Реальный HttpListener на свободном порту + HttpClient; корпус во временном
/// каталоге. Ранее сценарий жил только в _scratch/headless_e2e.ps1.
/// </summary>
[Collection("LogSequential")] // прогон движка пишет в глобальный Log
public class HeadlessServerIntegrationTests
{
    private static int GetFreePort()
    {
        // Занять порт 0 у системы, узнать номер и отпустить — окно гонки
        // минимально и приемлемо для тестовой среды.
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    private static string WriteCorpus()
    {
        var corpus = Path.Combine(Path.GetTempPath(), "ea_headless_it_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, "t1.txt"),
            "Хроники храма. Наг и Йеб правили веками. Культ Нага процветал у моря.\n",
            new UTF8Encoding(false));
        return corpus;
    }

    [Fact]
    public async Task Serve_handles_health_analyze_and_rejects_bad_requests()
    {
        var port = GetFreePort();
        using var cts = new CancellationTokenSource();
        var serverTask = Task.Run(() => HeadlessServer.Serve("127.0.0.1", port, cts.Token));

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var corpus = WriteCorpus();
        try
        {
            // Дождаться готовности листенера (поллинг /health, ≤10 с).
            HttpResponseMessage? health = null;
            for (var i = 0; i < 50 && health is null; i++)
            {
                try { health = await http.GetAsync("health"); }
                catch { await Task.Delay(200); }
            }
            Assert.NotNull(health);
            Assert.True(health!.IsSuccessStatusCode);

            var healthJson = await health.Content.ReadAsStringAsync();
            Assert.Contains("\"ok\"", healthJson);
            Assert.Contains(Core.About.Version, healthJson);

            // Полный анализ мини-корпуса.
            var body = $$"""
                {"corpus":"{{corpus.Replace("\\", "\\\\")}}","preset":"light","formats":["json"]}
                """;
            var analyze = await http.PostAsync("analyze",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, analyze.StatusCode);

            var json = await analyze.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("totalMentions").GetInt32() >= 1);
            Assert.True(doc.RootElement.GetProperty("funnel").GetProperty("rawMatches").GetInt32() >= 1);
            Assert.True(Directory.Exists(doc.RootElement.GetProperty("reportsDir").GetString()));

            // Запрос без corpus → 400 с сообщением об ошибке.
            var bad = await http.PostAsync("analyze",
                new StringContent("""{"preset":"light"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("corpus", await bad.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            // Неизвестный маршрут → 404.
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("nope")).StatusCode);
        }
        finally
        {
            cts.Cancel();
            try { Directory.Delete(corpus, recursive: true); } catch { /* занято — не критично */ }
        }

        // Хост корректно завершается по отмене.
        var finished = await Task.WhenAny(serverTask, Task.Delay(10_000));
        Assert.Same(serverTask, finished);
        Assert.Equal(0, await serverTask);
    }
}
