using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DexTor.Qemu;

/// <summary>
/// Identifies one DexTor device's VM session for cross-process ownership.
/// </summary>
/// <remarks>
/// The identity is a SHA-256 fingerprint over the canonical identity of the device's
/// <c>data.img</c> — see <see cref="IPersistentStorageCanonicalizer"/> — never a
/// constant shared by all DexTor USBs. Hashing is required, not cosmetic:
/// <list type="number">
/// <item>Windows kernel object names treat <c>\</c> as a namespace separator, so a raw
/// path such as <c>\\?\Volume{…}\DEXTOR\storage\data.img</c> is not a legal mutex
/// name.</item>
/// <item>A kernel object name is visible to other processes on the machine; a
/// fingerprint keeps the operator's host path out of it.</item>
/// <item>It bounds the name to the 260-character limit.</item>
/// </list>
/// The fingerprint is one-way, so the resulting name is safe to log.
/// </remarks>
public sealed record DexTorSessionIdentity(string Fingerprint)
{
    private const string NamePrefix = "DexTor.Session.";

    /// <summary>
    /// The one kernel object name every DexTor launcher uses for this device.
    /// </summary>
    /// <remarks>
    /// Always <c>Global\</c>. The protected resource is a physical USB shared by the
    /// whole machine, so a per-logon-session name would let two Windows accounts each
    /// believe they own it. There is deliberately no second name to fall back to:
    /// two ownership domains for one <c>data.img</c> is the failure this lock exists
    /// to prevent.
    /// </remarks>
    public string MutexName => $"Global\\{NamePrefix}{Fingerprint}";

    /// <summary>
    /// Derives the session identity from an already-canonical storage identity.
    /// </summary>
    public static DexTorSessionIdentity ForCanonicalStorage(string canonicalStorageId)
    {
        if (string.IsNullOrWhiteSpace(canonicalStorageId))
        {
            throw new ArgumentException("A canonical storage identity is required.", nameof(canonicalStorageId));
        }

        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"dextor-session-v2|{canonicalStorageId}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new DexTorSessionIdentity(Convert.ToHexString(digest)[..32]);
    }

    /// <summary>
    /// Derives the session identity for a validated persistent storage image.
    /// </summary>
    /// <param name="dataImagePath">Host path of <c>DEXTOR\storage\data.img</c>.</param>
    /// <param name="canonicalizer">
    /// Resolver for the file's canonical identity. Defaults to the Windows volume-GUID
    /// resolver.
    /// </param>
    /// <remarks>
    /// The device's provisioning <c>requestId</c> deliberately does <em>not</em> take
    /// part. It is a correlation identifier for one provisioning run, it is optional in
    /// the device metadata, and a launcher that cannot read it would key the same
    /// <c>data.img</c> to a different mutex — which is precisely a second writer. The
    /// storage identity alone decides ownership.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static DexTorSessionIdentity ForDataImage(
        string dataImagePath,
        IPersistentStorageCanonicalizer? canonicalizer = null)
    {
        if (string.IsNullOrWhiteSpace(dataImagePath))
        {
            throw new ArgumentException("A persistent storage image path is required.", nameof(dataImagePath));
        }

        var resolver = canonicalizer ?? WindowsVolumeStorageCanonicalizer.Default;
        return ForCanonicalStorage(resolver.Canonicalize(dataImagePath));
    }
}

/// <summary>
/// Why an ownership claim ended the way it did. Contention, access denial and an
/// unusable namespace are different facts and lead to different operator guidance.
/// </summary>
public enum SessionOwnershipOutcome
{
    /// <summary>The claim could not be evaluated; ownership must be assumed unsafe.</summary>
    Unavailable = 0,

    /// <summary>Ownership was taken; no previous owner existed.</summary>
    Acquired,

    /// <summary>
    /// Ownership was taken from an owner that died without releasing it. The device is
    /// free, but the previous session ended unclean.
    /// </summary>
    RecoveredFromAbandonedSession,

    /// <summary>Another live process on this machine owns the device.</summary>
    HeldByAnotherProcess,

    /// <summary>This process already owns the device.</summary>
    HeldByThisProcess,

    /// <summary>
    /// The kernel object exists but this account may not open it — another Windows
    /// account on this machine owns the device.
    /// </summary>
    DeniedToThisAccount
}

/// <summary>Result of one ownership attempt.</summary>
public sealed record SessionOwnershipClaim(
    SessionOwnershipOutcome Outcome,
    DexTorSessionLock? Lock,
    string? Diagnostic)
{
    /// <summary>True when this process now owns the device's session.</summary>
    public bool IsGranted => Lock is not null;
}

