using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DexTor.SessionHost;

/// <summary>
/// Binds the QEMU process tree to the lifetime of the process that owns the DexTor VM
/// session.
/// </summary>
/// <remarks>
/// Session ownership is a named mutex, and Windows releases a mutex the moment its
/// owning process dies. Without this job object, a session host that crashes would
/// free the device while its QEMU child kept writing to <c>data.img</c> — the next
/// launcher would acquire ownership legitimately and start a second writer on the same
/// image. <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> makes the two facts inseparable:
/// the kernel terminates the job when the last handle to it closes, and the only handle
/// is the one held here, so ownership and the writer end together.
/// <para>
/// The job handle is created without a <c>SECURITY_ATTRIBUTES</c> block, so
/// <c>bInheritHandle</c> is FALSE. That matters twice over: QEMU is started with handle
/// inheritance enabled for the redirected stdio pipes, and an inherited job handle
/// would keep the job alive inside the very process the job is meant to kill.
/// </para>
/// <para>
/// The job is configured before any child exists and is handed to
/// <see cref="QemuProcessLauncher"/>, which names it in
/// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c> so the child is a member from its first
/// instruction. Nothing assigns a process to this job after the fact.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class QemuJobObject : IDisposable
{
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly SafeJobHandle _handle;

    private QemuJobObject(SafeJobHandle handle) => _handle = handle;

    /// <summary>
    /// The job to name in <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>. Not owned by the caller.
    /// </summary>
    internal SafeJobHandle Handle => _handle;

    /// <summary>
    /// Creates a job whose closure terminates every process still assigned to it.
    /// </summary>
    /// <exception cref="Win32Exception">The job could not be created or configured.</exception>
    public static QemuJobObject CreateKillOnClose()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The DexTor session job object could not be created.");
        }

        var information = default(JobObjectExtendedLimitInformationData);
        information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        var size = (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationData>();
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref information, size))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "The DexTor session job object could not be configured.");
        }

        return new QemuJobObject(handle);
    }

    /// <summary>
    /// True when <paramref name="process"/> is a member of <em>this</em> job, as opposed
    /// to a member of some job.
    /// </summary>
    /// <remarks>
    /// Passing the job handle makes <c>IsProcessInJob</c> answer about one specific job.
    /// With a null job it answers "is it in any job at all", which a build agent's own
    /// job object would satisfy and which would prove nothing here.
    /// </remarks>
    public bool Contains(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!IsProcessInJob(process, _handle, out var member))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Membership of the DexTor session job object could not be determined.");
        }

        return member;
    }

    /// <summary>
    /// How many processes the job still contains. Zero is the only state in which every
    /// session writer is known to have exited.
    /// </summary>
    public uint ActiveProcessCount()
    {
        var information = default(JobObjectBasicAccountingInformationData);
        var size = (uint)Marshal.SizeOf<JobObjectBasicAccountingInformationData>();
        if (!QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, ref information, size, out _))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The DexTor session job object could not be queried.");
        }

        return information.ActiveProcesses;
    }

    /// <summary>
    /// Terminates every process in the job. This is how the whole QEMU tree is killed:
    /// the job is the tree's definition, so nothing can escape by re-parenting.
    /// </summary>
    public void Terminate(uint exitCode)
    {
        if (!TerminateJobObject(_handle, exitCode))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The DexTor session job object could not be terminated.");
        }
    }

    /// <summary>
    /// Closes the job, terminating every process still assigned to it.
    /// </summary>
    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationData
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCountersData
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformationData BasicLimitInformation;
        public IoCountersData IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformationData
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformationData information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        ref JobObjectBasicAccountingInformationData information,
        uint informationLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(
        SafeProcessHandle process,
        SafeJobHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    internal sealed partial class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);
    }
}
