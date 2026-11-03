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
    public void CancellationBeforeCommitPreventsSideEffect()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool called = false;
        Assert.Throws<OperationCanceledException>(() => SideEffectCommit.Run(cancellation.Token, () => called = true));
        Assert.False(called);
    }

    [Fact]
    public async Task CommittedOperationCanFinishAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool finished = false;
        Task worker = Task.Run(() => SideEffectCommit.Run(cancellation.Token, () =>
        {
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            finished = true;
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        release.TrySetResult(true);
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(finished);
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
    public async Task CoordinatorWaitsTenSecondsAndTerminatesOnlyItsOwnChild()
    {
        using var child = StartOwnChild();
        try
        {
            // Test-only trust is bound to Process.Start's retained handle, PID, and creation time.
            // This proves only the controlled fixture path, not the production cancellation contract.
            var policy = new TestPolicy(child);
            SafeProcessHandle originalHandle = child.SafeHandle;
            int originalPid = child.Id;
            var catalog = new WindowsProcessCatalog(policy,
                () => WaitForSingleObject(originalHandle, 0) == 0x102 ? new[] { originalPid } : Array.Empty<int>(), true);
            var clock = new WindowsElapsedClock();
            var timing = new Timing();
            var coordinator = new ProcessShutdownCoordinator(new TimingCatalog(catalog, clock, timing), clock, clock);
            long started = clock.ElapsedMilliseconds;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            CloseOutcome outcome = await coordinator.CloseAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(35));

            Assert.Equal(CloseOutcome.Closed, outcome);
            Assert.True(clock.ElapsedMilliseconds - started >= ProcessShutdownCoordinator.GracePeriodMilliseconds);
            Assert.True(timing.RequestCompleted.HasValue && timing.ForceStarted.HasValue);
            Assert.True(timing.ForceStarted!.Value - timing.RequestCompleted!.Value >= ProcessShutdownCoordinator.GracePeriodMilliseconds);
            Assert.Equal(0u, WaitForSingleObject(originalHandle, 0));
        }
        finally { StopOwnChild(child); }
    }

    [Fact]
    public async Task CoordinatorCancellationLeavesItsOwnChildRunning()
    {
        using var child = StartOwnChild();
        try
        {
            var policy = new TestPolicy(child);
            SafeProcessHandle originalHandle = child.SafeHandle;
            int originalPid = child.Id;
            var catalog = new WindowsProcessCatalog(policy,
                () => WaitForSingleObject(originalHandle, 0) == 0x102 ? new[] { originalPid } : Array.Empty<int>(), true);
            var clock = new WindowsElapsedClock();
            var timing = new Timing();
            var coordinator = new ProcessShutdownCoordinator(new TimingCatalog(catalog, clock, timing), clock, clock);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Task<CloseOutcome> operation = coordinator.CloseAsync(cancellation.Token);
            Assert.False(operation.IsCompleted);
            await timing.RequestSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            CloseOutcome outcome = await operation.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(CloseOutcome.Error, outcome);
            Assert.Equal(0x102u, WaitForSingleObject(originalHandle, 0));
            Assert.True(timing.RequestCompleted.HasValue);
            await clock.WaitUntilAsync(timing.RequestCompleted!.Value + ProcessShutdownCoordinator.GracePeriodMilliseconds + 1_000,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(timing.ForceStarted);
            Assert.Equal(0x102u, WaitForSingleObject(originalHandle, 0));
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
        Assert.True(policy.IsReady);
        Assert.False(policy.IsTrusted(new WindowsProcessEvidence(new ProcessIdentity(1, 1), plausible, "sid", 1)));
        Assert.False(DiscordStableEvidencePolicy.InspectFile(plausible, root).RunningImageBindingVerified);
    }

    [Fact]
    public void VerifiedSignerMustExactlyMatchDiscordPublisher()
    {
        Assert.True(DiscordStableEvidencePolicy.IsAllowedEvidence(new(true, true, "CN=Discord Inc.", "Discord Inc.")));
        Assert.False(DiscordStableEvidencePolicy.IsAllowedEvidence(new(true, true, "CN=Discord Inc. Fake", "Discord Inc. Fake")));
        Assert.False(DiscordStableEvidencePolicy.IsAllowedEvidence(new(true, false, "CN=Discord Inc.", "Discord Inc.")));
        Assert.False(DiscordStableEvidencePolicy.IsAllowedEvidence(new(false, true, "CN=Discord Inc.", "Discord Inc.")));
    }

    [Fact]
    public void StablePathRejectsChannelsAndNestedOrUnversionedLocations()
    {
        string root = Path.Combine(Path.GetTempPath(), "discord-killer-test-user");
        Assert.True(DiscordStableEvidencePolicy.IsStablePath(Path.Combine(root, "Discord", "app-1.0.9059", "Discord.exe"), root));
        Assert.False(DiscordStableEvidencePolicy.IsStablePath(Path.Combine(root, "DiscordPTB", "app-1.0.9059", "Discord.exe"), root));
        Assert.False(DiscordStableEvidencePolicy.IsStablePath(Path.Combine(root, "Discord", "app-abc", "Discord.exe"), root));
        Assert.False(DiscordStableEvidencePolicy.IsStablePath(Path.Combine(root, "Discord", "app-1.0.9059", "nested", "Discord.exe"), root));
        Assert.False(DiscordStableEvidencePolicy.IsStablePath(Path.Combine(root, "Discord", "app-1.0.9059", "Discord-other.exe"), root));
    }

    [Fact]
    public void UnsignedFileAtPlausiblePathIsNeverTrusted()
    {
        string root = Path.Combine(Path.GetTempPath(), "discord-killer-policy-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "Discord", "app-1.0.9059");
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "Discord.exe");
            File.WriteAllText(path, "not a signed Windows executable");
            DiscordStableEvidence evidence = DiscordStableEvidencePolicy.InspectFile(path, root);
            Assert.True(evidence.StablePathMatch);
            Assert.False(evidence.AuthenticodeValid);
            Assert.False(evidence.RunningImageBindingVerified);
            Assert.False(new DiscordStableEvidencePolicy().IsTrusted(
                new WindowsProcessEvidence(new ProcessIdentity(1, 1), path, "sid", 1)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CopiedSystemFileAtPlausiblePathIsNeverTrustedAsDiscord()
    {
        string root = Path.Combine(Path.GetTempPath(), "discord-killer-policy-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "Discord", "app-1.0.9059");
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            string path = Path.Combine(folder, "Discord.exe");
            File.Copy(source, path);
            DiscordStableEvidence evidence = DiscordStableEvidencePolicy.InspectFile(path, root);
            Assert.True(evidence.StablePathMatch);
            if (evidence.AuthenticodeValid)
                Assert.DoesNotContain("Discord Inc", evidence.SignerSubject ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.False(evidence.RunningImageBindingVerified);
            Assert.False(new DiscordStableEvidencePolicy().IsTrusted(
                new WindowsProcessEvidence(new ProcessIdentity(1, 1), path, "sid", 1)));
        }
        finally { Directory.Delete(root, true); }
    }

    [DiscordSignedFixtureFact]
    public void SignedDiscordFixtureReturnsVerifiedProviderSigner()
    {
        // A CI job must opt in with a real signed Stable executable from its disposable
        // runner. No process is started, opened, messaged, or terminated by this test.
        string fixture = Environment.GetEnvironmentVariable("DISCORD_KILLER_SIGNED_FIXTURE")!;
        Assert.True(File.Exists(fixture), "DISCORD_KILLER_SIGNED_FIXTURE must point to the signed Discord.exe fixture.");
        string root = Path.Combine(Path.GetTempPath(), "discord-killer-positive-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "Discord", "app-1.0.9059");
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "Discord.exe");
            File.Copy(fixture, path);
            DiscordStableEvidence evidence = DiscordStableEvidencePolicy.InspectFile(path, root);
            Assert.True(evidence.StablePathMatch);
            Assert.True(evidence.AuthenticodeValid, "WinVerifyTrust or provider signer extraction failed.");
            Assert.NotNull(evidence.SignerSubject);
            Assert.True(DiscordStableEvidencePolicy.IsAllowedEvidence(evidence));
            Assert.True(string.Equals("CN=Discord Inc.", evidence.SignerSubject.Split(',')[0].Trim(),
                StringComparison.OrdinalIgnoreCase), $"Unexpected verified signer: {evidence.SignerSubject}");
            Assert.False(evidence.RunningImageBindingVerified);
            Assert.True(new DiscordStableEvidencePolicy().IsReady);
        }
        finally { Directory.Delete(root, true); }
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

    private sealed class Timing
    {
        public TaskCompletionSource<bool> RequestSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _requestCompleted = -1;
        private long _forceStarted = -1;
        public long? RequestCompleted
        {
            get { long value = Volatile.Read(ref _requestCompleted); return value < 0 ? null : value; }
            set => Interlocked.Exchange(ref _requestCompleted, value ?? -1);
        }
        public long? ForceStarted
        {
            get { long value = Volatile.Read(ref _forceStarted); return value < 0 ? null : value; }
            set => Interlocked.Exchange(ref _forceStarted, value ?? -1);
        }
    }

    private sealed class TimingCatalog(IProcessCatalog inner, IMonotonicClock clock, Timing timing) : IProcessCatalog
    {
        public async Task<ProcessScan> ScanAsync(CancellationToken cancellationToken)
        {
            ProcessScan scan = await inner.ScanAsync(cancellationToken);
            return new ProcessScan(scan.Complete, scan.Candidates.Select(candidate =>
                candidate.VerifiedTarget is null ? candidate :
                new ProcessCandidate(candidate.Identity, new TimingTarget(candidate.VerifiedTarget, clock, timing))).ToArray());
        }
    }

    private sealed class TimingTarget(IProcessTarget inner, IMonotonicClock clock, Timing timing) : IProcessTarget
    {
        public ProcessIdentity Identity => inner.Identity;
        public Task<bool> VerifyIdentityAsync(CancellationToken token) => inner.VerifyIdentityAsync(token);
        public Task<bool> HasExitedAsync(CancellationToken token) => inner.HasExitedAsync(token);
        public Task WaitForExitAsync(CancellationToken token) => inner.WaitForExitAsync(token);

        public async Task RequestCloseAsync(CancellationToken token)
        {
            await inner.RequestCloseAsync(token);
            timing.RequestCompleted = clock.ElapsedMilliseconds;
            timing.RequestSignal.TrySetResult(true);
        }

        public async Task ForceCloseAsync(CancellationToken token)
        {
            timing.ForceStarted = clock.ElapsedMilliseconds;
            await inner.ForceCloseAsync(token);
        }

        public void Dispose() => inner.Dispose();
    }
}

/// <summary>Reports a real skip, rather than passing without exercising a signed fixture.</summary>
internal sealed class DiscordSignedFixtureFactAttribute : FactAttribute
{
    public DiscordSignedFixtureFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISCORD_KILLER_SIGNED_FIXTURE")))
            Skip = "Set DISCORD_KILLER_SIGNED_FIXTURE to a signed Discord Stable executable on an isolated Windows runner.";
    }
}
