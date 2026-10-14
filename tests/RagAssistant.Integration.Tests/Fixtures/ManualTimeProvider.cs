namespace RagAssistant.Integration.Tests.Fixtures;

/// <summary>
/// A clock that only moves when a test calls <see cref="Advance"/>. Timers (and so <c>Task.Delay(…, TimeProvider)</c>) fire when
/// the clock passes their due time, which makes backoff, polling and heartbeat schedules testable without waiting.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly DateTimeOffset _start;
    private long _ticks;

    public ManualTimeProvider(DateTimeOffset? start = null) => _start = start ?? DateTimeOffset.UtcNow;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>How far the clock has been advanced since it was created.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromTicks(_ticks);
            }
        }
    }

    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _start.AddTicks(_ticks);
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _ticks += delta.Ticks;
            due = [.. _timers.Where(t => t.DueAt <= _ticks).OrderBy(t => t.DueAt)];
        }

        // Callbacks run outside the lock: a fired Task.Delay continues synchronously and may create the next timer.
        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private long _period;

        public long DueAt { get; private set; } = long.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    DueAt = long.MaxValue;
                    return true;
                }

                DueAt = owner._ticks + dueTime.Ticks;
                owner._timers.Add(this);
                return true;
            }
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                if (!owner._timers.Remove(this))
                {
                    return;
                }

                if (_period > 0)
                {
                    DueAt += _period;
                    owner._timers.Add(this);
                }
                else
                {
                    DueAt = long.MaxValue;
                }
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                DueAt = long.MaxValue;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
