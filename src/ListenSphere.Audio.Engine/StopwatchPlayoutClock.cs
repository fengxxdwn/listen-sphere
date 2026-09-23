using System.Diagnostics;
using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Audio.Engine;

public sealed class StopwatchPlayoutClock : IPlayoutClock
{
    private readonly long origin = Stopwatch.GetTimestamp();
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(origin);
}

/// <summary>Compensates coarse timer wakeups without accumulating unbounded catch-up work.</summary>
public sealed class PlayoutFrameScheduler(IPlayoutClock clock, TimeSpan frameDuration, int maximumCatchUp = 8)
{
    private TimeSpan origin = clock.Elapsed;
    private long emitted;

    public int TakeDueFrames()
    {
        if (frameDuration <= TimeSpan.Zero || maximumCatchUp < 1)
            throw new InvalidOperationException("A positive frame interval and catch-up bound are required.");
        long due = Math.Max(0, (clock.Elapsed - origin).Ticks / frameDuration.Ticks);
        if (due < emitted) { origin = clock.Elapsed; emitted = 0; return 0; }
        int count = (int)Math.Min(maximumCatchUp, due - emitted);
        emitted = due;
        return count;
    }
}
