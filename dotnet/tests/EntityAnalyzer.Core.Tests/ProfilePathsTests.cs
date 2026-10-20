using System;
using System.IO;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Изоляция профиля: ENTITYANALYZER_DATA_DIR должен переносить все пути
/// пользовательских данных в указанный каталог (герметичный смоук/тесты),
/// а некорректное значение — молча откатываться к %APPDATA%\EntityAnalyzer.
/// </summary>
public class ProfilePathsTests
{
    [Fact]
    public void Resolve_uses_default_when_override_empty()
    {
        Assert.Equal(@"C:\x", ProfilePaths.Resolve(null, @"C:\x"));
        Assert.Equal(@"C:\x", ProfilePaths.Resolve("", @"C:\x"));
        Assert.Equal(@"C:\x", ProfilePaths.Resolve("   ", @"C:\x"));
    }

    [Fact]
    public void Resolve_uses_override_when_set()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ea_profile_test");
        Assert.Equal(Path.GetFullPath(dir), ProfilePaths.Resolve(dir, @"C:\x"));
        Assert.Equal(Path.GetFullPath(dir), ProfilePaths.Resolve("  " + dir + "  ", @"C:\x"));
    }

    [Fact]
    public void Resolve_falls_back_on_invalid_override()
    {
        Assert.Equal(@"C:\x", ProfilePaths.Resolve("bad\0dir", @"C:\x"));
    }

    [Fact]
    public void Environment_override_changes_directory()
    {
        var old = Environment.GetEnvironmentVariable(ProfilePaths.OverrideEnvVar);
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ea_profile_test_env");
            Environment.SetEnvironmentVariable(ProfilePaths.OverrideEnvVar, dir);
            Assert.Equal(Path.GetFullPath(dir), ProfilePaths.DirectoryPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProfilePaths.OverrideEnvVar, old);
        }
    }

    [Fact]
    public void Default_directory_is_appdata_entityanalyzer()
    {
        var old = Environment.GetEnvironmentVariable(ProfilePaths.OverrideEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ProfilePaths.OverrideEnvVar, null);
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EntityAnalyzer");
            Assert.Equal(expected, ProfilePaths.DirectoryPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProfilePaths.OverrideEnvVar, old);
        }
    }
}
