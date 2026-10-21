using System.IO;
using System.Text;
using System.Text.Json;

namespace EntityAnalyzer.Core.Logging;

/// <summary>
/// R2-17 «Офлайн-статистика использования»: локальный счётчик событий в
/// stats.jsonl рядом с настройками. Данные никогда не покидают машину;
/// выгрузка — только вручную самим пользователем. Контракт: сбой записи
/// не должен ломать приложение (тихо игнорируется).
/// </summary>
public static class UsageStats
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Кириллица в деталях событий остаётся читаемой; ключи — camelCase.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Десериализация positional-records: параметры конструктора в PascalCase.
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Строка статистики. Класс со свойствами — чтобы STJ
    /// гарантированно работал в обе стороны (camelCase JSON).</summary>
    public sealed class Entry
    {
        public string Ts { get; set; } = "";
        public string Ev { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    private static string FilePath(string dir) => Path.Combine(dir, "stats.jsonl");

    /// <summary>Добавить событие. Никогда не бросает.</summary>
    public static void Append(string dir, string ev, string detail)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var entry = new Entry
            {
                Ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Ev = ev,
                Detail = detail,
            };
            File.AppendAllText(FilePath(dir), JsonSerializer.Serialize(entry, JsonOpts) + "\n", Encoding.UTF8);
        }
        catch { /* телеметрия не должна ломать приложение */ }
    }

    /// <summary>Суммы по парам «событие|деталь» для ручной выгрузки.</summary>
    public static Dictionary<string, int> Count(string dir)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(FilePath(dir)))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<Entry>(line, JsonOpts);
                    if (e is null) continue;
                    var key = $"{e.Ev}|{e.Detail}";
                    result[key] = result.GetValueOrDefault(key) + 1;
                }
                catch { /* битая строка не роняет сводку */ }
            }
        }
        catch { /* файла нет — пустая статистика */ }
        return result;
    }
}
