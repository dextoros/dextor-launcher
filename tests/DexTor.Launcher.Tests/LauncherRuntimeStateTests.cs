using System;
using System.IO;
using System.Reflection;

using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class LauncherRuntimeStateTests
{
    [Fact]
    public void SetPrepareRebootHintWritesStateFile()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"dextor-launcher-state-set-{Guid.NewGuid():N}.txt");
        var originalPath = Environment.GetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", tempPath);
            InvokeClear();

            InvokeSet();
            InvokeHasHint().Should().BeTrue();

            File.Exists(tempPath).Should().BeTrue();
            File.ReadAllText(tempPath).Should().Be("prepare-reboot");
        }
        finally
        {
            InvokeClear();
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", originalPath);
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void ClearPrepareRebootHintRemovesExistingHint()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"dextor-launcher-state-clear-{Guid.NewGuid():N}.txt");
        var originalPath = Environment.GetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", tempPath);
            InvokeSet();

            InvokeHasHint().Should().BeTrue();
            InvokeClear();

            InvokeHasHint().Should().BeFalse();
            File.Exists(tempPath).Should().BeFalse();
        }
        finally
        {
            InvokeClear();
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", originalPath);
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void HasPrepareRebootHintReturnsFalseWhenFileIsMissing()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"dextor-launcher-state-missing-{Guid.NewGuid():N}.txt");
        var originalPath = Environment.GetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", tempPath);
            InvokeClear();

            InvokeHasHint().Should().BeFalse();
            File.Exists(tempPath).Should().BeFalse();
        }
        finally
        {
            InvokeClear();
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", originalPath);
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void ThePickedLanguageIsKeptBesideTheRebootHint()
    {
        WithStatePath(path =>
        {
            LauncherRuntimeState.ReadLanguage().Should().BeNull("nothing was picked yet");

            LauncherRuntimeState.SaveLanguage("pt-br");
            LauncherRuntimeState.ReadLanguage().Should().Be("pt-BR");
            LauncherRuntimeState.ResolveStartLanguage().Should().Be("pt-BR");

            // The hint and the language live side by side; clearing one leaves the other.
            LauncherRuntimeState.SetPrepareRebootHint();
            LauncherRuntimeState.SaveLanguage("ru");
            LauncherRuntimeState.HasPrepareRebootHint().Should().BeTrue();
            LauncherRuntimeState.ReadLanguage().Should().Be("ru");
            LauncherRuntimeState.ClearPrepareRebootHint();
            LauncherRuntimeState.HasPrepareRebootHint().Should().BeFalse();
            LauncherRuntimeState.ReadLanguage().Should().Be("ru");
            File.ReadAllText(path).Should().Be("language=ru");
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("language=xx")]
    [InlineData("language=")]
    [InlineData("prepare-reboot")]
    public void WithoutAUsableStoredLanguageTheStartFollowsWindows(string? content)
    {
        WithStatePath(path =>
        {
            if (content is not null) File.WriteAllText(path, content);

            LauncherRuntimeState.ReadLanguage().Should().BeNull();
            LauncherRuntimeState.ResolveStartLanguage().Should().Be(DexTor.Application.Localizer.ResolveLanguage());
        });
    }

    [Fact]
    public void AnUnknownLanguageIsNotStored()
    {
        WithStatePath(path =>
        {
            LauncherRuntimeState.SaveLanguage("xx");
            File.Exists(path).Should().BeFalse();
        });
    }

    [Fact]
    public void AStateFileFromAnEarlierBuildIsStillReadAsTheHint()
    {
        WithStatePath(path =>
        {
            File.WriteAllText(path, "prepare-reboot");
            LauncherRuntimeState.HasPrepareRebootHint().Should().BeTrue();
            LauncherRuntimeState.SaveLanguage("de");
            File.ReadAllText(path).Should().Be("prepare-reboot\nlanguage=de");
        });
    }

    private static void WithStatePath(Action<string> test)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"dextor-launcher-state-language-{Guid.NewGuid():N}.txt");
        var originalPath = Environment.GetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", tempPath);
            test(tempPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", originalPath);
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static void InvokeSet()
    {
        var type = GetRuntimeStateType();
        var method = type.GetMethod("SetPrepareRebootHint", BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();
        method!.Invoke(null, null);
    }

    private static void InvokeClear()
    {
        var type = GetRuntimeStateType();
        var method = type.GetMethod("ClearPrepareRebootHint", BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();
        method!.Invoke(null, null);
    }

    private static bool InvokeHasHint()
    {
        var type = GetRuntimeStateType();
        var method = type.GetMethod("HasPrepareRebootHint", BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();
        return (bool)method!.Invoke(null, null)!;
    }

    private static Type GetRuntimeStateType()
    {
        var type = typeof(Program).Assembly.GetType("DexTor.Launcher.LauncherRuntimeState", throwOnError: false);
        type.Should().NotBeNull();
        return type!;
    }
}
