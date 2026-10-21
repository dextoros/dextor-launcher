using System.ComponentModel;
using System.Diagnostics;
using DexTor.Qemu;
using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// Exercises <see cref="QemuProcessLauncher.Launch"/> — the function the session host
/// calls in production — against a real stand-in executable and real job objects.
/// </summary>
/// <remarks>
/// Nothing here substitutes process creation. Each test resolves the stand-in the same
/// way the runtime is resolved, builds the real QEMU argument list, and lets the
/// launcher call <c>CreateProcessW</c>.
/// </remarks>
public sealed class QemuProcessLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dextor-launch-" + Guid.NewGuid().ToString("N"));
    private readonly QemuStandIn _standIn;
    private readonly string _systemImage;
    private readonly string _dataImage;

    public QemuProcessLauncherTests()
    {
        var runtime = Path.Combine(_root, "runtime");
        _standIn = QemuStandIn.Install(runtime, Path.Combine(runtime, "qemu-manifest.json"));
        _systemImage = Path.Combine(_root, "system.img");
        _dataImage = Path.Combine(_root, "data.img");
        File.WriteAllText(_systemImage, "system");
        File.WriteAllText(_dataImage, new string('0', 4096));
    }

    private QemuLaunchPlan Plan() => _standIn.BuildPlan(_systemImage, _dataImage);

    [Fact]
    public void TheChildIsAMemberOfTheIntendedJobAtTheFirstObservablePoint()
    {
        using var job = QemuJobObject.CreateKillOnClose();
        using var process = QemuProcessLauncher.Launch(Plan(), job);

        // The launcher itself refuses to hand back a process that is not in this job, so
        // reaching here at all is part of the assertion. Membership is then re-checked
        // against this job specifically, not against "some job" — the test runner's own
        // job would satisfy that and prove nothing.
        QemuStandIn.WaitUntil(() => File.Exists(_standIn.StartedPath)).Should().BeTrue();
        job.ActiveProcessCount().Should().BeGreaterThan(0);

        _standIn.RequestShutdown();
        process.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();
    }

    [Fact]
    public void TheChildIsAlreadyInTheJobWhenTheSessionHostDiesImmediatelyAfterCreation()
    {
        // Stands in for the session host dying between creating QEMU and any managed
        // startup work: no handshake, no stream reads, nothing but closing the job. With
        // Process.Start followed by AssignProcessToJobObject this was the orphaning
        // window; with creation-time assignment there is nothing to orphan.
        int processId;
        using (var job = QemuJobObject.CreateKillOnClose())
        {
            using var process = QemuProcessLauncher.Launch(Plan(), job);
            processId = process.Id;

            // The barrier proves the child really ran rather than being a handle that
            // never executed, so the kill below is a kill of a live process.
            QemuStandIn.WaitUntil(() => File.Exists(_standIn.StartedPath)).Should().BeTrue();
        }

        QemuStandIn.WaitUntil(() => ProcessHasTerminated(processId)).Should().BeTrue(
            "closing the job must terminate a child that was never handed on");
    }

    [Fact]
    public void ClosingTheJobAfterANormalStartTerminatesTheChild()
    {
        int processId;
        using (var job = QemuJobObject.CreateKillOnClose())
        {
            using var process = QemuProcessLauncher.Launch(Plan(), job);
            processId = process.Id;
            QemuStandIn.WaitUntil(() => File.Exists(_standIn.StartedPath)).Should().BeTrue();
            process.StandardOutput.ReadLine().Should().Contain("QMP", "the stand-in speaks the handshake");
        }

        QemuStandIn.WaitUntil(() => ProcessHasTerminated(processId)).Should().BeTrue();
    }

    [Fact]
    public void DescendantsOfTheChildStayContained()
    {
        _standIn.Configure(spawnDescendant: true);
        int descendantId;
        using (var job = QemuJobObject.CreateKillOnClose())
        {
            using var process = QemuProcessLauncher.Launch(Plan(), job);
            QemuStandIn.WaitUntil(() => File.Exists(_standIn.DescendantPidPath)).Should().BeTrue();
            descendantId = _standIn.ReadDescendantProcessId();

            // Two members: the stand-in and the process it started after it was already
            // a member, which inherits the job.
            QemuStandIn.WaitUntil(() => job.ActiveProcessCount() >= 2).Should().BeTrue();
        }

        QemuStandIn.WaitUntil(() => ProcessHasTerminated(descendantId)).Should().BeTrue(
            "a descendant must not survive the job that contains its parent");
    }

    [Fact]
    public void NoObserverHandleKeepsTheJobAliveAfterTheLauncherReleasesIt()
    {
        // Kill-on-close is kill-on-*last*-handle-close, so a test that kept its own job
        // handle open would prove nothing. The job here is disposed while the only other
        // reference is the child, which must not hold one.
        var job = QemuJobObject.CreateKillOnClose();
        var process = QemuProcessLauncher.Launch(Plan(), job);
        var processId = process.Id;
        QemuStandIn.WaitUntil(() => File.Exists(_standIn.StartedPath)).Should().BeTrue();

        job.Dispose();

        QemuStandIn.WaitUntil(() => ProcessHasTerminated(processId)).Should().BeTrue(
            "the child must not have inherited a handle to its own job");
        process.Dispose();
    }

    [Fact]
    public void TheChildInheritsItsStandardStreamsAndNothingElse()
    {
        using var job = QemuJobObject.CreateKillOnClose();
        using var process = QemuProcessLauncher.Launch(Plan(), job);

        // Inherited stdio: the handshake arrives over the pipe.
        process.StandardOutput.ReadLine().Should().Contain("QMP");

        // Not inherited: the job handle. If the child held one, disposing ours would not
        // be the last close and the process would survive - which the previous test
        // covers - and the child would also be able to keep its own job alive here.
        _standIn.RequestShutdown();
        process.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();
        // The stand-in's console host is a job member too and leaves a moment after the
        // stand-in; what matters is that the job empties, not that it is empty at once.
        QemuStandIn.WaitUntil(() => job.ActiveProcessCount() == 0).Should().BeTrue("no session writer may outlive the stand-in");
    }

    [Fact]
    public async Task AfterACleanExitTheJobEmptiesWithinTheGraceTheFinalizerAllows()
    {
        using var job = QemuJobObject.CreateKillOnClose();
        using var process = QemuProcessLauncher.Launch(Plan(), job);
        process.StandardOutput.ReadLine().Should().Contain("QMP");

        _standIn.RequestShutdown();
        process.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();

        // This is the finalizer's own wait: a clean exit must never look like a process
        // that outlived QEMU, however late the console host leaves.
        (await SessionGeneration.WaitForDrainAsync(job.ActiveProcessCount, SessionFinalizer.JobDrainGrace))
            .Should().Be(0u);
    }

    [Fact]
    public async Task DrainReturnsAsSoonAsTheJobIsEmpty()
    {
        var counts = new Queue<uint>([1u, 1u, 0u]);
        var pauses = 0;

        var remaining = await SessionGeneration.WaitForDrainAsync(
            () => counts.Dequeue(), TimeSpan.FromMinutes(1), () => { pauses++; return Task.CompletedTask; });

        remaining.Should().Be(0u);
        pauses.Should().Be(2);
    }

    [Fact]
    public async Task AProcessThatOutlivesTheGraceIsStillReported()
    {
        var remaining = await SessionGeneration.WaitForDrainAsync(
            () => 1u, TimeSpan.FromMilliseconds(60), () => Task.Delay(10));

        remaining.Should().Be(1u, "a real descendant must still be escalated");
    }
    [Fact]
    public void ArgumentsWorkingDirectoryAndEnvironmentReachTheChildUnchanged()
    {
        var plan = Plan();
        using var job = QemuJobObject.CreateKillOnClose();
        using var process = QemuProcessLauncher.Launch(plan, job);
        QemuStandIn.WaitUntil(() => File.Exists(_standIn.ReportPath)).Should().BeTrue();

        var report = _standIn.ReadReport();

        report.Arguments.Should().Equal(plan.Arguments, "argument boundaries must survive quoting");
        report.Arguments.Should().Contain("virtio-blk-pci,drive=dextor-data,serial=DEXTOR_DATA,iothread=dataio");
        report.Executable.Should().Be(Path.GetFullPath(_standIn.ExecutablePath));
        report.WorkingDirectory.Should().Be(Path.GetDirectoryName(Path.GetFullPath(_standIn.ExecutablePath)));

        // The parent environment is inherited, exactly as UseShellExecute=false did.
        report.EnvironmentMarker.Should().Be(Environment.GetEnvironmentVariable("DEXTOR_TEST_MARKER"));

        _standIn.RequestShutdown();
        process.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();
    }

    [Fact]
    public void TheChildExitCodeIsObservedThroughTheOriginalProcessHandle()
    {
        _standIn.Configure(mode: "exit", exitCode: 42);
        using var job = QemuJobObject.CreateKillOnClose();
        using var process = QemuProcessLauncher.Launch(Plan(), job);

        process.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();

        // Read after exit from the handle CreateProcessW returned, so the answer cannot
        // come from a process that merely reused the id.
        process.ExitCode.Should().Be(42);
        process.HasExited.Should().BeTrue();
    }

    [Fact]
    public void AMissingExecutableFailsWithoutLeavingAnything()
    {
        var plan = new QemuLaunchPlan(
            Path.Combine(_root, "runtime", "does-not-exist.exe"),
            ["-no-reboot"]);
        using var job = QemuJobObject.CreateKillOnClose();

        var action = () => QemuProcessLauncher.Launch(plan, job);

        action.Should().Throw<Win32Exception>();
        job.ActiveProcessCount().Should().Be(0, "a failed creation must leave the job empty");
    }

    [Fact]
    public void ANonExecutableFileFailsClosedRatherThanStartingUnjobbed()
    {
        // A runtime that passed validation but is not a real image: the launch must fail
        // outright. There is no Process.Start fallback to reach for.
        var broken = Path.Combine(_root, "broken.exe");
        File.WriteAllText(broken, "not a portable executable");
        using var job = QemuJobObject.CreateKillOnClose();

        var action = () => QemuProcessLauncher.Launch(new QemuLaunchPlan(broken, ["-no-reboot"]), job);

        action.Should().Throw<Win32Exception>();
        job.ActiveProcessCount().Should().Be(0);
    }

    [Fact]
    public void ADisposedJobFailsTheLaunchInsteadOfStartingOutsideIt()
    {
        // Stands in for attribute setup that cannot succeed: the job named in
        // PROC_THREAD_ATTRIBUTE_JOB_LIST is gone. An unjobbed QEMU is precisely what must
        // not happen, so this is a failure, not a fallback.
        var job = QemuJobObject.CreateKillOnClose();
        job.Dispose();

        var action = () => QemuProcessLauncher.Launch(Plan(), job);

        action.Should().Throw<Win32Exception>();
    }

    [Fact]
    public void CancellationBeforeCreationStartsNothing()
    {
        using var job = QemuJobObject.CreateKillOnClose();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => QemuProcessLauncher.Launch(Plan(), job, cancellation.Token);

        action.Should().Throw<OperationCanceledException>();
        job.ActiveProcessCount().Should().Be(0, "no child may exist when creation was never attempted");
        File.Exists(_standIn.StartedPath).Should().BeFalse();
    }

    private static bool ProcessHasTerminated(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A stand-in may still hold the directory; the temp folder is disposable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
