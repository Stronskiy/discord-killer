namespace DiscordKiller.Core;

/// <summary>
/// An opaque process-instance identity. The operating-system adapter must bind
/// this to an independently verified process handle, owner and executable.
/// A PID alone is not an identity.
/// </summary>
public readonly record struct ProcessIdentity(int ProcessId, long StartTimeUtcTicks);

/// <summary>
/// A null VerifiedTarget represents a candidate whose identity or access could
/// not be established. The adapter must not mark a target verified by process
/// name or path alone.
/// </summary>
public sealed record ProcessCandidate(ProcessIdentity Identity, IProcessTarget? VerifiedTarget);

/// <summary>
/// Ownership of every non-null target transfers to the coordinator when a scan
/// completes, including incomplete scans. A catalog that fails before returning a
/// scan must release all handles it allocated itself.
/// </summary>
public sealed record ProcessScan(bool Complete, IReadOnlyList<ProcessCandidate> Candidates);

public interface IProcessCatalog
{
    /// <summary>
    /// Must return promptly; cancellation must prevent late side effects. If a
    /// cancelled scan returns late, ownership of its returned targets still
    /// transfers to the coordinator for cleanup.
    /// </summary>
    Task<ProcessScan> ScanAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One independently verified process instance and its retained handle. All
/// methods must return promptly and honour cancellation. In particular, a
/// cancelled RequestCloseAsync or ForceCloseAsync MUST NOT perform a later
/// side effect. Every asynchronous operation must retain its own safe handle
/// lease until it finishes. Dispose must be idempotent and non-throwing.
/// A request with no main window completes normally; it is not proof of exit.
/// Implementations require Windows-specific verification before use.
/// </summary>
public interface IProcessTarget : IDisposable
{
    ProcessIdentity Identity { get; }
    Task<bool> VerifyIdentityAsync(CancellationToken cancellationToken);
    Task<bool> HasExitedAsync(CancellationToken cancellationToken);
    Task RequestCloseAsync(CancellationToken cancellationToken);
    Task ForceCloseAsync(CancellationToken cancellationToken);
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>Wait for a deadline on the same monotonic clock, including sleep.</summary>
public interface IMonotonicDelay
{
    Task WaitUntilAsync(long deadlineMilliseconds, CancellationToken cancellationToken);
}

/// <summary>
/// Testable shutdown policy. No Windows process discovery or termination is
/// supplied. Real adapters must prove identity and the cancellation, handle
/// lifetime and no-late-side-effects contract before they may be enabled.
/// </summary>
public sealed class ProcessShutdownCoordinator : IDiscordCloser
{
    public const int GracePeriodMilliseconds = 10_000;
    public const int ExitVerificationMilliseconds = 5_000;
    public const int CloseRequestLimitMilliseconds = 5_000;
    public const int AdapterOperationLimitMilliseconds = 5_000;

    private readonly IProcessCatalog _catalog;
    private readonly IMonotonicClock _clock;
    private readonly IMonotonicDelay _delay;

    public ProcessShutdownCoordinator(IProcessCatalog catalog, IMonotonicClock clock, IMonotonicDelay delay)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public async Task<CloseOutcome> CloseAsync(CancellationToken cancellationToken)
    {
        var leases = new LeaseRegistry();
        try
        {
            ProcessScan initial = await BoundedAsync(
                token => ScanAndAdoptAsync(leases, token), leases, cancellationToken).ConfigureAwait(false);
            if (initial is null || initial.Candidates is null) return CloseOutcome.Unverified;
            ProcessCandidate[] snapshot = initial.Candidates.ToArray();
            if (!IsTrustedScan(new ProcessScan(initial.Complete, snapshot))) return CloseOutcome.Unverified;

            Task<TargetResult>[] tasks = snapshot
                .Select(candidate => CloseTargetAsync(candidate.VerifiedTarget!, leases, cancellationToken))
                .ToArray();
            TargetResult[] results = await Task.WhenAll(tasks).ConfigureAwait(false);

            // A second scan is for reporting only. New processes are never targeted.
            ProcessScan final = await BoundedAsync(
                token => ScanAndAdoptAsync(leases, token), leases, cancellationToken).ConfigureAwait(false);
            bool initialEmpty = snapshot.Length == 0;
            if (!IsTrustedScan(final) || final.Candidates.Count != 0)
                return initialEmpty ? CloseOutcome.Unverified : CloseOutcome.Partial;

            if (initialEmpty) return CloseOutcome.AlreadyClosed;
            if (results.All(result => result == TargetResult.Exited)) return CloseOutcome.Closed;
            if (results.Any(result => result == TargetResult.Exited)) return CloseOutcome.Partial;
            return results.Any(result => result == TargetResult.Unverified)
                ? CloseOutcome.Unverified : CloseOutcome.Error;
        }
        catch (Exception)
        {
            return CloseOutcome.Error;
        }
        finally
        {
            // A cancelled operation might violate the adapter contract and
            // continue using its handle. Never dispose that handle while any
            // tracked operation may still be running. Cleanup is deferred and
            // can remain pending indefinitely for a broken adapter.
            leases.ReleaseWhenSafe();
        }
    }

