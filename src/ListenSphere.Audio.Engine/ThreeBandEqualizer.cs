namespace ListenSphere.Audio.Engine;

/// <summary>A lightweight stateful three-band tone control for interleaved stereo Float32 PCM.</summary>
public sealed class ThreeBandEqualizer(int sampleRate = 48_000)
{
    private readonly float lowAlpha = CalculateAlpha(200, sampleRate);
    private readonly float highAlpha = CalculateAlpha(4_000, sampleRate);
    private readonly float[] lowState = new float[2];
    private readonly float[] highLowState = new float[2];

    public void Process(Span<float> interleavedStereo, float bassDb, float midDb, float trebleDb)
    {
        if (interleavedStereo.Length % 2 != 0)
        {
            throw new ArgumentException("Stereo PCM must contain complete sample pairs.", nameof(interleavedStereo));
        }

        float bassGain = DbToGain(bassDb);
        float midGain = DbToGain(midDb);
        float trebleGain = DbToGain(trebleDb);
        for (int index = 0; index < interleavedStereo.Length; index++)
        {
            int channel = index & 1;
            float input = interleavedStereo[index];
            lowState[channel] += lowAlpha * (input - lowState[channel]);
            highLowState[channel] += highAlpha * (input - highLowState[channel]);
            float low = lowState[channel];
            float high = input - highLowState[channel];
            float mid = input - low - high;
            interleavedStereo[index] = Math.Clamp(
                (low * bassGain) + (mid * midGain) + (high * trebleGain),
                -1f,
                1f);
        }
    }

    private static float CalculateAlpha(float cutoff, int sampleRate) =>
        1f - MathF.Exp(-2f * MathF.PI * cutoff / sampleRate);

    private static float DbToGain(float decibels) =>
        MathF.Pow(10f, Math.Clamp(decibels, -12f, 12f) / 20f);
}
