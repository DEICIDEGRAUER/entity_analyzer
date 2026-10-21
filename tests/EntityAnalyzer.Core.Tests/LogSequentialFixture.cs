using System;
using System.IO;
using Xunit;
using EntityAnalyzer.Core.Semantics;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Фикстура для коллекции LogSequential: изолирует пользовательские данные
/// (semantic_patterns.json, codex.json, settings.json) во временном каталоге,
/// чтобы тесты не зависели от состояния машины и не загрязняли профиль пользователя.
/// </summary>
public class LogSequentialFixture : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _oldSemanticDir;
    private readonly string? _oldAppDataDir;

    public LogSequentialFixture()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "EntityAnalyzer_Tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        // Изолируем семантические паттерны через AsyncLocal (приоритет над env var)
        SemanticPatternStore.SetTestUserDir(_tempDir);

        // Также устанавливаем env var для обратной совместимости
        _oldSemanticDir = Environment.GetEnvironmentVariable("EA_SEMANTIC_USER_DIR");
        Environment.SetEnvironmentVariable("EA_SEMANTIC_USER_DIR", _tempDir);

        // Изолируем настройки приложения (AppData\EntityAnalyzer)
        _oldAppDataDir = Environment.GetEnvironmentVariable("APPDATA");
        var testAppData = Path.Combine(_tempDir, "AppData");
        Directory.CreateDirectory(testAppData);
        Environment.SetEnvironmentVariable("APPDATA", testAppData);
    }

    public void Dispose()
    {
        // Восстанавливаем AsyncLocal
        SemanticPatternStore.SetTestUserDir(null);

        // Восстанавливаем env vars
        if (_oldSemanticDir is not null)
            Environment.SetEnvironmentVariable("EA_SEMANTIC_USER_DIR", _oldSemanticDir);
        else
            Environment.SetEnvironmentVariable("EA_SEMANTIC_USER_DIR", null);

        if (_oldAppDataDir is not null)
            Environment.SetEnvironmentVariable("APPDATA", _oldAppDataDir);
        else
            Environment.SetEnvironmentVariable("APPDATA", null);

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { /* игнорируем ошибки очистки */ }
    }
}

/// <summary>
/// Определение коллекции LogSequential с фикстурой LogSequentialFixture.
/// DisableParallelization = true гарантирует последовательный запуск тестов в коллекции.
/// </summary>
[CollectionDefinition("LogSequential", DisableParallelization = true)]
public class LogSequentialCollection : ICollectionFixture<LogSequentialFixture>
{
    // Этот класс не содержит кода — он только связывает имя коллекции с фикстурой.
}