namespace ListenSphere.Audio.Engine;

/// <summary>Ten-band stereo graphic equalizer for interleaved Float32 PCM.</summary>
public sealed class GraphicEqualizer(int sampleRate = 48_000)
{
    public static readonly int[] Frequencies = [20, 60, 100, 250, 500, 1_000, 2_000, 4_000, 8_000, 20_000];
    private readonly BiquadState[,] states = new BiquadState[2, Frequencies.Length];
    private readonly BiquadCoefficients[] coefficients = new BiquadCoefficients[Frequencies.Length];
    private readonly float[] previousGains = Enumerable.Repeat(float.NaN, Frequencies.Length).ToArray();
    private readonly int sampleRate = sampleRate;

    public void Process(Span<float> interleavedStereo, IReadOnlyList<float> gainsDb)
    {
        if (interleavedStereo.Length % 2 != 0)
            throw new ArgumentException("Stereo PCM must contain complete sample pairs.", nameof(interleavedStereo));
        if (gainsDb.Count != Frequencies.Length)
            throw new ArgumentException("The graphic equalizer requires ten gain values.", nameof(gainsDb));

        UpdateCoefficients(gainsDb);
        for (int index = 0; index < interleavedStereo.Length; index++)
        {
            int channel = index & 1;
            float sample = interleavedStereo[index];
            for (int band = 0; band < Frequencies.Length; band++)
            {
                ref BiquadState state = ref states[channel, band];
                BiquadCoefficients c = coefficients[band];
                float output = (c.B0 * sample) + state.Z1;
                state.Z1 = (c.B1 * sample) - (c.A1 * output) + state.Z2;
                state.Z2 = (c.B2 * sample) - (c.A2 * output);
                sample = output;
            }
            interleavedStereo[index] = Math.Clamp(sample, -1f, 1f);
        }
    }

    private void UpdateCoefficients(IReadOnlyList<float> gainsDb)
    {
        for (int band = 0; band < Frequencies.Length; band++)
        {
            float gain = Math.Clamp(gainsDb[band], -20f, 20f);
            if (gain == previousGains[band]) continue;
            previousGains[band] = gain;
            coefficients[band] = CreatePeaking(Frequencies[band], gain);
        }
    }

    private BiquadCoefficients CreatePeaking(float frequency, float gainDb)
    {
        const float q = 1.35f;
        float amplitude = MathF.Pow(10f, gainDb / 40f);
        float omega = 2f * MathF.PI * frequency / sampleRate;
        float alpha = MathF.Sin(omega) / (2f * q);
        float cosine = MathF.Cos(omega);
        float a0 = 1f + (alpha / amplitude);
        return new BiquadCoefficients(
            (1f + (alpha * amplitude)) / a0,
            (-2f * cosine) / a0,
            (1f - (alpha * amplitude)) / a0,
            (-2f * cosine) / a0,
            (1f - (alpha / amplitude)) / a0);
    }

    private readonly record struct BiquadCoefficients(float B0, float B1, float B2, float A1, float A2);
    private struct BiquadState { public float Z1; public float Z2; }
}
