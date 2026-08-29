using System.Runtime.InteropServices;

namespace ListenSphere.Audio.Engine;

public sealed record RemoteMixerStatistics(
    long MixedFrames,
    long StreamUnderflows,
    long StreamOverflows,
    long ClippedSamples,
    int ActiveStreams,
    int BufferedFrames);

/// <summary>
/// Aligns independent remote streams to a fixed playback clock and mixes Float32 PCM.
/// </summary>
public sealed class RemotePcmMixer(
    int frameBytes,
    int startupFrames = 2,
    int maximumFrames = 12,
    bool hardClipOutput = true)
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, StreamBuffer> streams = [];
    private long mixedFrames;
    private long streamUnderflows;
    private long streamOverflows;
    private long clippedSamples;

    public RemoteMixerStatistics Statistics
    {
        get
        {
            lock (gate)
            {
                return new RemoteMixerStatistics(
                    mixedFrames,
                    streamUnderflows,
                    streamOverflows,
                    clippedSamples,
                    streams.Count,
                    streams.Values.Sum(stream => stream.Frames.Count));
            }
        }
    }

    public void RegisterStream(Guid sessionId, int? preferredStartupFrames = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(sessionId, Guid.Empty);
        int requestedStartup = Math.Clamp(
            preferredStartupFrames ?? startupFrames,
            1,
            maximumFrames);
        lock (gate)
        {
            if (streams.TryGetValue(sessionId, out StreamBuffer? existing))
            {
                if (requestedStartup > existing.StartupFrames)
                {
                    existing.StartupFrames = requestedStartup;
                    if (existing.Frames.Count < requestedStartup)
                    {
                        existing.Started = false;
                    }
                }
                return;
            }

            streams.Add(sessionId, new StreamBuffer(requestedStartup));
        }
    }

    public void RemoveStream(Guid sessionId)
    {
        lock (gate)
        {
            streams.Remove(sessionId);
        }
    }

    public void Enqueue(Guid sessionId, byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (pcm.Length != frameBytes)
        {
            throw new ArgumentException($"Expected exactly {frameBytes} PCM bytes.", nameof(pcm));
        }

        lock (gate)
        {
            if (!streams.TryGetValue(sessionId, out StreamBuffer? stream))
            {
                stream = new StreamBuffer(startupFrames);
                streams.Add(sessionId, stream);
            }

            while (stream.Frames.Count >= maximumFrames)
            {
                stream.Frames.Dequeue();
                streamOverflows++;
            }

            stream.Frames.Enqueue(pcm);
            if (!stream.Started && stream.Frames.Count >= stream.StartupFrames)
            {
                stream.Started = true;
            }
        }
    }

    public bool TryMixNext(Span<byte> output)
    {
        if (output.Length != frameBytes || output.Length % sizeof(float) != 0)
        {
            throw new ArgumentException($"Expected exactly {frameBytes} PCM bytes.", nameof(output));
        }

        lock (gate)
        {
            output.Clear();
            Span<float> mixed = MemoryMarshal.Cast<byte, float>(output);
            var mixedAny = false;
            foreach (StreamBuffer stream in streams.Values)
            {
                if (!stream.Started)
                {
                    continue;
                }

                if (!stream.Frames.TryDequeue(out byte[]? frame))
                {
                    streamUnderflows++;
                    // Once Bluetooth/RFCOMM jitter drains the queue, wait for a fresh
                    // startup cushion instead of alternating one frame of audio with
                    // one frame of silence indefinitely.
                    stream.Started = false;
                    continue;
                }

                mixedAny = true;
                ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(frame);
                for (var index = 0; index < mixed.Length; index++)
                {
                    float sample = float.IsFinite(samples[index]) ? samples[index] : 0f;
                    mixed[index] += sample;
                }
            }

            if (!mixedAny)
            {
                return false;
            }

            for (var index = 0; index < mixed.Length; index++)
            {
                if (mixed[index] > 1f)
                {
                    clippedSamples++;
                    if (hardClipOutput)
                    {
                        mixed[index] = 1f;
                    }
                }
                else if (mixed[index] < -1f)
                {
                    clippedSamples++;
                    if (hardClipOutput)
                    {
                        mixed[index] = -1f;
                    }
                }
            }

            mixedFrames++;
            return true;
        }
    }

    private sealed class StreamBuffer(int startupFrames)
    {
        public Queue<byte[]> Frames { get; } = [];
        public int StartupFrames { get; set; } = startupFrames;
        public bool Started { get; set; }
    }
}
