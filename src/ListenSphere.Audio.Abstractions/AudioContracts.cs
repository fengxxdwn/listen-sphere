using System.Buffers;

namespace ListenSphere.Audio.Abstractions;

/// <summary>Describes normalized PCM exchanged inside ListenSphere.</summary>
public readonly record struct AudioFormat(int SampleRate, AudioSampleFormat SampleFormat, int ChannelCount)
{
    public static AudioFormat Default { get; } = new(48_000, AudioSampleFormat.Float32LittleEndian, 2);

    public TimeSpan GetDuration(int sampleFrames) =>
        TimeSpan.FromSeconds((double)sampleFrames / SampleRate);
}

public enum AudioSampleFormat : byte
{
    Float32LittleEndian = 1
}

/// <summary>
/// A borrowed audio frame. The owner remains responsible for the memory lifetime and
/// consumers must not retain the memory after the sink call completes.
/// </summary>
public readonly record struct AudioFrame(
    ReadOnlyMemory<byte> Data,
    AudioFormat Format,
    int SampleFrames,
    ulong Timestamp);

/// <summary>Consumes borrowed, normalized audio frames.</summary>
public interface IAudioFrameSink
{
    ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken);
}

/// <summary>Captures audio from a platform source into a frame sink.</summary>
public interface IAudioCaptureSource : IAsyncDisposable
{
    AudioFormat OutputFormat { get; }
    ValueTask StartAsync(IAudioFrameSink sink, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>Plays normalized audio frames through a platform output.</summary>
public interface IAudioPlaybackSink : IAsyncDisposable
{
    AudioFormat InputFormat { get; }
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

public sealed record AudioPlaybackStatistics(
    long FramesWritten,
    long BufferUnderruns,
    long BufferOverflows,
    long DriftCorrections,
    int BufferedMilliseconds,
    double EstimatedClockDriftPpm);

/// <summary>Exposes lock-free snapshots of playback buffer health.</summary>
public interface IAudioPlaybackDiagnostics
{
    AudioPlaybackStatistics Statistics { get; }
}

/// <summary>Combines normalized floating-point channels into one output buffer.</summary>
public interface IAudioMixer
{
    AudioFormat MixFormat { get; }
    void Mix(ReadOnlySpan<ReadOnlyMemory<float>> inputs, Span<float> output);
}

/// <summary>Describes a platform playback device without exposing platform APIs.</summary>
public interface IAudioDevice
{
    string Id { get; }
    string DisplayName { get; }
    bool IsDefault { get; }
}

/// <summary>Enumerates platform playback devices.</summary>
public interface IAudioDeviceManager
{
    ValueTask<IReadOnlyList<IAudioDevice>> GetPlaybackDevicesAsync(
        CancellationToken cancellationToken);
}

/// <summary>Reads and controls the master volume of a platform playback endpoint.</summary>
public interface IAudioOutputVolumeController
{
    ValueTask<float> GetVolumeAsync(string deviceId, CancellationToken cancellationToken);
    ValueTask SetVolumeAsync(
        string deviceId,
        float volume,
        CancellationToken cancellationToken);
}

/// <summary>Converts PCM between negotiated input and output formats.</summary>
public interface IAudioResampler
{
    AudioFormat InputFormat { get; }
    AudioFormat OutputFormat { get; }
    int Convert(ReadOnlySpan<byte> input, Span<byte> output);
}

/// <summary>Encodes normalized PCM into a negotiated network codec.</summary>
public interface IAudioEncoder
{
    byte CodecId { get; }
    int Encode(ReadOnlySpan<byte> pcm, Span<byte> encoded);
}

/// <summary>Decodes a negotiated network codec into normalized PCM.</summary>
public interface IAudioDecoder
{
    byte CodecId { get; }
    int Decode(ReadOnlySpan<byte> encoded, Span<byte> pcm);
}

/// <summary>Provides pooled buffers without prescribing a platform audio API.</summary>
public interface IAudioBufferPool
{
    IMemoryOwner<byte> Rent(int minimumLength);
}
