using System.Reflection;
using System.IO;
using System.Windows.Forms;

using DexTor.SessionHost;
using DexTor.Application;

using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class ProgramSessionStateTests
{
    [Fact]
    public void ActiveUsbAvailabilityRequiresMetadataAndPrivateStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "dextor-active-usb-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "DEXTOR", "metadata"));
            Directory.CreateDirectory(Path.Combine(root, "DEXTOR", "storage"));
            File.WriteAllText(Path.Combine(root, "DEXTOR", "metadata", "device.json"), "{}");

            Program.IsActiveDexTorUsbAvailable(root).Should().BeFalse();
            File.WriteAllBytes(Path.Combine(root, "DEXTOR", "storage", "data.img"), [0]);
            Program.IsActiveDexTorUsbAvailable(root).Should().BeTrue();
            Directory.Delete(root, true);
            Program.IsActiveDexTorUsbAvailable(root).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("STOP", true)]
    [InlineData("GUEST_PANICKED", true)]
    [InlineData("RESET", false)]
    [InlineData("RESUME", false)]
    public void SessionHostRecognizesTerminalQmpEvents(string eventName, bool expected)
    {
        SessionHostService.IsUnexpectedStopEvent(eventName).Should().Be(expected);
    }

    [Theory]
    [InlineData(SessionState.Idle, false, false)]
    [InlineData(SessionState.StartingQemu, true, true)]
    [InlineData(SessionState.Running, true, false)]
    [InlineData(SessionState.Stopping, false, true)]
    [InlineData(SessionState.StorageLocking, true, true)]
    [InlineData(SessionState.SafeToEject, false, false)]
    public void ResolveLauncherSessionFlagsMapsStateCorrectly(
        SessionState state,
        bool expectedIsRunning,
        bool expectedHasUncleanSession)
    {
        var (isSessionRunning, hasUncleanSession) = InvokeResolveLauncherSessionFlags(state);

        isSessionRunning.Should().Be(expectedIsRunning);
        hasUncleanSession.Should().Be(expectedHasUncleanSession);
    }

    [Fact]
    public void ResolveStartActionMessageReturnsUnreadyForUncleanSession()
    {
        var message = InvokeResolveStartActionMessage(new LauncherContext
        {
            HasUncleanSession = true
        });

        message.MessageKey.Should().Be("launcher.action.start.unready");
        message.Icon.Should().Be(MessageBoxIcon.Warning);
        message.SolutionKey.Should().Be("launcher.action.start.solution.unready");
    }

    [Fact]
    public void ResolveStartActionMessageReturnsNoUsbWhenNotConnected()
    {
        var message = InvokeResolveStartActionMessage(new LauncherContext
        {
            HasUncleanSession = false,
            DextorRootPath = null
        });

        message.MessageKey.Should().Be("launcher.action.start.no_usb");
        message.Icon.Should().Be(MessageBoxIcon.Information);
        message.SolutionKey.Should().Be("launcher.action.start.solution.no_usb");
    }

    [Fact]
    public void ResolveStartActionMessageReturnsPromptWhenReady()
    {
        var message = InvokeResolveStartActionMessage(new LauncherContext
        {
            HasUncleanSession = false,
            DextorRootPath = @"D:\USB"
        });

        message.MessageKey.Should().Be("launcher.action.start.prompt");
        message.Icon.Should().Be(MessageBoxIcon.Information);
        message.SolutionKey.Should().BeNullOrWhiteSpace();
    }

    [Fact]
    public void ResolveRecoverMessageReturnsUnavailableWithoutUncleanSession()
    {
        var message = InvokeResolveRecoverMessage(new LauncherContext
        {
            HasUncleanSession = false,
            DextorRootPath = @"D:\USB"
        });

        message.MessageKey.Should().Be("launcher.action.unavailable");
        message.Icon.Should().Be(MessageBoxIcon.Information);
        message.SolutionKey.Should().Be("launcher.action.recover.solution.none");
    }

    [Fact]
    public void ResolveRecoverMessageReturnsPromptForUncleanSession()
    {
        var message = InvokeResolveRecoverMessage(new LauncherContext
        {
            HasUncleanSession = true,
            DextorRootPath = @"D:\USB"
        });

        message.MessageKey.Should().Be("launcher.action.recover.prompt");
        message.Icon.Should().Be(MessageBoxIcon.Warning);
        message.SolutionKey.Should().Be("launcher.action.recover.solution");
    }

    [Fact]
    public void ResolveOpenMessageReturnsPromptForAvailableDexTorUsb()
    {
        var message = InvokeResolveOpenMessage(new LauncherContext
        {
            DextorRootPath = @"D:\USB",
            IsDexTorUsb = true
        });

        message.MessageKey.Should().Be("launcher.action.open.prompt");
        message.Icon.Should().Be(MessageBoxIcon.Information);
        message.SolutionKey.Should().Be("launcher.action.open.solution");
    }

    [Fact]
    public void ResolveOpenMessageReturnsUnavailableWhenNoUsb()
    {
        var message = InvokeResolveOpenMessage(new LauncherContext());

        message.MessageKey.Should().Be("launcher.action.unavailable");
        message.Icon.Should().Be(MessageBoxIcon.Information);
        message.SolutionKey.Should().Be("launcher.action.open.solution.no_usb");
    }

    [Fact]
    public void ResolveOpenMessageReturnsUnavailableWhenPathIsPresentButNotDexTor()
    {
        var message = InvokeResolveOpenMessage(new LauncherContext
        {
            DextorRootPath = @"D:\USB",
            IsDexTorUsb = false
        });

        message.MessageKey.Should().Be("launcher.action.unavailable");
        message.Icon.Should().Be(MessageBoxIcon.Information);
        message.SolutionKey.Should().Be("launcher.action.open.solution");
    }

    [Fact]
    public void ResolveRepairMessageReturnsUnavailableWhenNoUpdateDetected()
    {
        var message = InvokeResolveRepairMessage(new LauncherContext
        {
            HasOptionalUpdate = false,
            DextorRootPath = @"D:\USB"
        });

        message.MessageKey.Should().Be("launcher.action.unavailable");
        message.Icon.Should().Be(MessageBoxIcon.Information);
        message.SolutionKey.Should().Be("launcher.action.repair.solution.none");
    }

    [Fact]
    public void ResolveRepairMessageReturnsPromptForReadyOptionalUpdate()
    {
        var message = InvokeResolveRepairMessage(new LauncherContext
        {
            HasOptionalUpdate = true,
            HasUncleanSession = false,
            DextorRootPath = @"D:\USB"
        });

        message.MessageKey.Should().Be("launcher.action.repair.prompt");
        message.Icon.Should().Be(MessageBoxIcon.Warning);
        message.SolutionKey.Should().Be("launcher.action.repair.solution");
    }

    [Fact]
    public void ResolveRepairMessageReturnsUnreadyForUncleanSession()
    {
        var message = InvokeResolveRepairMessage(new LauncherContext
        {
            HasOptionalUpdate = true,
            HasUncleanSession = true,
            DextorRootPath = @"D:\USB"
        });

        message.MessageKey.Should().Be("launcher.action.repair.unready");
        message.Icon.Should().Be(MessageBoxIcon.Warning);
        message.SolutionKey.Should().Be("launcher.action.repair.solution.unready");
    }

    [Fact]
    public void ResolveEjectMessageReturnsNoUsbWhenMissing()
    {
        var message = InvokeResolveEjectMessage(new LauncherContext
        {
            IsSessionRunning = false,
            DextorRootPath = null
        });

        message.MessageKey.Should().Be("launcher.action.eject.no_usb");
        message.Icon.Should().Be(MessageBoxIcon.Information);
        message.SolutionKey.Should().Be("launcher.action.eject.solution.no_usb");
    }

    [Fact]
    public void ResolveEjectMessageReturnsUnreadyBeforeNoUsbWhenSessionIsUnclean()
    {
        var message = InvokeResolveEjectMessage(new LauncherContext
        {
            IsSessionRunning = false,
            HasUncleanSession = true,
            DextorRootPath = null
        });

        message.MessageKey.Should().Be("launcher.action.eject.unready");
        message.Icon.Should().Be(MessageBoxIcon.Warning);
        message.SolutionKey.Should().Be("launcher.action.eject.solution.unready");
    }

    [Fact]
    public void ResolveEjectMessageReturnsRunningAndSolution()
    {
        var message = InvokeResolveEjectMessage(new LauncherContext
        {
            IsSessionRunning = true,
            DextorRootPath = @"D:\USB"
        });

        message.MessageKey.Should().Be("launcher.action.eject.running");
        message.Icon.Should().Be(MessageBoxIcon.Warning);
        message.SolutionKey.Should().Be("launcher.action.eject.solution.running");
    }

    [Fact]
    public void ResolveEjectMessageReturnsPromptForReadyState()
    {
        var message = InvokeResolveEjectMessage(new LauncherContext
        {
            IsSessionRunning = false,
            HasUncleanSession = false,
            DextorRootPath = @"D:\USB"
        });

        message.MessageKey.Should().Be("launcher.action.eject.prompt");
        message.Icon.Should().Be(MessageBoxIcon.Information);
        message.SolutionKey.Should().Be("launcher.action.eject.solution");
    }

    [Fact]
    public void ResolveRestartRequiredMessageReturnsRestartPrompt()
    {
        var message = InvokeResolveRestartRequiredMessage();

        message.MessageKey.Should().Be("launcher.action.restart_required.prompt");
        message.Icon.Should().Be(MessageBoxIcon.Warning);
        message.SolutionKey.Should().Be("launcher.action.restart_required.solution");
    }

    [Fact]
    public void HandleCreateUsbReturnsWarningWhenUsbIsConnectedButNotWritable()
    {
        var (result, messageCount, lastMessage, lastIcon) = InvokeHandleCreateUsb(
            new TestLocalizer(),
            new LauncherContext
            {
                IsUsbConnected = true,
                IsDexTorUsb = false,
                UsbRootCandidates = []
            });

        result.Should().Be(0);
        messageCount.Should().Be(1);
        lastMessage.Should().Contain("launcher.action.create.no_writable_usb");
        lastMessage.Should().Contain("Solution:");
        lastMessage.Should().Contain("launcher.action.create.no_writable_usb.solution");
        lastIcon.Should().Be(MessageBoxIcon.Warning);
    }

    [Fact]
    public void HandleCreateUsbReturnsConnectPromptWhenUsbIsNotConnected()
    {
        var (result, messageCount, lastMessage, lastIcon) = InvokeHandleCreateUsb(
            new TestLocalizer(),
            new LauncherContext
            {
                IsUsbConnected = false,
                IsDexTorUsb = false,
                UsbRootCandidates = []
            });

        result.Should().Be(0);
        messageCount.Should().Be(1);
        lastMessage.Should().Be("launcher.action.connect.prompt");
        lastIcon.Should().Be(MessageBoxIcon.Information);
    }

    [Fact]
    public void BuildActionMessageAppendsSolution()
    {
        var message = InvokeBuildActionMessage(
            "launcher.action.start.unready",
            "launcher.action.start.solution.unready");

        message.Should().Contain("Session is inconsistent. Run recover before starting DexTor.");
        message.Should().Contain("Solution:");
        message.Should().Contain("Run Check and recover, then retry Start DexTor.");
    }

    [Fact]
    public void BuildOpenDexTorResultReturnsUnavailableForMissingUsb()
    {
        var (message, icon) = InvokeBuildOpenDexTorResult(new LauncherContext());

        message.Should().Be("launcher.action.unavailable" + "\r\n\r\nSolution: launcher.action.open.solution.no_usb");
        icon.Should().Be(MessageBoxIcon.Information);
    }

    [Fact]
    public void BuildOpenDexTorResultReturnsUnavailableWithLayoutSolutionWhenPathIsNotDexTor()
    {
        var (message, icon) = InvokeBuildOpenDexTorResult(new LauncherContext
        {
            DextorRootPath = @"D:\USB",
            IsDexTorUsb = false
        });

        message.Should().Be("launcher.action.unavailable" + "\r\n\r\nSolution: launcher.action.open.solution");
        icon.Should().Be(MessageBoxIcon.Information);
    }

    [Fact]
    public void BuildOpenDexTorResultReturnsWarningForOpenFailure()
    {
        var (message, icon) = InvokeBuildOpenDexTorResult(new LauncherContext
        {
            DextorRootPath = @"C:\bad|path",
            IsDexTorUsb = true
        });

        message.Should().StartWith("launcher.action.open.prompt");
        message.Should().Contain("Solution:");
        message.Should().Contain("launcher.action.open.solution");
        message.Should().Contain("launcher.action.open.error.not_found");
        icon.Should().Be(MessageBoxIcon.Warning);
    }

    [Fact]
    public void BuildEjectActionResultReturnsNoUsbMessageWhenUsbMissing()
    {
        var (message, icon) = InvokeBuildEjectActionResult(new LauncherContext());

        message.Should().Be("launcher.action.eject.no_usb" + "\r\n\r\nSolution: launcher.action.eject.solution.no_usb");
        icon.Should().Be(MessageBoxIcon.Information);
    }

    [Fact]
    public void BuildEjectActionResultReturnsWarningForEjectFailure()
    {
        var (message, icon) = InvokeBuildEjectActionResult(new LauncherContext
        {
            DextorRootPath = @"C:\bad|path"
        });

        message.Should().StartWith("launcher.action.eject.prompt");
        message.Should().Contain("Solution: launcher.action.eject.solution");
        message.Should().Contain("launcher.action.eject.error.not_found");
        icon.Should().Be(MessageBoxIcon.Warning);
    }

    [Fact]
    public void HandleRestartRequiredCancelsWithoutRestartAttempt()
    {
        var (result, confirmCount, restartCount, actionMessageCount, _, _) = InvokeHandleRestartRequired(
            new TestLocalizer(),
            () => DialogResult.No,
            () => true);

        result.Should().Be(0);
        confirmCount.Should().Be(1);
        restartCount.Should().Be(0);
        actionMessageCount.Should().Be(0);
    }

    [Fact]
    public void HandleRestartRequiredShowsErrorMessageOnRestartFailure()
    {
        var (result, confirmCount, restartCount, actionMessageCount, lastMessage, lastIcon) = InvokeHandleRestartRequired(
            new TestLocalizer(),
            () => DialogResult.Yes,
            () => false);

        result.Should().Be(0);
        confirmCount.Should().Be(1);
        restartCount.Should().Be(1);
        actionMessageCount.Should().Be(1);
        lastMessage.Should().Contain("launcher.action.restart_required.failed");
        lastMessage.Should().Contain("Solution:");
        lastMessage.Should().Contain("launcher.action.restart_required.failed.solution");
        lastIcon.Should().Be(MessageBoxIcon.Error);
    }

    [Fact]
    public void HandlePrepareOpensFirstRequirementActionLink()
    {
        var report = new PreflightReport(
            true,
            new[]
            {
                new PreflightRequirement(
                    PreflightRequirementKind.Runtime,
                    PreflightRequirementState.Warning,
                    "preflight.runtime.title",
                    "preflight.runtime.blocker",
                    ActionTextKey: "preflight.runtime.blocker.action",
                    ActionUrl: "https://example.org/prepare"),
            });

        var openedUrlCount = 0;
        var (result, actionMessageCount, openedUrlCountResult, lastOpenedUrl, message) = InvokeHandlePrepare(
            new TestLocalizer(),
            report,
            _ => openedUrlCount++,
            out var messageForFirst,
            isCurrentUserAdmin: () => true,
            tryEnableVirtualization: () => false);

        result.Should().Be(0);
        actionMessageCount.Should().Be(1);
        openedUrlCount.Should().Be(1);
        openedUrlCountResult.Should().Be(1);
        lastOpenedUrl.Should().Be("https://example.org/prepare");
        message.Should().Contain("launcher.action.prepare.failed");
        message.Should().Contain("Solution:");
        message.Should().Contain("launcher.action.prepare.failed.solution");
    }

    [Fact]
    public void HandlePrepareDoesNotOpenEmptyActionLink()
    {
        var report = new PreflightReport(
            true,
            new[]
            {
                new PreflightRequirement(
                    PreflightRequirementKind.Runtime,
                    PreflightRequirementState.Ok,
                    "preflight.runtime.title",
                    "preflight.runtime.ok")
            });

        var openedUrlCount = 0;
        var (result, actionMessageCount, openedUrlCountResult, _, message) = InvokeHandlePrepare(
            new TestLocalizer(),
            report,
            _ => openedUrlCount++,
            out var messageForNoLink,
            isCurrentUserAdmin: () => true,
            tryEnableVirtualization: () => false);

        result.Should().Be(0);
        actionMessageCount.Should().Be(1);
        openedUrlCount.Should().Be(0);
        openedUrlCountResult.Should().Be(0);
        message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void HandlePrepareSetsPrepareRebootHint()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"dextor-launcher-state-test-{Guid.NewGuid():N}.txt");
        var originalStatePath = Environment.GetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", statePath);
            InvokeLauncherRuntimeClearHint();
            InvokeHandlePrepare(
                new TestLocalizer(),
                new PreflightReport(
                    false,
                    new[]
                    {
                        new PreflightRequirement(
                            PreflightRequirementKind.Runtime,
                            PreflightRequirementState.Warning,
                            "preflight.runtime.title",
                            "preflight.runtime.blocker",
                            ActionTextKey: "preflight.runtime.blocker.action",
                            ActionUrl: "https://example.org/prepare")
                    }),
            _ => { },
            out var messageForRebootHint,
            isCurrentUserAdmin: () => true,
            tryEnableVirtualization: () => true);

            InvokeLauncherRuntimeHasHint().Should().BeTrue();
            File.Exists(statePath).Should().BeTrue();
        }
        finally
        {
            InvokeLauncherRuntimeClearHint();
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", originalStatePath);
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public void HandlePrepareWithoutAdminFallsBackToPrepareLinks()
    {
        var openedUrlCount = 0;
        var (result, actionMessageCount, openedUrlCountResult, lastOpenedUrl, message) = InvokeHandlePrepare(
            new TestLocalizer(),
            new PreflightReport(
                true,
                new[]
                {
                    new PreflightRequirement(
                        PreflightRequirementKind.Runtime,
                        PreflightRequirementState.Ok,
                        "preflight.runtime.title",
                        "preflight.runtime.ok",
                        ActionTextKey: "preflight.runtime.blocker.action",
                        ActionUrl: "https://example.org/prepare")
            }),
            _ => openedUrlCount++,
            out var messageForNoAdmin,
            isCurrentUserAdmin: () => false);

        result.Should().Be(0);
        actionMessageCount.Should().Be(1);
        openedUrlCount.Should().Be(1);
        openedUrlCountResult.Should().Be(1);
        lastOpenedUrl.Should().Be("https://example.org/prepare");
        message.Should().Contain("launcher.action.prepare.permission");
    }

    [Fact]
    public void HandlePrepareSuccessSetsRebootHintAndDoesNotOpenLink()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"dextor-launcher-state-test-{Guid.NewGuid():N}.txt");
        var originalStatePath = Environment.GetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", statePath);
            InvokeLauncherRuntimeClearHint();

            var openedUrlCount = 0;
            var (result, actionMessageCount, openedUrlCountResult, _, message) = InvokeHandlePrepare(
                new TestLocalizer(),
                new PreflightReport(
                    true,
                    new[]
                    {
                        new PreflightRequirement(
                            PreflightRequirementKind.Runtime,
                            PreflightRequirementState.Ok,
                            "preflight.runtime.title",
                            "preflight.runtime.ok")
            }),
                _ => openedUrlCount++,
                out var messageForSuccess,
                isCurrentUserAdmin: () => true,
                tryEnableVirtualization: () => true);

            result.Should().Be(0);
            actionMessageCount.Should().Be(1);
            openedUrlCount.Should().Be(0);
            openedUrlCountResult.Should().Be(0);
            message.Should().Contain("launcher.action.prepare.success");
            message.Should().Contain("launcher.action.prepare.success.solution");
            InvokeLauncherRuntimeHasHint().Should().BeTrue();
        }
        finally
        {
            InvokeLauncherRuntimeClearHint();
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", originalStatePath);
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public void ResolveLauncherContextRequiresRebootAfterPrepareHintWhenWhpxIsNotReady()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"dextor-launcher-state-test-{Guid.NewGuid():N}.txt");
        var originalStatePath = Environment.GetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", statePath);
            InvokeLauncherRuntimeSetHint();

            var context = InvokeResolveLauncherContext(
                new PreflightReport(
                    false,
                    [
                        new PreflightRequirement(
                            PreflightRequirementKind.HostVirtualization,
                            PreflightRequirementState.Warning,
                            "preflight.whpx.title",
                            "preflight.whpx.warning")
                    ]));
            context.NeedsReboot.Should().BeTrue();
            context.IsWhpxEnabled.Should().BeFalse();
        }
        finally
        {
            InvokeLauncherRuntimeClearHint();
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", originalStatePath);
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public void ResolveLauncherContextClearsRebootHintWhenWhpxIsReady()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"dextor-launcher-state-test-{Guid.NewGuid():N}.txt");
        var originalStatePath = Environment.GetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", statePath);
            InvokeLauncherRuntimeSetHint();

            var context = InvokeResolveLauncherContext(
                new PreflightReport(
                    true,
                    [
                        new PreflightRequirement(
                            PreflightRequirementKind.HostVirtualization,
                            PreflightRequirementState.Ok,
                            "preflight.whpx.title",
                            "preflight.whpx.ok")
                    ]));
            context.NeedsReboot.Should().BeFalse();
            context.IsWhpxEnabled.Should().BeTrue();
            InvokeLauncherRuntimeHasHint().Should().BeFalse();
            File.Exists(statePath).Should().BeFalse();
        }
        finally
        {
            InvokeLauncherRuntimeClearHint();
            Environment.SetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH", originalStatePath);
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    private static (bool IsSessionRunning, bool HasUncleanSession) InvokeResolveLauncherSessionFlags(SessionState state)
    {
        var method = typeof(Program).GetMethod("ResolveLauncherSessionFlags", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return ((bool, bool))method!.Invoke(null, new object?[] { state })!;
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) InvokeResolveStartActionMessage(LauncherContext context)
    {
        var method = typeof(Program).GetMethod("ResolveStartActionMessage", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return ((string MessageKey, MessageBoxIcon Icon, string? SolutionKey))method!.Invoke(null, new object?[] { context })!;
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) InvokeResolveRecoverMessage(LauncherContext context)
    {
        var method = typeof(Program).GetMethod("ResolveRecoverMessage", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return ((string MessageKey, MessageBoxIcon Icon, string? SolutionKey))method!.Invoke(null, new object?[] { context })!;
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) InvokeResolveOpenMessage(LauncherContext context)
    {
        var method = typeof(Program).GetMethod("ResolveOpenDexTorMessage", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return ((string MessageKey, MessageBoxIcon Icon, string? SolutionKey))method!.Invoke(null, new object?[] { context })!;
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) InvokeResolveRepairMessage(LauncherContext context)
    {
        var method = typeof(Program).GetMethod("ResolveRepairAndStartMessage", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return ((string MessageKey, MessageBoxIcon Icon, string? SolutionKey))method!.Invoke(null, new object?[] { context })!;
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) InvokeResolveRestartRequiredMessage()
    {
        var method = typeof(Program).GetMethod("ResolveRestartRequiredMessage", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return ((string MessageKey, MessageBoxIcon Icon, string? SolutionKey))method!.Invoke(null, null!)!;
    }

    private static LauncherContext InvokeResolveLauncherContext(PreflightReport preflight)
    {
        var method = typeof(Program).GetMethod("ResolveLauncherContext", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return (LauncherContext)method!.Invoke(null, new object?[] { preflight })!;
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) InvokeResolveEjectMessage(LauncherContext context)
    {
        var method = typeof(Program).GetMethod("ResolveEjectMessage", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return ((string MessageKey, MessageBoxIcon Icon, string? SolutionKey))method!.Invoke(null, new object?[] { context })!;
    }

    private static (int Result, int ConfirmationCount, int RestartCount, int ActionMessageCount, string LastMessage, MessageBoxIcon LastIcon)
        InvokeHandleRestartRequired(
            ILocalizer localizer,
            Func<DialogResult> confirmSelection,
            Func<bool> restartResult)
    {
        var method = typeof(Program).GetMethod("HandleRestartRequired", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var type = typeof(Program);
        var confirmationField = type.GetField("ShowConfirmationCallback", BindingFlags.NonPublic | BindingFlags.Static);
        var restartField = type.GetField("TryRestartWindowsCallback", BindingFlags.NonPublic | BindingFlags.Static);
        var actionMessageField = type.GetField("ShowActionMessageCallback", BindingFlags.NonPublic | BindingFlags.Static);

        confirmationField.Should().NotBeNull();
        restartField.Should().NotBeNull();
        actionMessageField.Should().NotBeNull();

        var originalConfirmation = (Func<string, ILocalizer, MessageBoxButtons, DialogResult>)confirmationField!.GetValue(null)!;
        var originalRestart = (Func<bool>)restartField!.GetValue(null)!;
        var originalActionMessage = (Func<string, ILocalizer, MessageBoxIcon, int>)actionMessageField!.GetValue(null)!;
        var confirmationCount = 0;
        var restartCount = 0;
        var actionMessageCount = 0;
        var lastMessage = string.Empty;
        var lastIcon = MessageBoxIcon.None;

        try
        {
            confirmationField.SetValue(
                null,
                new Func<string, ILocalizer, MessageBoxButtons, DialogResult>((_, _, _) =>
                {
                    confirmationCount++;
                    return confirmSelection();
                }));

            restartField.SetValue(
                null,
                new Func<bool>(() =>
                {
                    restartCount++;
                    return restartResult();
                }));

            actionMessageField.SetValue(
                null,
                new Func<string, ILocalizer, MessageBoxIcon, int>((message, _, icon) =>
                {
                    actionMessageCount++;
                    lastMessage = message;
                    lastIcon = icon;
                    return 0;
                }));

            var result = (int)method!.Invoke(null, new object?[] { localizer })!;
            return (result, confirmationCount, restartCount, actionMessageCount, lastMessage, lastIcon);
        }
        finally
        {
            confirmationField.SetValue(null, originalConfirmation);
            restartField.SetValue(null, originalRestart);
            actionMessageField.SetValue(null, originalActionMessage);
        }
    }

    private static string InvokeBuildActionMessage(string messageKey, string? solutionKey)
    {
        var method = typeof(Program).GetMethod("BuildActionMessage", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        return (string)method!.Invoke(null, new object?[] { new TestLocalizer(), messageKey, solutionKey })!;
    }

    private static (string Message, MessageBoxIcon Icon) InvokeBuildOpenDexTorResult(LauncherContext context)
    {
        var method = typeof(Program).GetMethod("BuildOpenDexTorResult", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var result = method!.Invoke(null, new object?[] { context, new TestLocalizer() })!;
        return ((string Message, MessageBoxIcon Icon))result;
    }

    private static (string Message, MessageBoxIcon Icon) InvokeBuildEjectActionResult(LauncherContext context)
    {
        var method = typeof(Program).GetMethod("BuildEjectActionResult", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var result = method!.Invoke(null, new object?[] { context, new TestLocalizer() })!;
        return ((string Message, MessageBoxIcon Icon))result;
    }

    private static (int Result, int ActionMessageCount, int OpenUrlCount, string LastOpenedUrl, string MessageContent)
        InvokeHandlePrepare(
            ILocalizer localizer,
            PreflightReport report,
            Action<string> openUrlHandler,
            out string openedMessage,
            Func<bool>? isCurrentUserAdmin = null,
            Func<bool>? tryEnableVirtualization = null)
    {
        var method = typeof(Program).GetMethod("HandlePrepare", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var type = typeof(Program);
        var actionMessageField = type.GetField("ShowActionMessageCallback", BindingFlags.NonPublic | BindingFlags.Static);
        var openUrlField = type.GetField("OpenUrlCallback", BindingFlags.NonPublic | BindingFlags.Static);
        var isAdminField = type.GetField("IsCurrentUserAdminCallback", BindingFlags.NonPublic | BindingFlags.Static);
        var tryEnableVirtualizationField = type.GetField("TryEnableVirtualizationCallback", BindingFlags.NonPublic | BindingFlags.Static);
        actionMessageField.Should().NotBeNull();
        openUrlField.Should().NotBeNull();
        isAdminField.Should().NotBeNull();
        tryEnableVirtualizationField.Should().NotBeNull();

        var originalActionMessage = (Func<string, ILocalizer, MessageBoxIcon, int>)actionMessageField!.GetValue(null)!;
        var originalOpenUrl = (Action<string>)openUrlField!.GetValue(null)!;
        var originalIsAdmin = (Func<bool>)isAdminField!.GetValue(null)!;
        var originalTryEnable = (Func<bool>)tryEnableVirtualizationField!.GetValue(null)!;

        var messageCount = 0;
        var urlCount = 0;
        var localOpenedUrl = string.Empty;
        var localMessage = string.Empty;

        try
        {
            actionMessageField.SetValue(
                null,
                new Func<string, ILocalizer, MessageBoxIcon, int>((message, _, icon) =>
                {
                    messageCount++;
                    localMessage = message;
                    return 0;
                }));

            openUrlField.SetValue(
                null,
                new Action<string>(url =>
                {
                    urlCount++;
                    localOpenedUrl = url;
                    openUrlHandler(url);
                }));

            isAdminField.SetValue(null, isCurrentUserAdmin ?? ((Func<bool>)(() => true)));
            tryEnableVirtualizationField.SetValue(null, tryEnableVirtualization ?? ((Func<bool>)(() => true)));

            var result = (int)method!.Invoke(null, new object?[] { report, localizer })!;
            openedMessage = localMessage;
            return (result, messageCount, urlCount, localOpenedUrl, localMessage);
        }
        finally
        {
            actionMessageField.SetValue(null, originalActionMessage);
            openUrlField.SetValue(null, originalOpenUrl);
            isAdminField.SetValue(null, originalIsAdmin);
            tryEnableVirtualizationField.SetValue(null, originalTryEnable);
            openedMessage = localMessage;
        }
    }

    private static (int Result, int ActionMessageCount, string LastMessage, MessageBoxIcon LastIcon)
        InvokeHandleCreateUsb(
            ILocalizer localizer,
            LauncherContext context)
    {
        var method = typeof(Program).GetMethod("HandleCreateUsb", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var type = typeof(Program);
        var actionMessageField = type.GetField("ShowActionMessageCallback", BindingFlags.NonPublic | BindingFlags.Static);
        actionMessageField.Should().NotBeNull();

        var originalActionMessage = (Func<string, ILocalizer, MessageBoxIcon, int>)actionMessageField!.GetValue(null)!;
        var messageCount = 0;
        var lastMessage = string.Empty;
        var lastIcon = MessageBoxIcon.None;

        try
        {
            actionMessageField.SetValue(
                null,
                new Func<string, ILocalizer, MessageBoxIcon, int>((message, _, icon) =>
                {
                    messageCount++;
                    lastMessage = message;
                    lastIcon = icon;
                    return 0;
                }));

            var result = (int)method!.Invoke(null, new object?[] { context, localizer })!;
            return (result, messageCount, lastMessage, lastIcon);
        }
        finally
        {
            actionMessageField.SetValue(null, originalActionMessage);
        }
    }

    private static void InvokeLauncherRuntimeSetHint()
    {
        var type = GetLauncherRuntimeStateType();
        var method = type.GetMethod("SetPrepareRebootHint", BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();
        method!.Invoke(null, null);
    }

    private static void InvokeLauncherRuntimeClearHint()
    {
        var type = GetLauncherRuntimeStateType();
        var method = type.GetMethod("ClearPrepareRebootHint", BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();
        method!.Invoke(null, null);
    }

    private static bool InvokeLauncherRuntimeHasHint()
    {
        var type = GetLauncherRuntimeStateType();
        var method = type.GetMethod("HasPrepareRebootHint", BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();
        return (bool)method!.Invoke(null, null)!;
    }

    private static System.Type GetLauncherRuntimeStateType()
    {
        var type = typeof(Program).Assembly.GetType("DexTor.Launcher.LauncherRuntimeState", false);
        type.Should().NotBeNull();
        return type!;
    }

    private sealed class TestLocalizer : DexTor.Application.ILocalizer
    {
        public string T(string key, System.Collections.Generic.IReadOnlyDictionary<string, string>? args = null)
        {
            var text = key switch
            {
                "launcher.action.start.unready" => "Session is inconsistent. Run recover before starting DexTor.",
                "launcher.action.start.solution.unready" => "Run Check and recover, then retry Start DexTor.",
                "launcher.solution.label" => "Solution:",
                _ => key
            };

            if (args is null || args.Count == 0)
            {
                return text;
            }

            foreach (var pair in args)
            {
                text = text.Replace($"{{{pair.Key}}}", pair.Value);
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
