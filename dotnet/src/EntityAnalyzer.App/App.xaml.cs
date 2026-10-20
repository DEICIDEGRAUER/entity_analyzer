using System.IO;
using System.Windows;
using EntityAnalyzer.App.Services;
using EntityAnalyzer.Core.Logging;

namespace EntityAnalyzer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Структурированный лог: <профиль>\logs\app-YYYYMMDD.log,
        // профиль — %APPDATA%\EntityAnalyzer либо ENTITYANALYZER_DATA_DIR.
        var logDir = Path.Combine(EntityAnalyzer.Core.Persistence.ProfilePaths.DirectoryPath, "logs");
        Log.Init(logDir, LogLevel.Debug);
        Log.Info("app", "Запуск Entity Analyzer ({0})", typeof(App).Assembly.GetName().Version?.ToString() ?? "?");

        // Item 14: тема оформления и пользовательские цвета ветвей из настроек.
        // Применяем до создания окна, чтобы не было вспышки светлой темы.
        try
        {
            ThemeManager.Apply(SettingsService.Load());
        }
        catch (Exception themeEx)
        {
            Log.Error("theme", themeEx); // тема не должна ломать старт
        }

        // R2-16 / R1-15: самопроверка релиза — первый запуск новой версии гоняет
        // встроенный мини-корпус со сверкой эталона. Тихий успех — только лог;
        // провал — видимое предупреждение (молчать о сломанном релизе нельзя).
        // Прогон идёт во временном каталоге: пользовательские данные не трогаются.
        try
        {
            var settings = SettingsService.Load();
            if (!string.Equals(settings.LastSelfCheckVersion, EntityAnalyzer.Core.About.Version, StringComparison.Ordinal))
            {
                var result = EntityAnalyzer.Core.Analysis.SelfCheck.Run();
                if (result.Passed)
                {
                    settings.LastSelfCheckVersion = EntityAnalyzer.Core.About.Version;
                    SettingsService.Save(settings);
                    Log.Info("selfcheck", "Самопроверка релиза пройдена ({0:0} мс)", result.DurationMs);
                }
                else
                {
                    Log.Error("selfcheck", new InvalidOperationException(
                        "Самопроверка релиза провалена: " + string.Join("; ", result.Problems)));
                    MessageBox.Show(
                        "Самопроверка релиза НЕ пройдена:\n" +
                        string.Join("\n", result.Problems.Take(5)) +
                        "\n\nРезультаты анализа могут быть недостоверны.\nПодробности в журнале: " + logDir,
                        "Entity Analyzer", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception selfCheckEx)
        {
            // Самопроверка не должна ломать старт приложения.
            Log.Error("selfcheck", selfCheckEx);
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("ui", args.Exception);
            MessageBox.Show(
                "Непредвиденная ошибка:\n" + args.Exception.Message +
                "\n\nПодробности в журнале: " + logDir,
                "Entity Analyzer", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("app", "Завершение работы");
        Log.Flush();
        base.OnExit(e);
    }
}
