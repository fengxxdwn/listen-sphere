using System.Runtime.InteropServices;

namespace ListenSphere.Audio.Engine;

/// <summary>Applies a linear gain to normalized Float32 PCM without allocating.</summary>
public static class PcmGainProcessor
{
    public static void Apply(Span<byte> pcm, float gain)
    {
        if (pcm.Length % sizeof(float) != 0)
        {
            throw new ArgumentException("Float32 PCM length must be divisible by four.", nameof(pcm));
        }

        float normalizedGain = Math.Clamp(gain, 0f, 1f);
        Span<float> samples = MemoryMarshal.Cast<byte, float>(pcm);
        if (normalizedGain == 0f)
        {
            samples.Clear();
            return;
        }

        if (normalizedGain == 1f)
        {
            return;
        }

        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] *= normalizedGain;
        }
    }
}
