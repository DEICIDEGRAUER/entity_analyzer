using System.IO;
using System.Reflection;
using System.Text.Json;

namespace EntityAnalyzer.Core.Codex;

/// <summary>
/// R1-26 «Библиотека кодексов и паттернов»: встроенные тематические паки
/// карточек (офлайн-first — ничего не скачивается). Пак можно импортировать
/// в пользовательский кодекс (GUI) или использовать вместо стандартного
/// каталога (CLI --codex-pack).
/// </summary>
public static class CodexPacks
{
    public sealed record PackInfo(string Id, string Title, string Description);

    private sealed record PackFile(string Id, string Title, string Description, CodexDocument Codex);

    private const string Prefix = "EntityAnalyzer.Core.Resources.codex_pack_";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Список встроенных паков (id отсортированы для стабильного UI).</summary>
    public static IReadOnlyList<PackInfo> Builtin()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var result = new List<PackInfo>();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json"))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                var pack = JsonSerializer.Deserialize<PackFile>(reader.ReadToEnd(), JsonOpts);
                if (pack is not null)
                    result.Add(new PackInfo(pack.Id, pack.Title, pack.Description));
            }
            catch { /* битый пак не роняет библиотеку */ }
        }
        return result;
    }

    /// <summary>Загрузить документ пака по id. Неизвестный id — InvalidOperationException.</summary>
    public static CodexDocument Load(string id)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = Prefix + id + ".json";
        if (!assembly.GetManifestResourceNames().Contains(name))
            throw new InvalidOperationException(
                $"Встроенный пак «{id}» не найден. Доступны: {string.Join(", ", Builtin().Select(p => p.Id))}");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var pack = JsonSerializer.Deserialize<PackFile>(reader.ReadToEnd(), JsonOpts)
            ?? throw new InvalidOperationException($"Пак «{id}» повреждён.");
        return pack.Codex;
    }
}
