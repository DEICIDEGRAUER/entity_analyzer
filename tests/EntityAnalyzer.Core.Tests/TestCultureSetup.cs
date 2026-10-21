using System.Globalization;
using System.Runtime.CompilerServices;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-16 «Матричный CI»: культура тестового прогона задаётся переменной
/// окружения EA_TEST_CULTURE (ru-RU / en-US / tr-TR). Ловит ловушку
/// турецкого I и прочие культуро-зависимые сбои до продакшена.
/// Без переменной поведение прежнее (культура машины).
/// </summary>
internal static class TestCultureSetup
{
    [ModuleInitializer]
    internal static void ApplyCultureFromEnvironment()
    {
        var name = Environment.GetEnvironmentVariable("EA_TEST_CULTURE");
        if (string.IsNullOrWhiteSpace(name)) return;

        var culture = CultureInfo.GetCultureInfo(name);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
