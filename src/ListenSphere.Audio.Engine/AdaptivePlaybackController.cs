using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Audio.Engine;

public enum BufferCorrection { None, RecordUnderrun, DropFrame }

/// <summary>Combines remote clock observation and slowly varying buffer feedback.</summary>
public sealed class AdaptivePlaybackController(
    int targetMilliseconds = 60,
    int toleranceMilliseconds = 40,
    IRemoteClockEstimator? clockEstimator = null)
{
    private readonly IRemoteClockEstimator estimator = clockEstimator ?? new RemoteClockEstimator();
    private double filteredError, correctionPpm;
    public double EstimatedDriftPpm => estimator.EstimatedDriftPpm;
    public long ClockDiscontinuities => estimator.Discontinuities;

    public BufferCorrection EvaluateBuffer(int bufferedMilliseconds)
    {
        // Whole-frame dropping is reserved for an emergency, not routine oscillator correction.
        if (bufferedMilliseconds > targetMilliseconds + Math.Max(120, toleranceMilliseconds * 3))
            return BufferCorrection.DropFrame;
        return bufferedMilliseconds < Math.Max(0, targetMilliseconds - toleranceMilliseconds)
            ? BufferCorrection.RecordUnderrun : BufferCorrection.None;
    }

    public double ObserveClock(ulong sourceTimestamp, TimeSpan localTime, int sampleRate = 48_000) =>
        estimator.Observe(sourceTimestamp, localTime, sampleRate);

    public double GetResamplingRatio(double bufferedMilliseconds, double frameSeconds = 0.01)
    {
        if (!double.IsFinite(bufferedMilliseconds) || !double.IsFinite(frameSeconds) || frameSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferedMilliseconds));
        double dt = Math.Min(frameSeconds, 0.1);
        filteredError += (bufferedMilliseconds - targetMilliseconds - filteredError) * (1 - Math.Exp(-dt / 2));
        double requested = Math.Clamp(EstimatedDriftPpm + filteredError * 15, -2000, 2000);
        correctionPpm += Math.Clamp(requested - correctionPpm, -100 * dt, 100 * dt);
        return 1 + correctionPpm / 1_000_000;
    }

    public void Reset()
    {
        estimator.Reset(); filteredError = correctionPpm = 0;
    }
}