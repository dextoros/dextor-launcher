using System.Reflection;
using System.Collections.Generic;

using DexTor.Application;

using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void BuildCreateErrorMessageIncludesSolutionAndSafety()
    {
        var message = InvokeBuildCreateErrorMessage(
            "launcher.create.error.release",
            "release download failed",
            "launcher.create.error.safety.possibly_modified");

        message.Should().Contain("Could not create DexTor USB on D:\\USB. release download failed");
        message.Should().Contain("Solution: Fix the release source/path or retry the network flow.");
        message.Should().Contain("Data safety: Data may have been partially written. Keep the drive disconnected until recovery is done.");
    }

    [Fact]
    public void BuildCreateErrorMessageOmitsSafetyWhenMissing()
    {
        var message = InvokeBuildCreateErrorMessage("launcher.create.error.simulation", "simulation failed", null);

        message.Should().Contain("Solution: Check USB health, drive permissions, and choose another writable drive before retrying.");
        message.Should().NotContain("Data safety:");
    }

    [Fact]
    public void BuildCreateErrorMessageFallsBackToDefaultSolutionWhenErrorKeyMissing()
    {
        var message = InvokeBuildCreateErrorMessage(null, "unexpected error", null);

        message.Should().Contain("Solution: Check USB health, drive permissions, and choose another writable drive before retrying.");
    }

    private static string InvokeBuildCreateErrorMessage(
        string? errorMessageKey,
        string errorMessage,
        string? errorSafetyMessageKey)
    {
        var method = typeof(Program).GetMethod("BuildCreateErrorMessage", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return (string)method!.Invoke(null, new object?[]
        {
            new TestLocalizer(),
            @"D:\USB",
            errorMessageKey,
            errorMessage,
            errorSafetyMessageKey
        })!;
    }

    private sealed class TestLocalizer : ILocalizer
    {
        public string T(string key, IReadOnlyDictionary<string, string>? args = null)
        {
            var text = key switch
            {
                "launcher.action.create.error" => "Could not create DexTor USB on {drive}. {error}",
                "launcher.solution.label" => "Solution:",
                "launcher.action.create.solution" => "Check USB health, drive permissions, and choose another writable drive before retrying.",
                "launcher.action.create.solution.release" => "Fix the release source/path or retry the network flow.",
                "launcher.create.error.safety.label" => "Data safety:",
                "launcher.create.error.safety.possibly_modified" => "Data may have been partially written. Keep the drive disconnected until recovery is done.",
                _ => key
            };

            if (args is null)
            {
                return text;
            }

            foreach (var arg in args)
            {
                text = text.Replace("{" + arg.Key + "}", arg.Value);
            }

            return text;
        }

        public bool TryGetLanguage(out string language)
        {
            language = "en";
            return true;
        }
    }
}
