using System.Globalization;

namespace DiscordKiller.Core;

public enum TimerState { Idle, Running, Closing, Completed, Error }

public enum CloseOutcome { Closed, AlreadyClosed, Unverified, Partial, Error }

/// <summary>Elapsed milliseconds since a monotonic origin, including time asleep.</summary>
public interface IMonotonicClock
{
    long ElapsedMilliseconds { get; }
}

/// <summary>
/// Reports actual outcomes; implementations must not report Closed for a mere
/// request to close or act on a process whose identity cannot be confirmed.
/// </summary>
public interface IDiscordCloser
{
    Task<CloseOutcome> CloseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Shared timer state. The host calls TickAsync on a regular UI tick and on
/// system resume. Changed can run on any thread; UI hosts must marshal it.
/// </summary>
public sealed class TimerController
{
    private readonly IMonotonicClock _clock;
    private readonly IDiscordCloser _closer;
    private readonly object _gate = new();
    private string _draft = "30";
    private TimerState _state = TimerState.Idle;
    private CloseOutcome? _outcome;
    private long _startedAt;
    private long _durationMilliseconds;

    public TimerController(IMonotonicClock clock, IDiscordCloser closer)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _closer = closer ?? throw new ArgumentNullException(nameof(closer));
    }

    public event Action? Changed;

    public string Draft { get { lock (_gate) return _draft; } }
    public int? Minutes { get { lock (_gate) return ParseMinutes(_draft); } }
    public bool IsValid { get { lock (_gate) return ParseMinutes(_draft).HasValue; } }
    public TimerState State { get { lock (_gate) return _state; } }
    public CloseOutcome? Outcome { get { lock (_gate) return _outcome; } }

    public TimeSpan Remaining
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromMilliseconds(RemainingMilliseconds());
            }
        }
    }

    public double Progress
    {
        get
        {
            lock (_gate)
            {
                return _state == TimerState.Running && _durationMilliseconds > 0
                    ? (double)RemainingMilliseconds() / _durationMilliseconds
                    : 0;
            }
        }
    }

    public void SetDraft(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (_state is TimerState.Running or TimerState.Closing || _draft == value)
                return;
            _draft = value;
        }
        Changed?.Invoke();
    }

    public void SelectPreset(int minutes)
    {
        if (minutes is < 1 or > 720)
            throw new ArgumentOutOfRangeException(nameof(minutes));
        SetDraft(minutes.ToString(CultureInfo.InvariantCulture));
    }

    public bool Start()
    {
        lock (_gate)
        {
            int? minutes = ParseMinutes(_draft);
            if (_state is TimerState.Running or TimerState.Closing || minutes is null)
                return false;

            _startedAt = _clock.ElapsedMilliseconds;
            _durationMilliseconds = minutes.Value * 60_000L;
            _outcome = null;
            _state = TimerState.Running;
        }
        Changed?.Invoke();
        return true;
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_state != TimerState.Running)
                return false;
            _state = TimerState.Idle;
            _outcome = null;
        }
        Changed?.Invoke();
        return true;
    }

    public async Task TickAsync()
    {
        bool shouldClose;
        lock (_gate)
        {
            if (_state != TimerState.Running)
                return;
            shouldClose = RemainingMilliseconds() == 0;
            if (shouldClose)
                _state = TimerState.Closing;
        }

        Changed?.Invoke();
        if (!shouldClose)
            return;

        CloseOutcome outcome;
        try
        {
            outcome = await _closer.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            outcome = CloseOutcome.Error;
        }

        lock (_gate)
        {
            _outcome = outcome;
            _state = outcome is CloseOutcome.Closed or CloseOutcome.AlreadyClosed
                ? TimerState.Completed
                : TimerState.Error;
        }
        Changed?.Invoke();
    }

    private long RemainingMilliseconds()
    {
        if (_state != TimerState.Running)
            return 0;
        long now = _clock.ElapsedMilliseconds;
        long elapsed = now <= _startedAt ? 0 : Math.Min(now - _startedAt, _durationMilliseconds);
        return _durationMilliseconds - elapsed;
    }

    private static int? ParseMinutes(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        foreach (char digit in value)
            if (digit is < '0' or > '9') return null;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
               && minutes is >= 1 and <= 720 ? minutes : null;
    }
}
