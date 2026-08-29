using System.Runtime.InteropServices;

namespace ListenSphere.Audio.Engine;

public sealed record MasterLimiterStatistics(long LimitedSamples, float MaximumInputPeak);

/// <summary>
/// Applies a continuous soft knee above the threshold and keeps Float32 PCM below
/// the configured ceiling. Samples below the threshold remain bit-for-bit unchanged.
/// </summary>
public sealed class MasterSoftLimiter(float threshold = 0.9f, float ceiling = 0.98f)
{
    private long limitedSamples;
    private float maximumInputPeak;

    public MasterLimiterStatistics Statistics =>
        new(Interlocked.Read(ref limitedSamples), Volatile.Read(ref maximumInputPeak));

    public void Process(Span<byte> pcm)
    {
        if (pcm.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                "Float32 PCM length must be divisible by four.",
                nameof(pcm));
        }
        if (threshold <= 0f || threshold >= ceiling || ceiling > 1f)
        {
            throw new InvalidOperationException(
                "Limiter settings must satisfy 0 < threshold < ceiling <= 1.");
        }

        Span<float> samples = MemoryMarshal.Cast<byte, float>(pcm);
        float kneeWidth = ceiling - threshold;
        foreach (ref float sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                sample = 0f;
                continue;
            }

            float magnitude = MathF.Abs(sample);
            UpdateMaximumPeak(magnitude);
            if (magnitude <= threshold)
            {
                continue;
            }

            float compressed = threshold +
                (kneeWidth * (1f - MathF.Exp(-(magnitude - threshold) / kneeWidth)));
            sample = MathF.CopySign(MathF.Min(compressed, ceiling), sample);
            Interlocked.Increment(ref limitedSamples);
        }
    }

    private void UpdateMaximumPeak(float candidate)
    {
        float current = Volatile.Read(ref maximumInputPeak);
        while (candidate > current)
        {
            float observed = Interlocked.CompareExchange(
                ref maximumInputPeak,
                candidate,
                current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }
}
