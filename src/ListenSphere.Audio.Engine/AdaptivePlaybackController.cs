namespace ListenSphere.Audio.Engine;

public enum BufferCorrection
{
    None,
    RecordUnderrun,
    DropFrame
}

/// <summary>
/// Keeps playback latency bounded and estimates sender-to-receiver clock drift.
/// </summary>
public sealed class AdaptivePlaybackController(
    int targetMilliseconds = 60,
    int toleranceMilliseconds = 40)
{
    private ulong? firstTimestamp;
    private TimeSpan firstLocalTime;
    private double estimatedDriftPpm;

    public double EstimatedDriftPpm => estimatedDriftPpm;

    public BufferCorrection EvaluateBuffer(int bufferedMilliseconds)
    {
        if (bufferedMilliseconds > targetMilliseconds + toleranceMilliseconds)
        {
            return BufferCorrection.DropFrame;
        }

        return bufferedMilliseconds < Math.Max(0, targetMilliseconds - toleranceMilliseconds)
            ? BufferCorrection.RecordUnderrun
            : BufferCorrection.None;
    }

    public double ObserveClock(
        ulong sourceTimestamp,
        TimeSpan localTime,
        int sampleRate = 48_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (firstTimestamp is null)
        {
            firstTimestamp = sourceTimestamp;
            firstLocalTime = localTime;
            return estimatedDriftPpm;
        }

        if (sourceTimestamp < firstTimestamp.Value)
        {
            firstTimestamp = sourceTimestamp;
            firstLocalTime = localTime;
            estimatedDriftPpm = 0;
            return estimatedDriftPpm;
        }

        double localSeconds = (localTime - firstLocalTime).TotalSeconds;
        if (localSeconds < 0.25)
        {
            return estimatedDriftPpm;
        }

        double sourceSeconds = (sourceTimestamp - firstTimestamp.Value) / (double)sampleRate;
        double sample = Math.Clamp(
            (sourceSeconds - localSeconds) / localSeconds * 1_000_000,
            -10_000,
            10_000);
        estimatedDriftPpm = estimatedDriftPpm == 0
            ? sample
            : (estimatedDriftPpm * 0.9) + (sample * 0.1);
        return estimatedDriftPpm;
    }

    public void Reset()
    {
        firstTimestamp = null;
        firstLocalTime = default;
        estimatedDriftPpm = 0;
    }
}
