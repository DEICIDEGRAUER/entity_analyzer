using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityAnalyzer.Core.Headless;

/// <summary>
/// R1-12: запрос удалённого анализа (POST /analyze).
/// Минимум обязательного — корпус; остальное имеет разумные дефолты.
/// Класс (а не record): JsonSerializer заполняет только пришедшие поля.
/// </summary>
public sealed class HeadlessRequest
{
    public string Corpus { get; set; } = "";
    public string? Out { get; set; }
    public string Preset { get; set; } = "extended";
    public List<string> ExtraDirs { get; set; } = [];
    public List<string> Excludes { get; set; } = [];
    /// <summary>null — действует пресетный лимит цитат/файл.</summary>
    public int? MaxMentions { get; set; }
    public bool PatternsOnly { get; set; }
    /// <summary>R1-13: имена препроцессоров текста (page-numbers, collapse-repeats).</summary>
    public List<string> Preprocessors { get; set; } = [];
    public List<string> Formats { get; set; } = ["txt", "json", "csv", "html", "md"];
}

/// <summary>Компоненты воронки для ответа (зеркало QualitySnapshot).</summary>
public sealed record HeadlessFunnel(
    int RawMatches,
    int FilterRejected,
    int OverlapsResolved,
    int DisplayCapped,
    int Deduplicated,
    int DuplicatesGrouped,
    int FinalMentions);

/// <summary>Ответ анализа: сводка прогона или ошибка.</summary>
public sealed record HeadlessResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    int TotalMentions,
    int FilesTotal,
    int FilesWithMentions,
    double DurationMs,
    string ReportsDir,
    Dictionary<string, int>? EntityCounts,
    HeadlessFunnel? Funnel,
    string? Error)
{
    public static HeadlessResponse Fail(string error) =>
        new(Ok: false, 0, 0, 0, 0, "", null, null, error);
}

/// <summary>Результат разбора запроса.</summary>
public sealed record HeadlessParseResult(bool IsOk, HeadlessRequest? Request, string? Error)
{
    public static HeadlessParseResult Ok(HeadlessRequest request) => new(true, request, null);
    public static HeadlessParseResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// R1-12: разбор и сериализация протокола headless-хоста.
/// Транспорт (HTTP) живёт в CLI; здесь только модели и правила валидации.
/// </summary>
public static class HeadlessProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Разобрать JSON-тело запроса; при ошибке — сообщение клиенту.</summary>
    public static HeadlessParseResult ParseRequest(string json)
    {
        HeadlessRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<HeadlessRequest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return HeadlessParseResult.Fail($"Некорректный JSON запроса: {ex.Message}");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Corpus))
            return HeadlessParseResult.Fail("Обязательное поле «corpus» отсутствует или пусто.");

        if (request.MaxMentions is < 0)
            return HeadlessParseResult.Fail("Поле «maxMentions» должно быть ≥0 (0 = без лимита).");

        return HeadlessParseResult.Ok(request);
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string ToJson(HeadlessResponse response) =>
        JsonSerializer.Serialize(response, WriteOptions);
}
