using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using EntityAnalyzer.Core.Text;

namespace EntityAnalyzer.Cli;

/// <summary>
/// R1-13 (Roslyn): загрузчик пользовательских скриптов-препроцессоров (.csx).
/// Конвенция: последним выражением скрипт возвращает экземпляр ITextPreprocessor.
/// Скрипты — доверенный ввод того же уровня, что и конфигурация: они исполняют
/// произвольный код; для недоверенных источников использовать не следует.
/// Загруженный препроцессор регистрируется в реестре под своим Name и
/// включается в прогон как обычное имя (--preprocess / Processing.Preprocessors).
/// </summary>
public static class ScriptPreprocessorLoader
{
    public sealed record LoadResult(bool IsOk, ITextPreprocessor? Preprocessor, string? Error)
    {
        public static LoadResult Ok(ITextPreprocessor pp) => new(true, pp, null);
        public static LoadResult Fail(string error) => new(false, null, error);
    }

    private static readonly ScriptOptions Options = ScriptOptions.Default
        .WithReferences(
            typeof(ITextPreprocessor).Assembly,          // EntityAnalyzer.Core (контракт)
            typeof(object).Assembly,                     // System.Private.CoreLib
            typeof(System.Linq.Enumerable).Assembly)     // System.Linq
        .WithImports("System", "System.Linq", "System.Collections.Generic",
                     "EntityAnalyzer.Core.Text");

    /// <summary>Загрузить скрипт из файла и зарегистрировать его Name в реестре.</summary>
    public static async Task<LoadResult> LoadAsync(string path)
    {
        if (!File.Exists(path))
            return LoadResult.Fail($"Скрипт не найден: {path}");

        string code;
        try { code = File.ReadAllText(path); }
        catch (Exception ex) { return LoadResult.Fail($"Не удалось прочитать {path}: {ex.Message}"); }

        ITextPreprocessor instance;
        try
        {
            instance = await CSharpScript.EvaluateAsync<ITextPreprocessor>(code, Options);
        }
        catch (CompilationErrorException ex)
        {
            // Первая диагностика — самая полезная; полный список остаётся в выводе.
            var first = ex.Diagnostics.FirstOrDefault();
            return LoadResult.Fail(
                $"Ошибка компиляции {Path.GetFileName(path)}{(first is null ? "" : $": {first}")}");
        }
        catch (Exception ex)
        {
            return LoadResult.Fail($"Ошибка выполнения {Path.GetFileName(path)}: {ex.Message}");
        }

        if (instance is null)
            return LoadResult.Fail(
                $"Скрипт {Path.GetFileName(path)} не вернул экземпляр ITextPreprocessor. " +
                "Последним выражением файла должен быть конструктор класса препроцессора, например «new MyPp()».");

        if (string.IsNullOrWhiteSpace(instance.Name))
            return LoadResult.Fail($"Препроцессор из {Path.GetFileName(path)} вернул пустое имя.");

        if (!TextPreprocessorRegistry.Register(instance.Name, () => instance))
            return LoadResult.Fail(
                $"Имя «{instance.Name}» уже занято (встроенный препроцессор или другой скрипт). " +
                "Переименуйте препроцессор в скрипте.");

        Console.WriteLine($"  Скрипт-препроцессор загружен: {instance.Name} ({Path.GetFileName(path)})");
        return LoadResult.Ok(instance);
    }
}