    private async Task<ProcessScan> ScanAndAdoptAsync(LeaseRegistry leases, CancellationToken token)
    {
        ProcessScan scan = await _catalog.ScanAsync(token).ConfigureAwait(false);
        leases.Adopt(scan);
        return scan;
    }

    private static bool IsTrustedScan(ProcessScan scan)
    {
        if (scan is null || !scan.Complete || scan.Candidates is null) return false;
        var identities = new HashSet<ProcessIdentity>();
        foreach (ProcessCandidate candidate in scan.Candidates)
        {
            if (candidate is null || candidate.Identity.ProcessId <= 0 || candidate.Identity.StartTimeUtcTicks <= 0
                || candidate.VerifiedTarget is null || candidate.VerifiedTarget.Identity != candidate.Identity
                || !identities.Add(candidate.Identity))
                return false;
        }
        return true;
    }

    private async Task<TargetResult> CloseTargetAsync(IProcessTarget target, LeaseRegistry leases, CancellationToken cancellationToken)
    {
        try
        {
            if (!await BoundedAsync(target.VerifyIdentityAsync, leases, cancellationToken).ConfigureAwait(false))
                return TargetResult.Unverified;
            if (await BoundedAsync(target.HasExitedAsync, leases, cancellationToken).ConfigureAwait(false))
                return TargetResult.Exited;

            await BoundedAsync(token => AsBooleanAsync(target.RequestCloseAsync, token), leases, cancellationToken).ConfigureAwait(false);
            long graceDeadline = checked(_clock.ElapsedMilliseconds + GracePeriodMilliseconds);
            if (await WaitForExitUntilAsync(target, graceDeadline, leases, cancellationToken).ConfigureAwait(false))
                return TargetResult.Exited;

            if (!await BoundedAsync(target.VerifyIdentityAsync, leases, cancellationToken).ConfigureAwait(false))
                return TargetResult.Unverified;
            if (await BoundedAsync(target.HasExitedAsync, leases, cancellationToken).ConfigureAwait(false))
                return TargetResult.Exited;
            cancellationToken.ThrowIfCancellationRequested();
            await BoundedAsync(token => AsBooleanAsync(target.ForceCloseAsync, token), leases, cancellationToken).ConfigureAwait(false);
            long exitDeadline = checked(_clock.ElapsedMilliseconds + ExitVerificationMilliseconds);
            return await WaitForExitUntilAsync(target, exitDeadline, leases, cancellationToken).ConfigureAwait(false)
                ? TargetResult.Exited : TargetResult.Failed;
        }
        catch (Exception)
        {
            return TargetResult.Failed;
        }
    }

    private static async Task<bool> AsBooleanAsync(Func<CancellationToken, Task> operation, CancellationToken token)
    {
        await operation(token).ConfigureAwait(false);
        return true;
    }

