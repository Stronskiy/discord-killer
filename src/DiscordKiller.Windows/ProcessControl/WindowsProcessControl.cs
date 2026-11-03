using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscordKiller.Core;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("DiscordKiller.Windows.IntegrationTests")]

namespace DiscordKiller.Windows.ProcessControl;

/// <summary>Evidence for a single retained process handle. Trust must not be based on name or path alone.</summary>
internal sealed record WindowsProcessEvidence(ProcessIdentity Identity, string ImagePath, string OwnerSid, uint SessionId);

/// <summary>
/// Implementations must return promptly and require independent verification of running-image
/// authenticity before production use. Do not call blocking file trust APIs inside IsTrusted:
/// the coordinator cannot enforce a timeout before a synchronously blocking call returns.
/// </summary>
internal interface IWindowsProcessTrustPolicy
{
    bool IsReady { get; }
    bool IsTrusted(WindowsProcessEvidence evidence);
}

/// <summary>Default policy: no target is trusted and scans cannot report that Discord is absent.</summary>
internal sealed class UnverifiedWindowsProcessPolicy : IWindowsProcessTrustPolicy
{
    public bool IsReady => false;
    public bool IsTrusted(WindowsProcessEvidence evidence) => false;
}

/// <summary>Windows uptime explicitly includes sleep and hibernation.</summary>
internal sealed class WindowsElapsedClock : IMonotonicClock, IMonotonicDelay
{
    public long ElapsedMilliseconds => checked((long)Native.GetTickCount64());

    public async Task WaitUntilAsync(long deadlineMilliseconds, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining = deadlineMilliseconds - ElapsedMilliseconds;
            if (remaining <= 0) return;
            await Task.Delay((int)Math.Min(remaining, 250), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Experimental discovery of the stable desktop process name only. Enumerating bare PIDs
/// cannot establish a snapshot: a PID may be reused before OpenProcess. Production discovery
/// therefore cannot claim completeness or be connected to a coordinator. Default policy stays
/// unavailable until snapshot identity and running-image authenticity are verified on Windows.
/// Failed access or a failed trust decision is reported as an unverified candidate.
/// </summary>
internal sealed class WindowsProcessCatalog : IProcessCatalog
{
    private readonly IWindowsProcessTrustPolicy _policy;
    private readonly Func<IEnumerable<int>> _enumerateIds;
    private readonly bool _enumerationCompletenessProven;

    public WindowsProcessCatalog() : this(new UnverifiedWindowsProcessPolicy()) { }

    public WindowsProcessCatalog(IWindowsProcessTrustPolicy policy) : this(policy, EnumerateDiscordIds, false) { }

    /// <summary>
    /// Test-only injection: a true completeness flag is valid only for a controlled fixture
    /// whose policy binds every candidate to its original retained handle and creation time.
    /// Never set true for the production bare-PID enumerator.
    /// </summary>
    internal WindowsProcessCatalog(IWindowsProcessTrustPolicy policy, Func<IEnumerable<int>> enumerateIds, bool enumerationCompletenessProven)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _enumerateIds = enumerateIds ?? throw new ArgumentNullException(nameof(enumerateIds));
        _enumerationCompletenessProven = enumerationCompletenessProven;
    }

    public Task<ProcessScan> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_policy.IsReady)
            return Task.FromResult(new ProcessScan(false, Array.Empty<ProcessCandidate>()));

        var candidates = new List<ProcessCandidate>();
        try
        {
            foreach (int id in _enumerateIds().Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                WindowsProcessTarget? target = null;
                try
                {
                    target = WindowsProcessTarget.Open(id, _policy);
                    if (target.VerifyIdentityAsync(cancellationToken).GetAwaiter().GetResult())
                    {
                        candidates.Add(new ProcessCandidate(target.Identity, target));
                        target = null; // ownership transfers to the scan
                    }
                    else
                        candidates.Add(new ProcessCandidate(new ProcessIdentity(id, 0), null));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
                {
                    candidates.Add(new ProcessCandidate(new ProcessIdentity(id, 0), null));
                }
                finally { target?.Dispose(); }
            }
            // Bare PID enumeration also cannot prevent reuse before OpenProcess, and
            // GetProcessesByName("Discord") does not prove that no other Discord
            // channel, inaccessible process or undiscovered variant is running.
            // Only injected test enumerators claim completeness in this prototype.
            return Task.FromResult(new ProcessScan(_enumerationCompletenessProven, candidates));
        }
        catch
        {
            foreach (var candidate in candidates) candidate.VerifiedTarget?.Dispose();
            throw;
        }
    }

    private static IEnumerable<int> EnumerateDiscordIds()
    {
        Process[] processes = Process.GetProcessesByName("Discord");
        try { return processes.Select(p => p.Id).ToArray(); }
        finally { foreach (Process process in processes) process.Dispose(); }
    }
}

/// <summary>
/// Uses the originally opened OS handle for termination and exit checks. This is a primitive,
/// not proof of Discord identity. Calls must never be enabled for real Discord until a policy
/// has been independently verified. Only processes in the caller's user and session are eligible.
/// This experimental primitive is NOT safe to wire to ProcessShutdownCoordinator in production: these
/// synchronous Win32 operations run before returning a Task, so coordinator deadlines cannot
/// preempt them; cancellation can race the OS calls and a late effect cannot be excluded.
/// Isolated integration tests may exercise it against their own retained child process only.
/// </summary>
internal sealed class WindowsProcessTarget : IProcessTarget
{
    private readonly SafeProcessHandle _handle;
    private readonly IWindowsProcessTrustPolicy _policy;
    private int _disposed;
    public ProcessIdentity Identity { get; }

