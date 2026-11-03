using DiscordKiller.Core;
using Xunit;

namespace DiscordKiller.Core.Tests;

public sealed class ProcessShutdownCoordinatorTests
{
    private sealed class ManualClock : IMonotonicClock, IMonotonicDelay
    {
        private readonly object _gate = new();
        private long _now;
        private readonly List<(long Due, TaskCompletionSource<bool> Completion)> _waiters = [];
        private readonly Dictionary<long, TaskCompletionSource<bool>> _registered = [];

        public long ElapsedMilliseconds { get { lock (_gate) return _now; } }

        public Task WaitUntilAsync(long deadlineMilliseconds, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_now >= deadlineMilliseconds) return Task.CompletedTask;
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((deadlineMilliseconds, completion));
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                Registration(deadlineMilliseconds).TrySetResult(true);
                return completion.Task;
            }
        }

        public Task Registered(long deadline)
        {
            lock (_gate) return Registration(deadline).Task;
        }

        private TaskCompletionSource<bool> Registration(long deadline)
        {
            if (!_registered.TryGetValue(deadline, out var completion))
                _registered[deadline] = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return completion;
        }

        public void Advance(long milliseconds)
        {
            List<TaskCompletionSource<bool>> ready;
            lock (_gate)
            {
                _now += milliseconds;
                ready = _waiters.Where(waiter => waiter.Due <= _now).Select(waiter => waiter.Completion).ToList();
                _waiters.RemoveAll(waiter => waiter.Due <= _now || waiter.Completion.Task.IsCompleted);
            }
            foreach (var completion in ready) completion.TrySetResult(true);
        }
    }

    private sealed class FakeTarget(int pid, ManualClock clock) : IProcessTarget
    {
        private int _requests;
        private int _forces;
        private int _disposals;
        private bool _exited;
        private readonly TaskCompletionSource<bool> _exitedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProcessIdentity Identity { get; } = new(pid, 42);
        public bool IdentityValid { get; set; } = true;
        public bool ForceExits { get; set; } = true;
        public bool RequestThrows { get; set; }
        public bool WaitCompletesEarly { get; set; }
        public long AdvanceDuringVerification { get; set; }
        public TaskCompletionSource<bool>? RequestGate { get; set; }
        public TaskCompletionSource<bool>? VerifyGate { get; set; }
        public TaskCompletionSource<bool>? HasExitedGate { get; set; }
        public TaskCompletionSource<bool>? ForceGate { get; set; }
        public bool IgnoreCancellation { get; set; }
        public bool ForceCommittedBeforeGate { get; set; }
        public TaskCompletionSource<bool> RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ForceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> VerificationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ExitCheckStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests => Volatile.Read(ref _requests);
        public int Forces => Volatile.Read(ref _forces);
        public int Disposals => Volatile.Read(ref _disposals);
        public long? ForceAt { get; private set; }
        public bool Exited => Volatile.Read(ref _exited);

        public async Task<bool> VerifyIdentityAsync(CancellationToken cancellationToken)
        {
            VerificationStarted.TrySetResult(true);
            if (VerifyGate is not null) await WaitGate(VerifyGate, cancellationToken);
            if (AdvanceDuringVerification > 0) clock.Advance(AdvanceDuringVerification);
            return IdentityValid;
        }

        public async Task<bool> HasExitedAsync(CancellationToken cancellationToken)
        {
            ExitCheckStarted.TrySetResult(true);
            if (HasExitedGate is not null) await WaitGate(HasExitedGate, cancellationToken);
            return Exited;
        }

        public async Task RequestCloseAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            RequestStarted.TrySetResult(true);
            if (RequestThrows) throw new UnauthorizedAccessException("simulated");
            if (RequestGate is not null) await WaitGate(RequestGate, cancellationToken);
        }

        public async Task ForceCloseAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _forces);
            ForceAt = clock.ElapsedMilliseconds;
            ForceStarted.TrySetResult(true);
            if (ForceGate is not null) await WaitGate(ForceGate, cancellationToken);
            if (!ForceCommittedBeforeGate) cancellationToken.ThrowIfCancellationRequested();
            if (ForceExits) Exit();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => WaitCompletesEarly
            ? Task.CompletedTask : _exitedSignal.Task.WaitAsync(cancellationToken);

        public void Exit()
        {
            Volatile.Write(ref _exited, true);
            _exitedSignal.TrySetResult(true);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            Disposed.TrySetResult(true);
        }

        private Task WaitGate(TaskCompletionSource<bool> gate, CancellationToken token) =>
            IgnoreCancellation ? gate.Task : gate.Task.WaitAsync(token);
    }

    private sealed class FakeCatalog(params FakeTarget[] targets) : IProcessCatalog
    {
        private int _scans;
        public bool InitialComplete { get; set; } = true;
        public bool FinalComplete { get; set; } = true;
        public bool InitialUnverified { get; set; }
        public bool ThrowOnScan { get; set; }
        public bool ThrowOnFinalScan { get; set; }
        public TaskCompletionSource<bool>? ScanGate { get; set; }
        public bool IgnoreCancellation { get; set; }
        public bool ForceCommittedBeforeGate { get; set; }
        public TaskCompletionSource<bool> ScanStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProcessCandidate? NewCandidate { get; set; }
        public int Scans => Volatile.Read(ref _scans);

        public async Task<ProcessScan> ScanAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int scan = Interlocked.Increment(ref _scans);
            ScanStarted.TrySetResult(true);
            if (ScanGate is not null && scan == 1)
            {
                if (IgnoreCancellation) await ScanGate.Task;
                else await ScanGate.Task.WaitAsync(cancellationToken);
            }
            if (ThrowOnScan || (scan > 1 && ThrowOnFinalScan)) throw new UnauthorizedAccessException("simulated scan failure");
            var candidates = targets.Where(target => scan == 1 || !target.Exited)
                .Select(target => new ProcessCandidate(target.Identity, InitialUnverified && scan == 1 ? null : target))
                .ToList();
            if (scan > 1 && NewCandidate is not null) candidates.Add(NewCandidate);
            return new ProcessScan(scan == 1 ? InitialComplete : FinalComplete, candidates);
        }
    }

    private static async Task Signal(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task EmptyCompleteScansReportAlreadyClosed()
    {
        var clock = new ManualClock();
        var catalog = new FakeCatalog();
        var coordinator = new ProcessShutdownCoordinator(catalog, clock, clock);
        Assert.Equal(CloseOutcome.AlreadyClosed, await coordinator.CloseAsync(CancellationToken.None));
        Assert.Equal(2, catalog.Scans);
    }

    [Fact]
    public async Task IncompleteOrUnverifiedDiscoveryNeverTouchesProcesses()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock);
        var incomplete = new FakeCatalog(target) { InitialComplete = false };
        Assert.Equal(CloseOutcome.Unverified, await new ProcessShutdownCoordinator(incomplete, clock, clock).CloseAsync(CancellationToken.None));
        Assert.Equal(0, target.Requests);
        var unverified = new FakeCatalog(target) { InitialUnverified = true };
        Assert.Equal(CloseOutcome.Unverified, await new ProcessShutdownCoordinator(unverified, clock, clock).CloseAsync(CancellationToken.None));
        Assert.Equal(0, target.Requests);
        Assert.Equal(0, target.Forces);
    }

    [Fact]
    public async Task EnumerationFailureIsNotReportedAsAlreadyClosed()
    {
        var clock = new ManualClock();
        var catalog = new FakeCatalog() { ThrowOnScan = true };
        Assert.Equal(CloseOutcome.Error, await new ProcessShutdownCoordinator(catalog, clock, clock).CloseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EarlyGracefulExitCompletesWithoutForce()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock);
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock);
        Task<CloseOutcome> result = coordinator.CloseAsync(CancellationToken.None);
        await Signal(clock.Registered(10_000));
        target.Exit();
        Assert.Equal(CloseOutcome.Closed, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, target.Forces);
    }

    [Fact]
    public async Task RemainsInTrayUntilTenSecondsThenForceAndVerify()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock);
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock);
        Task<CloseOutcome> result = coordinator.CloseAsync(CancellationToken.None);
        await Signal(clock.Registered(10_000));
        clock.Advance(9_999);
        Assert.Equal(0, target.Forces);
        clock.Advance(1);
        Assert.Equal(CloseOutcome.Closed, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, target.Forces);
        Assert.Equal(10_000, target.ForceAt);
    }

    [Fact]
    public async Task SlowRequestDoesNotShortenAnotherTargetsOwnGracePeriod()
    {
        var clock = new ManualClock();
        var fast = new FakeTarget(1, clock);
        var slow = new FakeTarget(2, clock) { RequestGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(fast, slow), clock, clock);
        Task<CloseOutcome> result = coordinator.CloseAsync(CancellationToken.None);
        await Signal(slow.RequestStarted.Task);
        await Signal(clock.Registered(10_000));
        clock.Advance(4_000);
        slow.RequestGate.SetResult(true);
        await Signal(clock.Registered(14_000));
        clock.Advance(6_000);
        await Signal(fast.ForceStarted.Task);
        Assert.Equal(0, slow.Forces);
        clock.Advance(4_000);
        Assert.Equal(CloseOutcome.Closed, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(10_000, fast.ForceAt);
        Assert.Equal(14_000, slow.ForceAt);
    }

    [Fact]
    public async Task SleepAcrossGracePeriodRechecksIdentityBeforeForce()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock);
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock);
        Task<CloseOutcome> result = coordinator.CloseAsync(CancellationToken.None);
        await Signal(clock.Registered(10_000));
        target.IdentityValid = false; // PID reused or verification became unavailable.
        clock.Advance(60_000);
        Assert.NotEqual(CloseOutcome.Closed, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, target.Forces);
    }

    [Fact]
    public async Task ForceIsNotReportedSuccessfulWithoutExitConfirmationAfterFiveSeconds()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock) { ForceExits = false };
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock);
        Task<CloseOutcome> result = coordinator.CloseAsync(CancellationToken.None);
        await Signal(clock.Registered(10_000));
        clock.Advance(10_000);
        await Signal(clock.Registered(15_000));
        clock.Advance(4_999);
        Assert.False(result.IsCompleted);
        clock.Advance(1);
        Assert.NotEqual(CloseOutcome.Closed, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, target.Forces);
    }

    [Fact]
    public async Task NewCandidateIsObservedButNeverClosed()
    {
        var clock = new ManualClock();
        var first = new FakeTarget(1, clock);
        var newcomer = new FakeTarget(2, clock);
        var catalog = new FakeCatalog(first) { NewCandidate = new(newcomer.Identity, newcomer) };
        var coordinator = new ProcessShutdownCoordinator(catalog, clock, clock);
        Task<CloseOutcome> result = coordinator.CloseAsync(CancellationToken.None);
        await Signal(clock.Registered(10_000));
        first.Exit();
        Assert.Equal(CloseOutcome.Partial, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, newcomer.Requests);
        Assert.Equal(0, newcomer.Forces);
    }

    [Fact]
    public async Task OneDeniedTargetDoesNotPreventOtherTargetFromClosing()
    {
        var clock = new ManualClock();
        var first = new FakeTarget(1, clock);
        var denied = new FakeTarget(2, clock) { RequestThrows = true };
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(first, denied), clock, clock);
        Task<CloseOutcome> result = coordinator.CloseAsync(CancellationToken.None);
        await Signal(clock.Registered(10_000));
        first.Exit();
        Assert.Equal(CloseOutcome.Partial, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, first.Requests);
        Assert.Equal(1, denied.Requests);
        Assert.Equal(0, denied.Forces);
    }

    [Fact]
    public async Task IncompleteFinalRescanCannotBeReportedAsClosed()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock);
        var catalog = new FakeCatalog(target) { FinalComplete = false };
        var coordinator = new ProcessShutdownCoordinator(catalog, clock, clock);
        Task<CloseOutcome> result = coordinator.CloseAsync(CancellationToken.None);
        await Signal(clock.Registered(10_000));
        target.Exit();
        Assert.NotEqual(CloseOutcome.Closed, await result.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task HangingOrDeniedRequestCannotTriggerForce()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock) { RequestGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock);
        Task<CloseOutcome> result = coordinator.CloseAsync(CancellationToken.None);
        await Signal(target.RequestStarted.Task);
        await Signal(clock.Registered(5_000));
        clock.Advance(5_000);
        Assert.NotEqual(CloseOutcome.Closed, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, target.Forces);
    }

    [Fact]
    public async Task PrematureExitSignalWithoutConfirmedExitNeverForcesEarly()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock) { WaitCompletesEarly = true };
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock);
        CloseOutcome result = await coordinator.CloseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(CloseOutcome.Closed, result);
        Assert.Equal(0, target.Forces);
        Assert.Equal(0, clock.ElapsedMilliseconds);
    }

    [Fact]
    public async Task UnverifiedIdentityCannotReceiveCloseOrForce()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock) { IdentityValid = false };
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock);
        CloseOutcome result = await coordinator.CloseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(CloseOutcome.Closed, result);
        Assert.Equal(0, target.Requests);
        Assert.Equal(0, target.Forces);
    }

    [Fact]
    public async Task SynchronousVerificationCompletingAtDeadlineCannotAuthorizeClose()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock) { AdvanceDuringVerification = 5_000 };
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock);
        CloseOutcome result = await coordinator.CloseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(CloseOutcome.Closed, result);
        Assert.Equal(0, target.Requests);
        Assert.Equal(0, target.Forces);
        await Signal(target.Disposed.Task);
    }

    [Fact]
    public async Task SynchronousAdapterCancellingOuterTokenCannotReturnSuccessfulValue()
    {
        var clock = new ManualClock();
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(), clock, clock);
        using var cancellation = new CancellationTokenSource();
        Type leaseType = typeof(ProcessShutdownCoordinator).GetNestedType(
            "LeaseRegistry", System.Reflection.BindingFlags.NonPublic)!;
        object leases = Activator.CreateInstance(leaseType, nonPublic: true)!;
        var method = typeof(ProcessShutdownCoordinator).GetMethod(
            "BoundedAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(typeof(bool));
        Func<CancellationToken, Task<bool>> adapter = _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(true);
        };

        var result = (Task<bool>)method.Invoke(coordinator, [adapter, leases, cancellation.Token])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
    }

    [Fact]
    public async Task VerificationCompletingAfterTimeoutCannotAuthorizeClose()
    {
        var clock = new ManualClock();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new FakeTarget(1, clock) { VerifyGate = gate, IgnoreCancellation = true };
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock);
        Task<CloseOutcome> result = coordinator.CloseAsync(CancellationToken.None);
        await Signal(target.VerificationStarted.Task);
        await Signal(clock.Registered(5_000));
        clock.Advance(5_000);
        gate.TrySetResult(true);
        Assert.NotEqual(CloseOutcome.Closed, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, target.Requests);
        Assert.Equal(0, target.Forces);
        await Signal(target.Disposed.Task);
    }

    [Fact]
    public async Task CancelledShutdownDoesNotForceAnything()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock);
        using var cancellation = new CancellationTokenSource();
        var coordinator = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock);
        Task<CloseOutcome> result = coordinator.CloseAsync(cancellation.Token);
        await Signal(clock.Registered(10_000));
        cancellation.Cancel();
        Assert.NotEqual(CloseOutcome.Closed, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, target.Forces);
        await Signal(target.Disposed.Task);
        Assert.Equal(1, target.Disposals);
    }

    [Fact]
    public async Task ReleasesTargetAfterSuccessAndFailedFinalScan()
    {
        var clock = new ManualClock();
        var exited = new FakeTarget(1, clock);
        exited.Exit();
        Assert.Equal(CloseOutcome.Closed, await new ProcessShutdownCoordinator(new FakeCatalog(exited), clock, clock).CloseAsync(CancellationToken.None));
        await Signal(exited.Disposed.Task);
        Assert.Equal(1, exited.Disposals);

        var other = new FakeTarget(2, clock);
        other.Exit();
        var catalog = new FakeCatalog(other) { ThrowOnFinalScan = true };
        Assert.Equal(CloseOutcome.Error, await new ProcessShutdownCoordinator(catalog, clock, clock).CloseAsync(CancellationToken.None));
        await Signal(other.Disposed.Task);
        Assert.Equal(1, other.Disposals);
    }

    [Fact]
    public async Task DuplicateAndPartialScansDisposeEachObjectOnce()
    {
        var clock = new ManualClock();
        var duplicate = new FakeTarget(1, clock);
        Assert.Equal(CloseOutcome.Unverified, await new ProcessShutdownCoordinator(new FakeCatalog(duplicate, duplicate), clock, clock).CloseAsync(CancellationToken.None));
        await Signal(duplicate.Disposed.Task);
        Assert.Equal(1, duplicate.Disposals);

        var denied = new FakeTarget(2, clock) { RequestThrows = true };
        Assert.NotEqual(CloseOutcome.Closed, await new ProcessShutdownCoordinator(new FakeCatalog(denied), clock, clock).CloseAsync(CancellationToken.None));
        await Signal(denied.Disposed.Task);
        Assert.Equal(1, denied.Disposals); // The final scan returns the same object.
    }

    [Fact]
    public async Task FinalScanNewTargetIsDisposedWithoutBeingTouched()
    {
        var clock = new ManualClock();
        var first = new FakeTarget(1, clock);
        first.Exit();
        var newcomer = new FakeTarget(2, clock);
        var catalog = new FakeCatalog(first) { NewCandidate = new(newcomer.Identity, newcomer) };
        Assert.Equal(CloseOutcome.Partial, await new ProcessShutdownCoordinator(catalog, clock, clock).CloseAsync(CancellationToken.None));
        await Signal(first.Disposed.Task);
        await Signal(newcomer.Disposed.Task);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, newcomer.Disposals);
        Assert.Equal(0, newcomer.Requests);
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("has-exited")]
    [InlineData("request")]
    [InlineData("force")]
    public async Task HangingAdapterTimesOutWithoutUnsafeDisposalOrRepeatedForce(string phase)
    {
        var clock = new ManualClock();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new FakeTarget(1, clock) { IgnoreCancellation = true };
        if (phase == "verify") target.VerifyGate = gate;
        if (phase == "has-exited") target.HasExitedGate = gate;
        if (phase == "request") target.RequestGate = gate;
        if (phase == "force") target.ForceGate = gate;

        Task<CloseOutcome> result = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock).CloseAsync(CancellationToken.None);
        if (phase == "force")
        {
            await Signal(clock.Registered(10_000));
            clock.Advance(10_000);
            await Signal(target.ForceStarted.Task);
        }
        else if (phase == "verify") await Signal(target.VerificationStarted.Task);
        else if (phase == "has-exited") await Signal(target.ExitCheckStarted.Task);
        else await Signal(target.RequestStarted.Task);

        long deadline = phase == "force" ? 15_000 : 5_000;
        await Signal(clock.Registered(deadline));
        clock.Advance(5_000);
        Assert.NotEqual(CloseOutcome.Closed, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(phase == "force" ? 1 : 0, target.Forces);
        Assert.Equal(0, target.Disposals); // Never free while the operation is still running.
        gate.TrySetResult(true);
        await Signal(target.Disposed.Task);
        Assert.Equal(1, target.Disposals);
        Assert.False(target.Exited); // A cancelled force may not act later.
    }

    [Fact]
    public async Task TimeoutAfterCommittedForceIsUnconfirmedAndRetainsHandleUntilCompletion()
    {
        var clock = new ManualClock();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new FakeTarget(1, clock)
        {
            ForceGate = release, IgnoreCancellation = true, ForceCommittedBeforeGate = true
        };
        Task<CloseOutcome> result = new ProcessShutdownCoordinator(new FakeCatalog(target), clock, clock)
            .CloseAsync(CancellationToken.None);
        await Signal(clock.Registered(10_000));
        clock.Advance(10_000);
        await Signal(target.ForceStarted.Task);
        await Signal(clock.Registered(15_000));
        clock.Advance(5_000);
        Assert.NotEqual(CloseOutcome.Closed, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, target.Forces);
        Assert.Equal(0, target.Disposals);
        release.TrySetResult(true);
        await Signal(target.Disposed.Task);
        Assert.True(target.Exited); // A committed native call may finish after the timeout.
        Assert.Equal(1, target.Forces);
    }

    [Fact]
    public async Task LateNonCooperativeScanStillTransfersAndReleasesOwnership()
    {
        var clock = new ManualClock();
        var target = new FakeTarget(1, clock);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeCatalog(target) { ScanGate = gate, IgnoreCancellation = true };
        Task<CloseOutcome> result = new ProcessShutdownCoordinator(catalog, clock, clock).CloseAsync(CancellationToken.None);
        await Signal(catalog.ScanStarted.Task);
        await Signal(clock.Registered(5_000));
        clock.Advance(5_000);
        Assert.Equal(CloseOutcome.Error, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, target.Disposals);
        gate.TrySetResult(true);
        await Signal(target.Disposed.Task);
        Assert.Equal(1, target.Disposals);
        Assert.Equal(0, target.Requests);
    }
}