    private async Task<T> BoundedAsync<T>(Func<CancellationToken, Task<T>> start, LeaseRegistry leases, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<T>? operation = null;
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            long deadline = checked(_clock.ElapsedMilliseconds + AdapterOperationLimitMilliseconds);
            operation = start(operationCancellation.Token);
            if (operation is null) throw new InvalidOperationException("The adapter returned a null task.");
            leases.Track(operation);
            if (operation.IsCompleted)
            {
                if (_clock.ElapsedMilliseconds >= deadline)
                    throw new TimeoutException("The process adapter completed at or after its deadline.");
                return await operation.ConfigureAwait(false);
            }
            Task timer = _delay.WaitUntilAsync(deadline, timerCancellation.Token);
            ObserveFault(timer);
            Task cancelled = Task.Delay(Timeout.Infinite, timerCancellation.Token);
            Task winner = await Task.WhenAny(operation, timer, cancelled).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (winner != operation)
            {
                await winner.ConfigureAwait(false);
                throw new TimeoutException("The process adapter did not complete in time.");
            }
            if (_clock.ElapsedMilliseconds >= deadline)
                throw new TimeoutException("The process adapter completed at or after its deadline.");
            return await operation.ConfigureAwait(false);
        }
        finally
        {
            CancelSafely(timerCancellation);
            CancelSafely(operationCancellation);
            if (operation is null || operation.IsCompleted)
                operationCancellation.Dispose();
            else
                _ = operation.ContinueWith(_ => operationCancellation.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task<bool> WaitForExitUntilAsync(IProcessTarget target, long deadline, LeaseRegistry leases, CancellationToken cancellationToken)
    {
        if (await BoundedAsync(target.HasExitedAsync, leases, cancellationToken).ConfigureAwait(false)) return true;

        var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? exited = null;
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            exited = target.WaitForExitAsync(waitCancellation.Token);
            if (exited is null) throw new InvalidOperationException("The adapter returned a null wait task.");
            leases.Track(exited);
            Task timedOut = _delay.WaitUntilAsync(deadline, timerCancellation.Token);
            ObserveFault(timedOut);
            Task cancelled = Task.Delay(Timeout.Infinite, timerCancellation.Token);
            Task winner = await Task.WhenAny(exited, timedOut, cancelled).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await winner.ConfigureAwait(false);
            bool confirmedExited = await BoundedAsync(target.HasExitedAsync, leases, cancellationToken).ConfigureAwait(false);
            if (!confirmedExited && _clock.ElapsedMilliseconds < deadline)
                throw new InvalidOperationException("A wait completed before its deadline without a confirmed exit.");
            return confirmedExited;
        }
        finally
        {
            CancelSafely(timerCancellation);
            CancelSafely(waitCancellation);
            if (exited is null || exited.IsCompleted)
                waitCancellation.Dispose();
            else
                _ = exited.ContinueWith(_ => waitCancellation.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(
        completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
        TaskScheduler.Default);

    private static void CancelSafely(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (AggregateException) { /* A broken callback must not skip cleanup. */ }
    }

    private sealed class LeaseRegistry
    {
        private readonly object _gate = new();
        private readonly HashSet<IProcessTarget> _targets = new(ReferenceEqualityComparer.Instance);
        private readonly List<Task> _operations = [];

        public void Adopt(ProcessScan? scan)
        {
            if (scan?.Candidates is null) return;
            lock (_gate)
            {
                foreach (ProcessCandidate? candidate in scan.Candidates)
                    if (candidate?.VerifiedTarget is not null) _targets.Add(candidate.VerifiedTarget);
            }
        }

        public void Track(Task task)
        {
            ObserveFault(task);
            lock (_gate) _operations.Add(task);
        }

        public void ReleaseWhenSafe()
        {
            Task[] tasks;
            lock (_gate) tasks = _operations.ToArray();
            Task all = Task.WhenAll(tasks);
            ObserveFault(all);
            if (all.IsCompleted) ReleaseAll();
            else _ = all.ContinueWith(_ => ReleaseAll(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void ReleaseAll()
        {
            IProcessTarget[] targets;
            lock (_gate) targets = _targets.ToArray();
            foreach (IProcessTarget target in targets)
            {
                try { target.Dispose(); }
                catch (Exception) { /* Keep releasing other handles. Adapters must not throw. */ }
            }
        }
    }

    private enum TargetResult { Exited, Unverified, Failed }
}
