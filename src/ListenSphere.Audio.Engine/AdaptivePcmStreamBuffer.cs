using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Audio.Engine;

/// <summary>Per-remote-source owned PCM ring, ahead of mixing. No borrowed input survives Enqueue.</summary>
internal sealed class AdaptivePcmStreamBuffer
{
    private readonly int frameBytes;
    private readonly IPlayoutClock clock;
    private readonly byte[] ring, scratch;
    private readonly IAdaptiveAudioResampler resampler = new AdaptiveLinearResampler();
    private AdaptivePlaybackController controller;
    private int read, count, startupFrames;
    public byte[] Output { get; }
    public bool Started { get; private set; }
    public int BufferedFrames => (count + frameBytes - 1) / frameBytes;
    public int StartupFrames
    {
        get => startupFrames;
        set
        {
            startupFrames = value;
            controller = new AdaptivePlaybackController(Math.Max(0, value - 1) * frameBytes / 384);
            if (count < value * frameBytes - 8) Started = false;
        }
    }

    public AdaptivePcmStreamBuffer(int frameBytes, int startupFrames, int maximumFrames, IPlayoutClock clock)
    {
        if (frameBytes % 8 != 0) throw new ArgumentException("Float32 stereo frames required.");
        this.frameBytes = frameBytes; this.clock = clock; this.startupFrames = startupFrames;
        ring = new byte[checked(frameBytes * maximumFrames + resampler.GetMaximumOutputBytes(frameBytes) - frameBytes)];
        Output = new byte[frameBytes]; scratch = new byte[resampler.GetMaximumOutputBytes(frameBytes)];
        controller = new AdaptivePlaybackController(Math.Max(0, startupFrames - 1) * frameBytes / 384);
    }

    public int Enqueue(ReadOnlySpan<byte> pcm, ulong timestamp)
    {
        long discontinuities = controller.ClockDiscontinuities;
        controller.ObserveClock(timestamp, clock.Elapsed);
        if (controller.ClockDiscontinuities != discontinuities)
        {
            read = count = 0; Started = false; resampler.Reset();
            controller.Reset(); controller.ObserveClock(timestamp, clock.Elapsed);
        }
        if (Started) resampler.Ratio = controller.GetResamplingRatio(count / 384d, frameBytes / 384000d);
        int written = resampler.Convert(pcm, scratch);
        int dropped = 0;
        while (count + written > ring.Length)
        {
            int discard = Math.Min(count, frameBytes);
            read = (read + discard) % ring.Length; count -= discard; dropped++;
            if (discard == 0) throw new InvalidOperationException("PCM ring is smaller than one converted frame.");
        }
        int write = (read + count) % ring.Length;
        int first = Math.Min(written, ring.Length - write);
        scratch.AsSpan(0, first).CopyTo(ring.AsSpan(write));
        scratch.AsSpan(first, written - first).CopyTo(ring);
        count += written;
        if (!Started && count >= Math.Max(frameBytes, startupFrames * frameBytes - 8)) Started = true;
        return dropped;
    }

    public bool TryRead()
    {
        if (!Started) return false;
        if (count < frameBytes) { Started = false; return false; }
        int first = Math.Min(frameBytes, ring.Length - read);
        ring.AsSpan(read, first).CopyTo(Output);
        ring.AsSpan(0, frameBytes - first).CopyTo(Output.AsSpan(first));
        read = (read + frameBytes) % ring.Length; count -= frameBytes;
        return true;
    }
}
