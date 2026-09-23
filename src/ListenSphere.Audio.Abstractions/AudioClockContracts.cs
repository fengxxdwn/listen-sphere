namespace ListenSphere.Audio.Abstractions;

/// <summary>Monotonic local playout time, replaceable by a deterministic clock in tests.</summary>
public interface IPlayoutClock
{
    TimeSpan Elapsed { get; }
}

/// <summary>Positive drift means the remote sample clock is faster than local time.</summary>
public interface IRemoteClockEstimator
{
    double EstimatedDriftPpm { get; }
    long Discontinuities { get; }
    double Observe(ulong sourceTimestamp, TimeSpan localTime, int sampleRate = 48_000);
    void Reset();
}

/// <summary>Streaming conversion. Ratio is input samples per output sample; output is caller-owned.</summary>
public interface IAdaptiveAudioResampler : IAudioResampler
{
    double Ratio { get; set; }
    int GetMaximumOutputBytes(int inputBytes);
    void Reset();
}