    private WindowsProcessTarget(SafeProcessHandle handle, ProcessIdentity identity, IWindowsProcessTrustPolicy policy)
    {
        _handle = handle;
        Identity = identity;
        _policy = policy;
    }

    public static WindowsProcessTarget Open(int processId, IWindowsProcessTrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (processId <= 0 || processId == Environment.ProcessId) throw new ArgumentException("Invalid target PID.", nameof(processId));
        SafeProcessHandle handle = Native.OpenProcess(Native.QueryLimitedInformation | Native.Synchronize | Native.Terminate, false, processId);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            long started = Native.ReadStartTicks(handle);
            return new WindowsProcessTarget(handle, new ProcessIdentity(processId, started), policy);
        }
        catch { handle.Dispose(); throw; }
    }

    public Task<bool> VerifyIdentityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!_policy.IsReady || Native.HasExited(_handle)) return Task.FromResult(false);
        if (Native.ReadStartTicks(_handle) != Identity.StartTimeUtcTicks) return Task.FromResult(false);
        if (!Native.ProcessIdToSessionId((uint)Identity.ProcessId, out uint session) ||
            !Native.ProcessIdToSessionId((uint)Environment.ProcessId, out uint currentSession))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (session != currentSession) return Task.FromResult(false);
        string owner = Native.ReadOwnerSid(_handle);
        using var currentIdentity = WindowsIdentity.GetCurrent();
        string currentOwner = currentIdentity.User?.Value ?? "";
        if (string.IsNullOrEmpty(currentOwner) || !string.Equals(owner, currentOwner, StringComparison.Ordinal))
            return Task.FromResult(false);
        string path = Native.ReadImagePath(_handle);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_policy.IsTrusted(new WindowsProcessEvidence(Identity, path, owner, session)));
    }

    public Task<bool> HasExitedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return Task.FromResult(Native.HasExited(_handle));
    }

    public Task RequestCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!VerifyIdentityAsync(cancellationToken).GetAwaiter().GetResult()) throw new UnauthorizedAccessException("Target identity not verified.");
        // Experimental only: HWND/PID verification and PostMessage are not atomic; window
        // or PID reuse can race this call. Never enable this path for real Discord as-is.
        using Process process = Process.GetProcessById(Identity.ProcessId);
        process.Refresh();
        IntPtr hwnd = process.MainWindowHandle;
        if (hwnd == IntPtr.Zero) return Task.CompletedTask;
        Native.GetWindowThreadProcessId(hwnd, out uint windowPid);
        if (windowPid != Identity.ProcessId || Native.HasExited(_handle)) throw new UnauthorizedAccessException("Window target changed.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!Native.PostMessage(hwnd, Native.WmClose, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Task.CompletedTask;
    }

    public Task ForceCloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!VerifyIdentityAsync(cancellationToken).GetAwaiter().GetResult()) throw new UnauthorizedAccessException("Target identity not verified.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!Native.TerminateProcess(_handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Task.CompletedTask;
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (Native.HasExited(_handle)) return;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _handle.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

internal static class Native
{
    internal const uint QueryLimitedInformation = 0x1000;
    internal const uint Synchronize = 0x00100000;
    internal const uint Terminate = 0x0001;
    internal const uint WmClose = 0x0010;
    private const uint TokenQuery = 0x0008;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;

    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll")] internal static extern ulong GetTickCount64();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    internal static long ReadStartTicks(SafeProcessHandle process)
    {
        if (!GetProcessTimes(process, out FILETIME creation, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        long fileTime = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
        return DateTime.FromFileTimeUtc(fileTime).Ticks;
    }

    internal static string ReadImagePath(SafeProcessHandle process)
    {
        var path = new StringBuilder(32768);
        uint size = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    internal static string ReadOwnerSid(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out SafeAccessTokenHandle token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value ?? throw new UnauthorizedAccessException("Process owner unavailable.");
    }

    internal static bool HasExited(SafeProcessHandle process)
    {
        uint result = WaitForSingleObject(process, 0);
        if (result == WaitObject0) return true;
        if (result == WaitTimeout) return false;
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
