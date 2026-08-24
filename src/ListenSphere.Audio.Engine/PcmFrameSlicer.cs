using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Audio.Engine;

/// <summary>
/// Reassembles arbitrary normalized PCM chunks into fixed-duration frames. Frame memory
/// is borrowed and valid only for the duration of the callback.
/// </summary>
public sealed class PcmFrameSlicer
{
    private readonly AudioFormat format;
    private readonly int frameSamples;
    private readonly byte[] frameBuffer;
    private int bufferedBytes;
    private ulong timestamp;

    public PcmFrameSlicer(AudioFormat format, int frameDurationMilliseconds)
    {
        if (format.SampleRate <= 0 || format.ChannelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (format.SampleFormat != AudioSampleFormat.Float32LittleEndian)
        {
            throw new NotSupportedException("P1 supports Float32 little-endian PCM only.");
        }

        if (frameDurationMilliseconds <= 0 ||
            format.SampleRate * frameDurationMilliseconds % 1_000 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameDurationMilliseconds));
        }

        this.format = format;
        frameSamples = format.SampleRate * frameDurationMilliseconds / 1_000;
        frameBuffer = new byte[frameSamples * format.ChannelCount * sizeof(float)];
    }

    public int FrameBytes => frameBuffer.Length;

    public int Append(ReadOnlySpan<byte> source, Action<AudioFrame> frameReady)
    {
        ArgumentNullException.ThrowIfNull(frameReady);
        var emitted = 0;
        while (!source.IsEmpty)
        {
            var copyLength = Math.Min(frameBuffer.Length - bufferedBytes, source.Length);
            source[..copyLength].CopyTo(frameBuffer.AsSpan(bufferedBytes));
            bufferedBytes += copyLength;
            source = source[copyLength..];

            if (bufferedBytes != frameBuffer.Length)
            {
                continue;
            }

            frameReady(new AudioFrame(frameBuffer, format, frameSamples, timestamp));
            timestamp += (ulong)frameSamples;
            bufferedBytes = 0;
            emitted++;
        }

        return emitted;
    }

    public void Reset()
    {
        bufferedBytes = 0;
        timestamp = 0;
        Array.Clear(frameBuffer);
    }
}

