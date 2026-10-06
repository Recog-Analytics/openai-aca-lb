namespace openai_loadbalancer.Tests;

internal sealed class TestClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
            return now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            TimerCreated.TrySetResult();
            return timer;
        }
    }

    public void Advance(TimeSpan elapsed)
    {
        List<Action> callbacks = [];
        lock (gate)
        {
            now += elapsed;
            foreach (var timer in timers)
                if (!timer.Disposed && timer.Next <= now)
                {
                    timer.Next = timer.Period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + timer.Period;
                    callbacks.Add(timer.Fire);
                }
        }
        foreach (var callback in callbacks)
            callback();
    }

    private sealed class ManualTimer(TestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }
        public DateTimeOffset Next { get; set; }
        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                if (Disposed)
                    return false;
                Next = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.now + dueTime;
                Period = period;
                return true;
            }
        }

        public void Fire() => callback(state);
        public void Dispose()
        {
            lock (clock.gate)
                Disposed = true;
        }
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
