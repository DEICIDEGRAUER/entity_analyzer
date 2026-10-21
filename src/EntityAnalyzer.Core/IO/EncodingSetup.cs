using System.Runtime.CompilerServices;
using System.Text;

namespace EntityAnalyzer.Core.IO;

/// <summary>
/// Регистрирует провайдер legacy-кодировок до первого обращения к
/// <see cref="DocumentReader"/>. Без этого CP1251/CP1252 доступны только в
/// тех хостах, где провайдер случайно зарегистрировал кто-то другой (WPF,
/// тестовый раннер), а CLI/headless и изолированные тесты падают при чтении
/// старых DOC/RTF. Инициализация модуля срабатывает раньше статических полей
/// <see cref="DocumentReader"/>, поэтому порядок загрузки уже не важен.
/// </summary>
internal static class EncodingSetup
{
    // CA2255 предупреждает о module initializer в библиотеке. Здесь он нужен
    // намеренно: это support-библиотека приложения, и провайдер обязан быть
    // зарегистрирован до статических полей DocumentReader/AnalysisEngine.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#pragma warning restore CA2255
}
