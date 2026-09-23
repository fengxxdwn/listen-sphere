using System.Runtime.InteropServices;
using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Audio.Engine;

/// <summary>Small-ratio Float32 stereo correction with continuous phase across frame boundaries.</summary>
public sealed class AdaptiveLinearResampler : IAdaptiveAudioResampler
{
    public const double MinimumRatio = 0.998;
    public const double MaximumRatio = 1.002;
    private double ratio = 1, position;
    private float previousLeft, previousRight;
    private bool hasPrevious;
    public AudioFormat InputFormat => AudioFormat.Default;
    public AudioFormat OutputFormat => AudioFormat.Default;
    public double Ratio
    {
        get => ratio;
        set
        {
            if (!double.IsFinite(value) || value < MinimumRatio || value > MaximumRatio)
                throw new ArgumentOutOfRangeException(nameof(value));
            ratio = value;
        }
    }

    public int GetMaximumOutputBytes(int inputBytes)
    {
        if (inputBytes < 0 || inputBytes % 8 != 0) throw new ArgumentOutOfRangeException(nameof(inputBytes));
        return checked(((int)Math.Ceiling(inputBytes / 8d / MinimumRatio) + 2) * 8);
    }

    public int Convert(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length % 8 != 0 || output.Length % 8 != 0)
            throw new ArgumentException("Complete Float32 stereo samples are required.");
        if (input.IsEmpty) return 0;
        if (input.Overlaps(output)) throw new ArgumentException("Input and output must not overlap.");
        int frames = input.Length / 8;
        int requiredFrames = Math.Max(0, (int)Math.Ceiling((frames - 1 - position) / ratio));
        if (output.Length / 8 < requiredFrames) throw new ArgumentException("Output is too small.", nameof(output));
        var source = MemoryMarshal.Cast<byte, float>(input);
        var target = MemoryMarshal.Cast<byte, float>(output);
        int written = 0;
        while (position < frames - 1)
        {
            int left = (int)Math.Floor(position);
            float fraction = (float)(position - left);
            for (int channel = 0; channel < 2; channel++)
            {
                float a = left < 0 && hasPrevious
                    ? (channel == 0 ? previousLeft : previousRight) : source[Math.Max(left, 0) * 2 + channel];
                float b = source[(left + 1) * 2 + channel];
                target[written * 2 + channel] = a + (b - a) * fraction;
            }
            written++;
            position += ratio;
        }
        position -= frames;
        previousLeft = source[^2]; previousRight = source[^1]; hasPrevious = true;
        return written * 8;
    }

    public void Reset()
    {
        position = 0; ratio = 1; hasPrevious = false;
        previousLeft = previousRight = 0;
    }
}
