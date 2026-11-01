namespace MailWarden.Presentation.Tests.TestSupport;

/// <summary>Central per-test timeout used as <c>[Fact(Timeout = TestTimeouts.PerTestMilliseconds)]</c>.</summary>
internal static class TestTimeouts
{
    /// <summary>Default per-test timeout (30 s) from the project test configuration.</summary>
    public const int PerTestMilliseconds = 30_000;
}

/// <summary>
/// Time provider with a fixed instant. The local time zone is the machine's own, so a view model that uses either
/// <c>TimeProvider.LocalTimeZone</c> or <c>TimeZoneInfo.Local</c> sees the same zone as the test expectations.
/// </summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}

/// <summary>Polling helpers for work that view models start fire-and-forget (property setters, commands).</summary>
internal static class Wait
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Polls until the condition holds; fails after 10 s with the description.</summary>
    public static async Task UntilAsync(Func<bool> condition, string description)
    {
        DateTime deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {description}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Gives the code under test a short real-time window in which something unwanted could still happen.</summary>
    public static Task NegativeWindowAsync() => Task.Delay(300);
}

/// <summary>Unique directory below the test bin folder, removed on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        RootPath = Path.Combine(AppContext.BaseDirectory, "ap04a-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}

/// <summary>
/// Manually driven time provider (stand-in for a FakeTimeProvider): <see cref="CreateTimer"/> hands out timers that only
/// fire when <see cref="Advance"/> moves the clock past their due time, so waits like <c>Task.Delay(limit, provider)</c>
/// need no real time.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;
    private int _timersCreated;

    /// <summary>Timers created so far, disposed or not.</summary>
    public int TimersCreated
    {
        get
        {
            lock (_gate)
            {
                return _timersCreated;
            }
        }
    }

    /// <summary>Timers that are neither disposed nor fired once.</summary>
    public int ActiveTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => timer.IsArmed);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            _timersCreated++;
            return timer;
        }
    }

    /// <summary>Moves the clock forward and fires every timer that became due (outside the lock).</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(timer => timer.IsDue(_now))];
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private DateTimeOffset Now()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _dueAt;
        private TimeSpan _period;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            Arm(dueTime, period);
        }

        public bool IsArmed => _dueAt is not null;

        public bool IsDue(DateTimeOffset now) => _dueAt is { } due && due <= now;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Arm(dueTime, period);
            return true;
        }

        public void Fire()
        {
            DateTimeOffset now = _owner.Now();
            _dueAt = _period > TimeSpan.Zero ? now + _period : null;
            _callback(_state);
        }

        public void Dispose()
        {
            _dueAt = null;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void Arm(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.Now() + dueTime;
        }
    }
}