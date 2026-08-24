namespace ListenSphere.Audio.Engine;

public readonly record struct AudioLevel(float Peak, float Rms, bool IsClipping)
{
    public static AudioLevel Silence { get; } = new(0, 0, false);
}

/// <summary>Calculates allocation-free peak and RMS values for normalized float PCM.</summary>
public static class AudioLevelCalculator
{
    public static AudioLevel Calculate(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return AudioLevel.Silence;
        }

        double sumOfSquares = 0;
        var peak = 0f;
        foreach (var sample in samples)
        {
            var absolute = MathF.Abs(sample);
            peak = MathF.Max(peak, absolute);
            sumOfSquares += (double)sample * sample;
        }

        return new AudioLevel(
            peak,
            (float)Math.Sqrt(sumOfSquares / samples.Length),
            peak >= 1f);
    }
}

