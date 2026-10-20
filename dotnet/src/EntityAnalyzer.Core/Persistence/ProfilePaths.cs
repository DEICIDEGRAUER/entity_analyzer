using System.IO;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// Каталог пользовательского профиля приложения (настройки, кодекс, журналы,
/// словари, дневник — все локальные данные).
///
/// По умолчанию — %APPDATA%\EntityAnalyzer. Переопределяется переменной среды
/// <c>ENTITYANALYZER_DATA_DIR</c>: все пути профиля строятся отсюда, поэтому
/// тесты, GUI-смоук и верификация могут работать на изолированном каталоге и
/// не касаться данных пользователя.
/// </summary>
public static class ProfilePaths
{
    /// <summary>Переменная среды, переопределяющая каталог профиля.</summary>
    public const string OverrideEnvVar = "ENTITYANALYZER_DATA_DIR";

    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EntityAnalyzer");

    /// <summary>Действующий каталог профиля (override или %APPDATA%\EntityAnalyzer).</summary>
    public static string DirectoryPath =>
        Resolve(Environment.GetEnvironmentVariable(OverrideEnvVar), DefaultDirectory);

    /// <summary>
    /// Выбор каталога профиля по значению переменной override. Некорректное
    /// значение (пустое или неразбираемый путь) молча откатывается к каталогу
    /// по умолчанию — приложение не должно падать из-за переменной среды.
    /// </summary>
    public static string Resolve(string? overrideDirectory, string defaultDirectory)
    {
        if (string.IsNullOrWhiteSpace(overrideDirectory)) return defaultDirectory;
        try
        {
            return Path.GetFullPath(overrideDirectory.Trim());
        }
        catch
        {
            return defaultDirectory;
        }
    }
}