/// <summary>
/// Durable cross-process ownership of one DexTor device's VM session, held for the
/// entire QEMU lifetime.
/// </summary>
/// <remarks>
/// A named mutex is used deliberately: Windows releases it automatically when the
/// owning process dies, so a crashed launcher cannot permanently strand the device as
/// "already running". The successor observes
/// <see cref="RecoveredFromAbandonedSession"/>, which reports an unclean predecessor
/// and never by itself licenses skipping the writer and recovery checks.
/// <para>
/// Ownership is always claimed in the <c>Global\</c> namespace and never downgraded.
/// Creating a named mutex there does not require <c>SeCreateGlobalPrivilege</c>: that
/// check applies to section (file-mapping) objects, not to mutexes, events,
/// semaphores or waitable timers. If the global object still cannot be claimed, the
/// launcher fails closed rather than silently moving to a namespace where a second
/// launcher would not see it.
/// </para>
/// <para>
/// This lock holds no handle on <c>data.img</c>. QEMU must be able to open that file
/// itself, so the exclusive open stays a pre-launch probe while this lock provides the
/// ownership that outlives it.
/// </para>
/// </remarks>
public sealed class DexTorSessionLock : IDisposable
{
    /// <summary>
    /// Devices currently claimed by this process, keyed by fingerprint.
    /// </summary>
    /// <remarks>
    /// A Windows mutex is re-entrant for the thread that already owns it, so the kernel
    /// object by itself would happily let one launcher claim the same device twice from
    /// the same thread. This registry closes that hole in-process; the mutex closes it
    /// across processes. Both are required.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, byte> ProcessClaims = new(StringComparer.Ordinal);

    private MutexOwnerThread? _owner;

    private DexTorSessionLock(
        MutexOwnerThread owner,
        DexTorSessionIdentity identity,
        bool recoveredFromAbandonedSession)
    {
        _owner = owner;
        Identity = identity;
        RecoveredFromAbandonedSession = recoveredFromAbandonedSession;
    }

    public DexTorSessionIdentity Identity { get; }

    /// <summary>
    /// True when the previous owner terminated without releasing ownership. The device
    /// is free, but the previous session did not shut down cleanly: callers must still
    /// run every writer and recovery check, and must not record the storage as clean.
    /// </summary>
    public bool RecoveredFromAbandonedSession { get; }

    /// <summary>False once <see cref="Dispose"/> has released ownership.</summary>
    public bool IsHeld => Volatile.Read(ref _owner) is not null;

    /// <summary>
    /// Attempts to take ownership of this device's VM session without blocking, and
    /// reports exactly why when it could not.
    /// </summary>
    public static SessionOwnershipClaim TryAcquire(DexTorSessionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // Reserve in-process before touching the kernel object; see ProcessClaims.
        if (!ProcessClaims.TryAdd(identity.Fingerprint, 0))
        {
            return new SessionOwnershipClaim(SessionOwnershipOutcome.HeldByThisProcess, null, null);
        }

        MutexOwnerThread owner;
        try
        {
            owner = MutexOwnerThread.Start(identity.MutexName);
        }
        catch
        {
            ProcessClaims.TryRemove(identity.Fingerprint, out _);
            throw;
        }

        switch (owner.Outcome)
        {
            case SessionOwnershipOutcome.Acquired:
            case SessionOwnershipOutcome.RecoveredFromAbandonedSession:
                var sessionLock = new DexTorSessionLock(
                    owner,
                    identity,
                    owner.Outcome == SessionOwnershipOutcome.RecoveredFromAbandonedSession);
                return new SessionOwnershipClaim(owner.Outcome, sessionLock, null);

            default:
                // Nothing is held: reap the owner thread and give the reservation back.
                var outcome = owner.Outcome;
                var diagnostic = owner.Diagnostic;
                owner.Dispose();
                ProcessClaims.TryRemove(identity.Fingerprint, out _);
                return new SessionOwnershipClaim(outcome, null, diagnostic);
        }
    }

    /// <summary>
    /// Takes ownership, or throws carrying an actionable, outcome-specific message.
    /// </summary>
    public static DexTorSessionLock Acquire(DexTorSessionIdentity identity)
    {
        var claim = TryAcquire(identity);
        return claim.Lock ?? throw new VmPrelaunchException(DescribeRefusal(claim));
    }

    /// <summary>
    /// The operator-facing explanation for a refused claim. Public so callers that use
    /// <see cref="TryAcquire"/> report the same wording as <see cref="Acquire"/>.
    /// </summary>
    public static string DescribeRefusal(SessionOwnershipClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return claim.Outcome switch
        {
            SessionOwnershipOutcome.HeldByAnotherProcess or SessionOwnershipOutcome.HeldByThisProcess =>
                "DexTor is already running from this device.",
            SessionOwnershipOutcome.DeniedToThisAccount =>
                "DexTor cannot take ownership of this device's session because another Windows "
                + "account on this computer already holds it. Close DexTor in that account, or "
                + "sign it out, and try again.",
            _ =>
                "DexTor cannot establish machine-wide ownership of this device's session"
                + (string.IsNullOrWhiteSpace(claim.Diagnostic) ? "." : $": {claim.Diagnostic}.")
                + " Starting now could let a second virtual machine write to the same storage, "
                + "so startup was stopped."
        };
    }

