using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DexTor.Qemu;
using Microsoft.Win32.SafeHandles;

namespace DexTor.SessionHost;

/// <summary>
/// Starts QEMU already inside the session job object.
/// </summary>
/// <remarks>
/// <c>Process.Start</c> followed by <c>AssignProcessToJobObject</c> leaves a window in
/// which QEMU is running and belongs to no job. A session host that dies inside it
/// releases the ownership mutex while an unjobbed writer keeps writing to
/// <c>data.img</c>, and the next launcher acquires ownership legitimately and starts a
/// second writer. <c>CREATE_SUSPENDED</c> plus a separate assignment does not close
/// this: the process object still exists outside the job, and a host that dies before
/// the assignment leaves it suspended and orphaned rather than never-created.
/// <para>
/// So the job is named at creation time, through
/// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c> on a <c>STARTUPINFOEX</c> passed to
/// <c>CreateProcessW</c> with <c>EXTENDED_STARTUPINFO_PRESENT</c>. The kernel places
/// the process in the job as part of creating it, so QEMU never executes an instruction
/// outside the job. The attribute needs Windows 8 or later; DexTor's supported baseline
/// is Windows 11 (10.0.22000+), checked by <c>HostPreflight</c>.
/// </para>
/// <para>
/// There is no <c>Process.Start</c> fallback. If the job cannot be configured, the
/// attribute list cannot be built or creation-time assignment fails, the launch fails:
/// an unjobbed QEMU is the condition this type exists to prevent.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class QemuProcessLauncher
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartFUseStdHandles = 0x00000100;
    private const uint StartFUseShowWindow = 0x00000001;
    private const ushort ShowHidden = 0;
    private const int HandleFlagInherit = 0x00000001;

    /// <summary>
    /// <c>ProcThreadAttributeValue(13, thread: false, input: true, additive: false)</c>.
    /// </summary>
    private static readonly nint ProcThreadAttributeJobList = MakeInputAttribute(number: 13);

    /// <summary>
    /// <c>ProcThreadAttributeValue(2, thread: false, input: true, additive: false)</c>.
    /// </summary>
    private static readonly nint ProcThreadAttributeHandleList = MakeInputAttribute(number: 2);

    /// <summary>
    /// Creates the QEMU process as a member of <paramref name="job"/>.
    /// </summary>
    /// <exception cref="Win32Exception">Any step of process creation failed.</exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was already requested. Creation itself is not cancellable, so the
    /// check happens before anything is created rather than leaving a half-started child.
    /// </exception>
    public static QemuProcess Launch(
        QemuLaunchPlan plan,
        QemuJobObject job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(job);

        // CreateProcessW cannot be interrupted once entered, so the only honest place to
        // observe cancellation is before a child can exist.
        cancellationToken.ThrowIfCancellationRequested();

        var workingDirectory = Path.GetDirectoryName(plan.ExecutablePath) is { Length: > 0 } directory
            ? directory
            : AppContext.BaseDirectory;

        // The server ends stay in this process and are explicitly non-inheritable; only
        // the client ends are marked inheritable and named in the handle list.
        var standardInput = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var standardOutput = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var standardError = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        try
        {
            return CreateProcess(plan, job, workingDirectory, standardInput, standardOutput, standardError);
        }
        catch
        {
            standardInput.Dispose();
            standardOutput.Dispose();
            standardError.Dispose();
            throw;
        }
    }

    private static QemuProcess CreateProcess(
        QemuLaunchPlan plan,
        QemuJobObject job,
        string workingDirectory,
        AnonymousPipeServerStream standardInput,
        AnonymousPipeServerStream standardOutput,
        AnonymousPipeServerStream standardError)
    {
        var childInput = standardInput.ClientSafePipeHandle;
        var childOutput = standardOutput.ClientSafePipeHandle;
        var childError = standardError.ClientSafePipeHandle;

        // AnonymousPipeServerStream already sets HANDLE_FLAG_INHERIT on the client ends.
        // Asserting it is cheap and makes the inheritance contract explicit rather than
        // an assumption about another type's implementation.
        EnsureInheritable(childInput);
        EnsureInheritable(childOutput);
        EnsureInheritable(childError);

        // Native memory rather than pinned arrays: these buffers must stay at a fixed
        // address from UpdateProcThreadAttribute until DeleteProcThreadAttributeList,
        // which spans the CreateProcessW call.
        var attributeList = IntPtr.Zero;
        var jobList = IntPtr.Zero;
        var handleList = IntPtr.Zero;
        var commandLine = BuildCommandLine(plan).ToCharArray();
        SafeProcessHandle? processHandle = null;

        try
        {
            attributeList = InitializeAttributeList(attributeCount: 2);

            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, job.Handle.DangerousGetHandle());
            UpdateAttribute(attributeList, ProcThreadAttributeJobList, jobList, IntPtr.Size, "job list");

            // Exactly the three stdio handles the child needs. Everything else this
            // process holds — the job handle, the session ownership mutex, the server
            // ends of these pipes, unrelated host handles — is excluded, whatever its
            // own inheritable flag says.
            handleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleList, 0 * IntPtr.Size, childInput.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, 1 * IntPtr.Size, childOutput.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, 2 * IntPtr.Size, childError.DangerousGetHandle());
            UpdateAttribute(attributeList, ProcThreadAttributeHandleList, handleList, IntPtr.Size * 3, "handle list");

            var startupInfo = default(StartupInfoExData);
            startupInfo.StartupInfo.cb = (uint)Marshal.SizeOf<StartupInfoExData>();
            startupInfo.StartupInfo.dwFlags = StartFUseStdHandles | StartFUseShowWindow;
            // Hidden from its first moment: SDL obeys this on its first ShowWindow, so no
            // black window flashes up before the guest draws. QemuWindowManager shows it,
            // maximized, once the guest has a picture.
            startupInfo.StartupInfo.wShowWindow = ShowHidden;
            startupInfo.StartupInfo.hStdInput = childInput.DangerousGetHandle();
            startupInfo.StartupInfo.hStdOutput = childOutput.DangerousGetHandle();
            startupInfo.StartupInfo.hStdError = childError.DangerousGetHandle();
            startupInfo.lpAttributeList = attributeList;

            var created = CreateProcessW(
                plan.ExecutablePath,
                ref Unsafe.As<char, ushort>(ref commandLine[0]),
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: true,
                CreateNoWindow | ExtendedStartupInfoPresent,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInformation);

            // Read the error before any cleanup below can overwrite it.
            var error = created ? 0 : Marshal.GetLastPInvokeError();

            // The SafeHandle must own the process handle before anything else can throw,
            // or a failure here would leak a live process with no way to reach it.
            if (created)
            {
                processHandle = new SafeProcessHandle(processInformation.hProcess, ownsHandle: true);
                CloseHandle(processInformation.hThread);
            }

            GC.KeepAlive(job);
            GC.KeepAlive(standardInput);
            GC.KeepAlive(standardOutput);
            GC.KeepAlive(standardError);

            if (!created)
            {
                throw new Win32Exception(error, $"QEMU could not be started from {plan.ExecutablePath}.");
            }

            return Adopt(job, processInformation.dwProcessId, processHandle!, standardInput, standardOutput, standardError);
        }
        catch
        {
            // A process that was created but could not be handed over is still a writer.
            // It is inside the job by construction, so terminating the job is what makes
            // it gone, and the handle stays open until that is confirmed.
            if (processHandle is not null)
            {
                ContainFailedHandover(job, processHandle);
            }

            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (jobList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(jobList);
            }

            if (handleList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(handleList);
            }

            Array.Clear(commandLine);
        }
    }

    private static QemuProcess Adopt(
        QemuJobObject job,
        uint processId,
        SafeProcessHandle processHandle,
        AnonymousPipeServerStream standardInput,
        AnonymousPipeServerStream standardOutput,
        AnonymousPipeServerStream standardError)
    {
        // The invariant this type exists for, asserted against the one job we intended
        // rather than against "some job".
        if (!job.Contains(processHandle))
        {
            throw new Win32Exception(
                0,
                "QEMU started outside the DexTor session job object and was terminated.");
        }

        // Our copies of the child ends must go, or the read pipes never report end of
        // stream when QEMU exits and exit detection would hang on the streams.
        standardInput.DisposeLocalCopyOfClientHandle();
        standardOutput.DisposeLocalCopyOfClientHandle();
        standardError.DisposeLocalCopyOfClientHandle();

        return new QemuProcess(
            (int)processId,
            processHandle,
            job,
            new StreamWriter(standardInput, new UTF8Encoding(false)) { AutoFlush = true },
            new StreamReader(standardOutput, new UTF8Encoding(false)),
            new StreamReader(standardError, new UTF8Encoding(false)));
    }

    /// <summary>
    /// Terminates a child that was created but could not be handed to a
    /// <see cref="QemuProcess"/>, and waits for that to be true before returning.
    /// </summary>
    private static void ContainFailedHandover(QemuJobObject job, SafeProcessHandle processHandle)
    {
        try
        {
            job.Terminate(1);
        }
        catch (Win32Exception)
        {
            // Fall through to the wait: the caller learns from the original failure, and
            // the process handle below is what decides whether the writer is really gone.
        }

        _ = QemuProcess.WaitForHandle(processHandle, TimeSpan.FromSeconds(10));
        processHandle.Dispose();
    }

    private static IntPtr InitializeAttributeList(int attributeCount)
    {
        var size = IntPtr.Zero;
        if (InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size))
        {
            // Documented to fail with ERROR_INSUFFICIENT_BUFFER on the sizing call.
            throw new Win32Exception(0, "The DexTor process attribute list could not be sized.");
        }

        var error = Marshal.GetLastPInvokeError();
        const int errorInsufficientBuffer = 122;
        if (error != errorInsufficientBuffer)
        {
            throw new Win32Exception(error, "The DexTor process attribute list could not be sized.");
        }

        var list = Marshal.AllocHGlobal(size);
        if (InitializeProcThreadAttributeList(list, attributeCount, 0, ref size))
        {
            return list;
        }

        error = Marshal.GetLastPInvokeError();
        Marshal.FreeHGlobal(list);
        throw new Win32Exception(error, "The DexTor process attribute list could not be created.");
    }

    private static void UpdateAttribute(IntPtr attributeList, nint attribute, IntPtr value, int size, string description)
    {
        if (!UpdateProcThreadAttribute(attributeList, 0, attribute, value, (IntPtr)size, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"The DexTor process {description} attribute could not be set.");
        }
    }

    private static void EnsureInheritable(SafeHandle handle)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "A DexTor standard stream handle could not be made inheritable.");
        }
    }

    /// <summary>
    /// Builds the <c>lpCommandLine</c> string.
    /// </summary>
    /// <remarks>
    /// <c>lpApplicationName</c> carries the executable, so argv[0] here is only what the
    /// child reads back as its own name; the quoting rules still apply to it. Every
    /// argument is quoted by the CRT's documented rules, which is what
    /// <c>ProcessStartInfo.ArgumentList</c> did before, so spaces, Unicode, embedded
    /// quotes and trailing backslashes survive unchanged.
    /// </remarks>
    internal static string BuildCommandLine(QemuLaunchPlan plan)
    {
        var builder = new StringBuilder();
        AppendArgument(builder, plan.ExecutablePath);
        foreach (var argument in plan.Arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        // CreateProcessW may write into this buffer, so it is never shared and always
        // null-terminated by the char[] copy the caller makes.
        return builder.Append('\0').ToString();
    }

    private static void AppendArgument(StringBuilder builder, string argument)
    {
        builder.Append('"');
        for (var index = 0; index < argument.Length; index++)
        {
            var backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                backslashes++;
                index++;
            }

            if (index == argument.Length)
            {
                // Trailing backslashes precede the closing quote, so each must be doubled
                // or the quote would be escaped and the argument boundary would move.
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[index] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[index]);
            }
        }

        builder.Append('"');
    }

    /// <summary>
    /// The <c>ProcThreadAttributeValue</c> macro for a process-level input attribute:
    /// the attribute number in the low 16 bits, plus <c>PROC_THREAD_ATTRIBUTE_INPUT</c>.
    /// </summary>
    private static nint MakeInputAttribute(int number)
    {
        const int attributeNumberMask = 0x0000FFFF;
        const int attributeInput = 0x00020000;
        return (nint)((number & attributeNumberMask) | attributeInput);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoData
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoExData
    {
        public StartupInfoData StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformationData
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string applicationName,
        ref ushort commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoExData startupInfo,
        out ProcessInformationData processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref IntPtr size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        nint attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr attributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(SafeHandle handle, int mask, int flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
