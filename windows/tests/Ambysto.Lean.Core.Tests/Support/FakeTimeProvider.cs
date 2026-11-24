using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ambysto.Lean.Core.Tests.Support;

/// <summary>A clock the test moves by hand. Timers made through it (so <c>Task.Delay(…, timeProvider)</c>) fire when
/// <see cref="Advance"/> passes their due time, in due order. It has two clocks, as a real machine does: the wall clock
/// (<see cref="GetUtcNow"/>), which <see cref="SetWallClock"/> can set to any time (a user or a time sync changing the
/// system time), and the monotonic timestamp (<see cref="GetTimestamp"/>), which only <see cref="Advance"/> moves, and
/// only forward. Timers run on the timestamp, as real ones do.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<FakeTimer> timers = [];
    private DateTimeOffset now;
    private long ticks;

    public FakeTimeProvider(DateTimeOffset start)
    {
        now = start;
        ticks = start.UtcTicks;
    }

    public FakeTimeProvider() : this(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (gate)
        {
            return ticks;
        }
    }

    /// <summary>Sets the wall clock only (the system time changed); the timestamp and every timer stay where they are.</summary>
    public void SetWallClock(DateTimeOffset wall)
    {
        lock (gate)
        {
            now = wall;
        }
    }

    /// <summary>Moves both clocks forward by <paramref name="by"/>, firing every timer due on the way.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        long target;
        lock (gate)
        {
            target = ticks + by.Ticks;
        }

        while (true)
        {
            FakeTimer? next = null;
            lock (gate)
            {
                foreach (var timer in timers)
                {
                    if (timer.Due is { } due && due <= target && (next is null || due < next.Due))
                    {
                        next = timer;
                    }
                }

                if (next is null)
                {
                    now = now.AddTicks(target - ticks);
                    ticks = target;
                    return;
                }

                var at = next.Due!.Value;
                now = now.AddTicks(at - ticks);
                ticks = at;
                next.Reschedule();
            }

            next.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (gate)
        {
            timers.Add(timer);
        }

        return timer;
    }

    private void Remove(FakeTimer timer)
    {
        lock (gate)
        {
            timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan period = Timeout.InfiniteTimeSpan;

        /// <summary>The timestamp (ticks) at which the timer fires next, or null.</summary>
        public long? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            lock (owner.gate)
            {
                period = newPeriod;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.ticks + dueTime.Ticks;
            }

            return true;
        }

        public void Reschedule() => Due = period == Timeout.InfiniteTimeSpan ? null : Due!.Value + period.Ticks;

        public void Fire() => callback(state);

        public void Dispose()
        {
            Due = null;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
