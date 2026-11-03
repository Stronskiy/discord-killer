using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using DiscordKiller.Core;
using DiscordKiller.Windows.ProcessControl;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace DiscordKiller.Windows.IntegrationTests;

/// <summary>Only creates or terminates the PowerShell child process created by each test.</summary>
public sealed class WindowsProcessControlTests
{
    [Fact]
    public async Task DefaultPolicyCannotAssertDiscordIsAbsent()
    {
        Assert.True(OperatingSystem.IsWindows());
        ProcessScan scan = await new WindowsProcessCatalog().ScanAsync(CancellationToken.None);
        Assert.False(scan.Complete);
        Assert.Empty(scan.Candidates);
    }

    [Fact]
    public void RefusesToOpenCurrentProcess()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.Throws<ArgumentException>(() => WindowsProcessTarget.Open(Environment.ProcessId, new UnverifiedWindowsProcessPolicy()));
    }

    [Fact]
    public async Task DeniedPolicyCannotTerminateOwnChild()
    {
        using var child = StartOwnChild();
        try
        {
            using var target = WindowsProcessTarget.Open(child.Id, new UnverifiedWindowsProcessPolicy());
            Assert.False(await target.VerifyIdentityAsync(CancellationToken.None));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => target.ForceCloseAsync(CancellationToken.None));
            Assert.False(child.HasExited);
        }
        finally { StopOwnChild(child); }
    }

    [Fact]
    public async Task CancelledTokenCannotTerminateOwnChild()
    {
        using var child = StartOwnChild();
        try
        {
            using var target = WindowsProcessTarget.Open(child.Id, new TestPolicy(child));
            Assert.True(await target.VerifyIdentityAsync(CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => target.ForceCloseAsync(cancellation.Token));
            Assert.False(child.HasExited);
        }
        finally { StopOwnChild(child); }
    }

    [Fact]
    public async Task OriginalHandleCanTerminateAndObserveOnlyOwnChild()
    {
        using var child = StartOwnChild();
        try
        {
            using var target = WindowsProcessTarget.Open(child.Id, new TestPolicy(child));
            Assert.True(await target.VerifyIdentityAsync(CancellationToken.None));
            Assert.False(await target.HasExitedAsync(CancellationToken.None));
            await target.ForceCloseAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await target.WaitForExitAsync(timeout.Token);
            Assert.True(await target.HasExitedAsync(CancellationToken.None));
        }
        finally { StopOwnChild(child); }
    }

    [Fact]
    public async Task UntrustedCandidateIsExplicitlyReported()
    {
        using var child = StartOwnChild();
        try
        {
            var catalog = new WindowsProcessCatalog(new TestPolicy(child, allow: false), () => new[] { child.Id }, true);
            ProcessScan scan = await catalog.ScanAsync(CancellationToken.None);
            Assert.True(scan.Complete);
            ProcessCandidate candidate = Assert.Single(scan.Candidates);
            Assert.Null(candidate.VerifiedTarget);
            Assert.False(child.HasExited);
        }
        finally { StopOwnChild(child); }
    }

    [Fact]
    public async Task CatalogRetainsAndReportsOnlyOwnVerifiedChild()
    {
        using var child = StartOwnChild();
        try
        {
            var catalog = new WindowsProcessCatalog(new TestPolicy(child), () => new[] { child.Id }, true);
            ProcessScan scan = await catalog.ScanAsync(CancellationToken.None);
            Assert.True(scan.Complete);
            ProcessCandidate candidate = Assert.Single(scan.Candidates);
            using IProcessTarget target = Assert.IsAssignableFrom<IProcessTarget>(candidate.VerifiedTarget);
            Assert.Equal(child.Id, target.Identity.ProcessId);
            Assert.True(target.Identity.StartTimeUtcTicks > 0);
            Assert.True(await target.VerifyIdentityAsync(CancellationToken.None));
        }
        finally { StopOwnChild(child); }
    }

    [Fact]
    public async Task UnprovenEnumerationCannotReportAlreadyClosed()
    {
        using var child = StartOwnChild();
        try
        {
            var catalog = new WindowsProcessCatalog(new TestPolicy(child), () => Array.Empty<int>(), false);
            ProcessScan scan = await catalog.ScanAsync(CancellationToken.None);
            Assert.False(scan.Complete);
            Assert.Empty(scan.Candidates);
        }
        finally { StopOwnChild(child); }
    }

    [Fact]
    public void ExitedOriginalChildCannotBeTrustedEvenWithMatchingPidAndStartTime()
    {
        using var child = StartOwnChild();
        try
        {
            var policy = new TestPolicy(child);
            var identity = new ProcessIdentity(child.Id, child.StartTime.ToUniversalTime().Ticks);
            var evidence = new WindowsProcessEvidence(identity, "fixture", "fixture", 1);
            Assert.True(policy.IsTrusted(evidence));
            StopOwnChild(child);
            Assert.False(policy.IsTrusted(evidence));
        }
        finally { StopOwnChild(child); }
    }

    [Fact]
    public void StablePathEvidenceNeverAuthorizesARunningProcess()
    {
        Assert.True(OperatingSystem.IsWindows());
        string root = Path.Combine(Path.GetTempPath(), "discord-killer-test-user");
        string plausible = Path.Combine(root, "Discord", "app-1.0.9059", "Discord.exe");
        string otherChannel = Path.Combine(root, "DiscordCanary", "app-1.0.9059", "Discord.exe");
        Assert.True(DiscordStableEvidencePolicy.IsStablePath(plausible, root));
        Assert.False(DiscordStableEvidencePolicy.IsStablePath(otherChannel, root));
        var policy = new DiscordStableEvidencePolicy();
        Assert.False(policy.IsReady);
        Assert.False(policy.IsTrusted(new WindowsProcessEvidence(new ProcessIdentity(1, 1), plausible, "sid", 1)));
        Assert.False(DiscordStableEvidencePolicy.InspectFile(plausible, root).RunningImageBindingVerified);
    }

    private static Process StartOwnChild()
    {
        Assert.True(OperatingSystem.IsWindows());
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return Process.Start(new ProcessStartInfo(executable, "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start controlled child process.");
    }

    private static void StopOwnChild(Process child)
    {
        // Use only the retained handle from Process.Start, never reacquire by PID.
        SafeProcessHandle handle = child.SafeHandle;
        uint state = WaitForSingleObject(handle, 0);
        if (state == 0x102 && !TerminateProcess(handle, 1) && WaitForSingleObject(handle, 0) != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Assert.Equal(0u, WaitForSingleObject(handle, 10_000));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle handle, uint exitCode);

    private sealed class TestPolicy : IWindowsProcessTrustPolicy
    {
        private readonly SafeProcessHandle _originalHandle;
        private readonly int _pid;
        private readonly long _startTicks;
        private readonly bool _allow;

        public TestPolicy(Process original, bool allow = true)
        {
            _originalHandle = original.SafeHandle;
            _pid = original.Id;
            _startTicks = original.StartTime.ToUniversalTime().Ticks;
            _allow = allow;
        }

        public bool IsReady => true;
        public bool IsTrusted(WindowsProcessEvidence evidence) =>
            _allow && WaitForSingleObject(_originalHandle, 0) == 0x102 &&
            evidence.Identity.ProcessId == _pid && evidence.Identity.StartTimeUtcTicks == _startTicks;
    }
}