    /// <summary>
    /// Releases ownership. Idempotent, so every failure path can call it
    /// unconditionally, and safe from any thread.
    /// </summary>
    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
        {
            return;
        }

        try
        {
            owner.Dispose();
        }
        finally
        {
            ProcessClaims.TryRemove(Identity.Fingerprint, out _);
        }
    }

    /// <summary>
    /// Holds the named mutex on a thread of its own for the lifetime of the claim.
    /// </summary>
    /// <remarks>
    /// Win32 mutex ownership belongs to a <em>thread</em>, and <c>ReleaseMutex</c> from
    /// any other thread fails. Acquisition and release here happen across
    /// <c>StartAsync</c>, <c>StopAsync</c>, a <c>Process.Exited</c> callback, a
    /// cancellation and disposal — none of which guarantee a common thread, because an
    /// <c>await</c> resumes wherever the scheduler puts it. Releasing from the wrong
    /// thread would leave the kernel object owned by a still-live thread pool thread,
    /// and the next launcher would see the device as permanently in use.
    /// <para>
    /// So one dedicated thread claims the mutex, parks until release is requested, and
    /// releases it. Callers on any thread simply signal it. The thread is a background
    /// thread: if the process dies instead, Windows marks the mutex abandoned, which is
    /// the behaviour a crashed launcher is supposed to produce.
    /// </para>
    /// </remarks>
    private sealed class MutexOwnerThread : IDisposable
    {
        private readonly string _name;
        private readonly ManualResetEventSlim _claimSettled = new(false);
        private readonly ManualResetEventSlim _releaseRequested = new(false);
        private Thread? _thread;

        private MutexOwnerThread(string name) => _name = name;

        public SessionOwnershipOutcome Outcome { get; private set; } = SessionOwnershipOutcome.Unavailable;

        public string? Diagnostic { get; private set; }

        public static MutexOwnerThread Start(string name)
        {
            var owner = new MutexOwnerThread(name);
            var thread = new Thread(owner.Run)
            {
                IsBackground = true,
                Name = "DexTor session ownership"
            };
            owner._thread = thread;
            thread.Start();
            owner._claimSettled.Wait();
            return owner;
        }

        /// <summary>
        /// Releases the mutex on the thread that owns it and waits for that thread to
        /// finish. Callable from any thread, and idempotent.
        /// </summary>
        public void Dispose()
        {
            var thread = Interlocked.Exchange(ref _thread, null);
            if (thread is null)
            {
                return;
            }

            _releaseRequested.Set();

            // The owner thread only has ReleaseMutex and CloseHandle left to do, neither
            // of which blocks. Joining makes the release observable to every other thread
            // and lets the wait handles be disposed without racing the owner.
            thread.Join();
            _claimSettled.Dispose();
            _releaseRequested.Dispose();
        }

        private void Run()
        {
            Mutex? mutex = null;
            try
            {
                mutex = OpenMutex();
                if (mutex is null)
                {
                    return;
                }

                var abandoned = false;
                bool owned;
                try
                {
                    owned = mutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner died without releasing; ownership transfers to
                    // us, and the predecessor's session is unclean by definition.
                    owned = true;
                    abandoned = true;
                }

                if (!owned)
                {
                    // A live process owns it. Close our handle only; theirs keeps the
                    // object alive.
                    Outcome = SessionOwnershipOutcome.HeldByAnotherProcess;
                    return;
                }

                Outcome = abandoned
                    ? SessionOwnershipOutcome.RecoveredFromAbandonedSession
                    : SessionOwnershipOutcome.Acquired;
                _claimSettled.Set();

                _releaseRequested.Wait();
                mutex.ReleaseMutex();
            }
            catch (Exception exception)
            {
                // Nothing on this thread may escape: an unhandled exception here would
                // kill the launcher. Recording the failure leaves the caller fail-closed,
                // which is the safe reading of "ownership could not be established".
                Outcome = SessionOwnershipOutcome.Unavailable;
                Diagnostic = exception.Message;
            }
            finally
            {
                mutex?.Dispose();
                _claimSettled.Set();
            }
        }

        private Mutex? OpenMutex()
        {
            try
            {
                // No explicit DACL: the default grants the creating account full control
                // and nothing wider. Granting Everyone full control would let any process
                // on the machine release or tamper with DexTor's ownership.
                return new Mutex(initiallyOwned: false, _name, out _);
            }
            catch (UnauthorizedAccessException exception)
            {
                // The object exists but this token may not open it: another Windows
                // account on this machine created it.
                Outcome = SessionOwnershipOutcome.DeniedToThisAccount;
                Diagnostic = exception.Message;
                return null;
            }
            catch (Exception exception) when (exception is IOException
                                              or NotSupportedException
                                              or WaitHandleCannotBeOpenedException)
            {
                Outcome = SessionOwnershipOutcome.Unavailable;
                Diagnostic = exception.Message;
                return null;
            }
        }
    }
}
