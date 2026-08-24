using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Audio.Engine;

/// <summary>Stable P0 defaults used by future mixer implementations.</summary>
public sealed record MixingOptions
{
    public AudioFormat Format { get; init; } = AudioFormat.Default;
    public int FrameDurationMilliseconds { get; init; } = 10;
    public int MaximumRemoteStreams { get; init; } = 8;
}

