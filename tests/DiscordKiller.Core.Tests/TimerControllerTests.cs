using DiscordKiller.Core;
using Xunit;

namespace DiscordKiller.Core.Tests;

public sealed class TimerControllerTests
{
    private sealed class FakeClock : IMonotonicClock
    {
        public long ElapsedMilliseconds { get; set; }
    }

    private sealed class FakeCloser : IDiscordCloser
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource<CloseOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CloseOutcome> CloseAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Completion.Task;
        }
    }

    private sealed class ThrowingCloser : IDiscordCloser
    {
        public Task<CloseOutcome> CloseAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated shutdown failure");
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("720", 720)]
    [InlineData("0001", 1)]
    [InlineData("030", 30)]
    public void ValidWholeAsciiMinutesAreAccepted(string draft, int expected)
    {
        var timer = new TimerController(new FakeClock(), new FakeCloser());
        timer.SetDraft(draft);
        Assert.True(timer.IsValid);
        Assert.Equal(expected, timer.Minutes);
        Assert.True(timer.Start());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("721")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData(" 30")]
    [InlineData("30 ")]
    [InlineData("12m")]
    [InlineData("１２")]
    [InlineData("9999999999999999999999999999")]
    public void InvalidInputNeverStartsPreviousValue(string draft)
    {
        var clock = new FakeClock();
        var closer = new FakeCloser();
        var timer = new TimerController(clock, closer);
        timer.SetDraft("45");
        timer.SetDraft(draft);
        Assert.Equal(draft, timer.Draft);
        Assert.False(timer.IsValid);
        Assert.Null(timer.Minutes);
        Assert.False(timer.Start());
        Assert.Equal(TimerState.Idle, timer.State);
        Assert.Equal(0, closer.Calls);
    }

    [Fact]
    public void PresetAndDraftUseOneSharedValue()
    {
        var timer = new TimerController(new FakeClock(), new FakeCloser());
        timer.SetDraft("");
        timer.SelectPreset(60);
        Assert.Equal("60", timer.Draft);
        Assert.True(timer.IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.SelectPreset(721));
    }

    [Fact]
    public void RemainingTimeUsesMonotonicElapsedTimeIncludingSleep()
    {
        var clock = new FakeClock { ElapsedMilliseconds = 1_000 };
        var timer = new TimerController(clock, new FakeCloser());
        timer.SetDraft("720");
        Assert.True(timer.Start());
        Assert.Equal(TimeSpan.FromMinutes(720), timer.Remaining);
        Assert.Equal(1d, timer.Progress);
        clock.ElapsedMilliseconds += 60_000;
        Assert.Equal(TimeSpan.FromMinutes(719), timer.Remaining);
        Assert.InRange(timer.Progress, 0.998, 0.999);
    }

    [Fact]
    public void RunningTimerRejectsNewDraftPresetAndDuplicateStart()
    {
        var timer = new TimerController(new FakeClock(), new FakeCloser());
        Assert.True(timer.Start());
        timer.SetDraft("15");
        timer.SelectPreset(60);
        Assert.Equal("30", timer.Draft);
        Assert.False(timer.Start());
    }

    [Fact]
    public async Task CancelBeforeExpiryPreventsCloseEvenIfATickArrives()
    {
        var clock = new FakeClock();
        var closer = new FakeCloser();
        var timer = new TimerController(clock, closer);
        timer.SetDraft("1");
        Assert.True(timer.Start());
        Assert.True(timer.Cancel());
        clock.ElapsedMilliseconds = 60_000;
        await timer.TickAsync();
        Assert.Equal(TimerState.Idle, timer.State);
        Assert.Equal(0, closer.Calls);
    }

    [Fact]
    public async Task ExpiryAndResumeTicksCauseOnlyOneCloseAndBlockCancel()
    {
        var clock = new FakeClock();
        var closer = new FakeCloser();
        var timer = new TimerController(clock, closer);
        timer.SetDraft("1");
        Assert.True(timer.Start());
        clock.ElapsedMilliseconds = 90_000;
        Task close = timer.TickAsync();
        await timer.TickAsync();
        Assert.Equal(TimerState.Closing, timer.State);
        Assert.False(timer.Cancel());
        Assert.False(timer.Start());
        Assert.Equal(1, closer.Calls);
        closer.Completion.SetResult(CloseOutcome.Closed);
        await close;
        Assert.Equal(TimerState.Completed, timer.State);
        Assert.Equal(CloseOutcome.Closed, timer.Outcome);
    }

    [Fact]
    public async Task TickJustBeforeDeadlineDoesNotClose()
    {
        var clock = new FakeClock();
        var closer = new FakeCloser();
        var timer = new TimerController(clock, closer);
        timer.SetDraft("1");
        timer.Start();
        clock.ElapsedMilliseconds = 59_999;
        await timer.TickAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(1), timer.Remaining);
        Assert.Equal(TimerState.Running, timer.State);
        Assert.Equal(0, closer.Calls);
    }

    [Fact]
    public async Task SimultaneousExpiryTicksInvokeCloserAtMostOnce()
    {
        var clock = new FakeClock();
        var closer = new FakeCloser();
        var timer = new TimerController(clock, closer);
        timer.SetDraft("1");
        timer.Start();
        clock.ElapsedMilliseconds = 60_000;
        Task[] ticks = Enumerable.Range(0, 32).Select(_ => Task.Run(timer.TickAsync)).ToArray();
        // Release the one allowed call; additional ticks must observe Closing or Completed.
        closer.Completion.SetResult(CloseOutcome.AlreadyClosed);
        await Task.WhenAll(ticks);
        Assert.Equal(1, closer.Calls);
        Assert.Equal(TimerState.Completed, timer.State);
    }

    [Fact]
    public async Task ConcurrentCancelAndExpiryOnlyOneTransitionWins()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            var clock = new FakeClock();
            var closer = new FakeCloser();
            var timer = new TimerController(clock, closer);
            timer.SetDraft("1");
            timer.Start();
            clock.ElapsedMilliseconds = 60_000;
            using var gate = new ManualResetEventSlim(false);
            Task<bool> cancel = Task.Run(() => { gate.Wait(); return timer.Cancel(); });
            Task tick = Task.Run(async () => { gate.Wait(); await timer.TickAsync(); });
            gate.Set();
            bool cancelled = await cancel;
            closer.Completion.TrySetResult(CloseOutcome.AlreadyClosed);
            await tick;
            Assert.Equal(cancelled ? 0 : 1, closer.Calls);
            Assert.Equal(cancelled ? TimerState.Idle : TimerState.Completed, timer.State);
        }
    }

    [Theory]
    [InlineData(CloseOutcome.Unverified)]
    [InlineData(CloseOutcome.Partial)]
    [InlineData(CloseOutcome.Error)]
    public async Task UnverifiedOrPartialClosureIsNeverReportedAsSuccess(CloseOutcome outcome)
    {
        var clock = new FakeClock();
        var closer = new FakeCloser();
        var timer = new TimerController(clock, closer);
        timer.SetDraft("1");
        Assert.True(timer.Start());
        clock.ElapsedMilliseconds = 60_000;
        Task close = timer.TickAsync();
        closer.Completion.SetResult(outcome);
        await close;
        Assert.Equal(TimerState.Error, timer.State);
        Assert.Equal(outcome, timer.Outcome);
    }

    [Fact]
    public async Task AnAlreadyClosedDiscordIsACompletedOutcome()
    {
        var clock = new FakeClock();
        var closer = new FakeCloser();
        var timer = new TimerController(clock, closer);
        timer.SetDraft("1");
        Assert.True(timer.Start());
        clock.ElapsedMilliseconds = 60_000;
        Task close = timer.TickAsync();
        closer.Completion.SetResult(CloseOutcome.AlreadyClosed);
        await close;
        Assert.Equal(TimerState.Completed, timer.State);
        Assert.Equal(CloseOutcome.AlreadyClosed, timer.Outcome);
    }

    [Fact]
    public async Task ExceptionDuringCloseProducesError()
    {
        var clock = new FakeClock();
        var timer = new TimerController(clock, new ThrowingCloser());
        timer.SetDraft("1");
        timer.Start();
        clock.ElapsedMilliseconds = 60_000;
        await timer.TickAsync();
        Assert.Equal(TimerState.Error, timer.State);
        Assert.Equal(CloseOutcome.Error, timer.Outcome);
    }

    [Fact]
    public async Task CompletionStatusStaysUntilAnotherStart()
    {
        var clock = new FakeClock();
        var closer = new FakeCloser();
        var timer = new TimerController(clock, closer);
        timer.SetDraft("1");
        timer.Start();
        clock.ElapsedMilliseconds = 60_000;
        Task close = timer.TickAsync();
        closer.Completion.SetResult(CloseOutcome.Unverified);
        await close;
        timer.SetDraft("2");
        Assert.Equal(CloseOutcome.Unverified, timer.Outcome);
        Assert.True(timer.Start());
        Assert.Null(timer.Outcome);
    }
}
