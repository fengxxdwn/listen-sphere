using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Audio.Engine;

/// <summary>Sixty-second regression window separates long-term drift from packet arrival jitter.</summary>
public sealed class RemoteClockEstimator : IRemoteClockEstimator
{
    private readonly (double Local, double Source)[] observations = new (double, double)[121];
    private int count, next;
    private ulong lastTimestamp;
    private double sourceSeconds;
    private TimeSpan origin, lastLocal, lastObservation;
    private int rate;
    private bool initialized;
    public double EstimatedDriftPpm { get; private set; }
    public long Discontinuities { get; private set; }

    public double Observe(ulong sourceTimestamp, TimeSpan localTime, int sampleRate = 48_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (!initialized) { Initialize(sourceTimestamp, localTime, sampleRate); return 0; }
        ulong delta = unchecked(sourceTimestamp - lastTimestamp);
        // Some legacy transports expose a 32-bit sample counter; true 64-bit wrap works via subtraction.
        if (sourceTimestamp < lastTimestamp && lastTimestamp <= uint.MaxValue &&
            lastTimestamp > uint.MaxValue - (ulong)sampleRate * 2 && sourceTimestamp < (ulong)sampleRate * 2)
            delta = unchecked((uint)sourceTimestamp - (uint)lastTimestamp);
        if (sampleRate != rate || localTime < lastLocal || localTime - lastLocal > TimeSpan.FromSeconds(2) ||
            delta > (ulong)sampleRate * 2)
        {
            // Small backwards observations are reordered packets, not a new epoch.
            if (sampleRate == rate && localTime >= lastLocal && sourceTimestamp < lastTimestamp &&
                lastTimestamp - sourceTimestamp <= (ulong)sampleRate / 10 && sourceTimestamp != 0)
                return EstimatedDriftPpm;
            Discontinuities++;
            Initialize(sourceTimestamp, localTime, sampleRate);
            return 0;
        }
        if (delta == 0) return EstimatedDriftPpm;
        sourceSeconds += delta / (double)sampleRate;
        lastTimestamp = sourceTimestamp;
        lastLocal = localTime;
        if (localTime - lastObservation < TimeSpan.FromMilliseconds(500)) return EstimatedDriftPpm;
        lastObservation = localTime;
        observations[next] = ((localTime - origin).TotalSeconds, sourceSeconds);
        next = (next + 1) % observations.Length;
        count = Math.Min(count + 1, observations.Length);
        // Do not interpret startup buffering as oscillator drift.
        if (count < 9) return 0;
        double meanX = 0, meanY = 0;
        for (int i = 0; i < count; i++) { meanX += observations[i].Local; meanY += observations[i].Source; }
        meanX /= count; meanY /= count;
        double covariance = 0, variance = 0;
        for (int i = 0; i < count; i++)
        {
            double x = observations[i].Local - meanX;
            covariance += x * (observations[i].Source - meanY);
            variance += x * x;
        }
        if (variance > 0)
        {
            double estimate = Math.Clamp((covariance / variance - 1) * 1_000_000, -1500, 1500);
            EstimatedDriftPpm += (estimate - EstimatedDriftPpm) * 0.15;
        }
        return EstimatedDriftPpm;
    }

    private void Initialize(ulong timestamp, TimeSpan local, int sampleRate)
    {
        initialized = true; lastTimestamp = timestamp; rate = sampleRate;
        origin = lastLocal = lastObservation = local;
        sourceSeconds = 0; EstimatedDriftPpm = 0;
        count = 1; next = 1; observations[0] = (0, 0);
    }

    public void Reset()
    {
        initialized = false; count = next = 0; sourceSeconds = 0;
        EstimatedDriftPpm = 0; Discontinuities = 0;
    }
}
