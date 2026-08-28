using System.IO;

namespace EntityAnalyzer.Core.Logging;

/// <summary>
/// R2-23 «Журнал доступа к данным»: аудит-строки для каждой операции
/// чтения/записи пользовательских данных (%APPDATA%, корпус). Для
/// институциональных установок и чувствительных корпусов — доказуемая
/// картина обращений без внешних зависимостей.
/// </summary>
public static class Audit
{
    /// <summary>Запись пользовательского файла (settings.json, codex.json, …).</summary>
    public static void Write(string kind, string path) =>
        Log.Info("audit", $"write {kind} {path}");

    /// <summary>Чтение пользовательского файла.</summary>
    public static void Read(string kind, string path) =>
        Log.Info("audit", $"read {kind} {path}");

    /// <summary>
    /// Скан корпуса — агрегированно одной строкой на прогон: пофайловый
    /// аудит заспамил бы журнал на больших корпусах, при этом каждое
    /// чтение файла и так попадает в отчёты прогона.
    /// </summary>
    public static void CorpusScan(string dir, int files) =>
        Log.Info("audit", $"scan corpus {dir} files={files}");
}
