using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// The job object on its own: creation, the limits it carries, and the queries the
/// session host relies on. Whether QEMU is <em>in</em> the job is settled at creation
/// time and is covered by <see cref="QemuProcessLauncherTests"/>.
/// </summary>
public sealed class QemuJobObjectTests
{
    [Fact]
    public void ANewJobIsEmpty()
    {
        using var job = QemuJobObject.CreateKillOnClose();

        job.ActiveProcessCount().Should().Be(0);
    }

    [Fact]
    public void ThisProcessIsNotAMemberOfASessionJobItDidNotJoin()
    {
        // Membership is asked about one specific job. Asking "is it in any job" would be
        // answered yes by the test runner's own job and would prove nothing.
        using var job = QemuJobObject.CreateKillOnClose();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        using var handle = new Microsoft.Win32.SafeHandles.SafeProcessHandle(self.Handle, ownsHandle: false);

        job.Contains(handle).Should().BeFalse();
    }

    [Fact]
    public void DisposingAnEmptyJobTwiceIsSafe()
    {
        var job = QemuJobObject.CreateKillOnClose();

        job.Dispose();
        var action = job.Dispose;

        action.Should().NotThrow();
    }

    [Fact]
    public void QueryingADisposedJobFailsLoudlyRatherThanReportingZeroWriters()
    {
        // "No members" and "cannot tell" must not look the same: the first says every
        // session writer has exited, and only one of them is evidence.
        var job = QemuJobObject.CreateKillOnClose();
        job.Dispose();

        var action = () => job.ActiveProcessCount();

        action.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void TerminatingADisposedJobFailsLoudly()
    {
        var job = QemuJobObject.CreateKillOnClose();
        job.Dispose();

        var action = () => job.Terminate(1);

        action.Should().Throw<ObjectDisposedException>();
    }
}
