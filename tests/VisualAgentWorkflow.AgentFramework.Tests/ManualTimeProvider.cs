namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>Deterministic clock: time moves only when a test calls <see cref="Advance"/>; due timers fire synchronously.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
    public override long GetTimestamp() { lock (gate) return now.UtcTicks; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public int ActiveTimerCount { get { lock (gate) return timers.Count(timer => timer.Due is not null); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate) timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        var target = GetUtcNow() + delta;
        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers.Where(timer => timer.Due is { } due && due <= target)
                    .OrderBy(timer => timer.Due).FirstOrDefault();
                if (next is null) { now = target; return; }
                if (next.Due!.Value > now) now = next.Due.Value;
                next.Fire();
            }
            next.Invoke();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }
        private TimeSpan period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            lock (owner.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                period = newPeriod;
            }
            return true;
        }

        public void Fire() => Due = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : owner.now + period;
        public void Invoke() => callback(state);
        public void Dispose() { lock (owner.gate) { Due = null; owner.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
