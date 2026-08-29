namespace ListenSphere.Network;

public readonly record struct ReconnectBackoffDecision(
    bool CanAttempt,
    int FailureCount,
    TimeSpan RetryAfter);

/// <summary>
/// Maintains bounded reconnect backoff independently for each remote device.
/// Explicit user connections can reset one target without affecting the others.
/// </summary>
public sealed class ReconnectBackoffTracker
{
    private static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30)
    ];
    private readonly object gate = new();
    private readonly Dictionary<Guid, ReconnectState> states = [];
    private readonly IReadOnlyList<TimeSpan> delays;

    public ReconnectBackoffTracker(IReadOnlyList<TimeSpan>? delays = null)
    {
        this.delays = delays ?? DefaultDelays;
        if (this.delays.Count == 0 || this.delays.Any(delay => delay < TimeSpan.Zero))
        {
            throw new ArgumentException("Reconnect delays must contain non-negative values.", nameof(delays));
        }
    }

    public ReconnectBackoffDecision Check(Guid targetId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(targetId, Guid.Empty);
        lock (gate)
        {
            if (!states.TryGetValue(targetId, out ReconnectState? state))
            {
                return new ReconnectBackoffDecision(true, 0, TimeSpan.Zero);
            }

            TimeSpan remaining = state.NextAttemptAt - now;
            return new ReconnectBackoffDecision(
                remaining <= TimeSpan.Zero,
                state.FailureCount,
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
    }

    public ReconnectBackoffDecision RecordFailure(Guid targetId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(targetId, Guid.Empty);
        lock (gate)
        {
            states.TryGetValue(targetId, out ReconnectState? previous);
            int failureCount = (previous?.FailureCount ?? 0) + 1;
            TimeSpan delay = delays[Math.Min(failureCount - 1, delays.Count - 1)];
            states[targetId] = new ReconnectState(failureCount, now + delay);
            return new ReconnectBackoffDecision(false, failureCount, delay);
        }
    }

    public void Reset(Guid targetId)
    {
        lock (gate)
        {
            states.Remove(targetId);
        }
    }

    private sealed record ReconnectState(int FailureCount, DateTimeOffset NextAttemptAt);
}
