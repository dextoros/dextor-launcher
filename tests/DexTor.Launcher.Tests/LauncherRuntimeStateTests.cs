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
