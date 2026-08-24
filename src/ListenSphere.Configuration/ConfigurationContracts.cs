namespace ListenSphere.Configuration;

public sealed record ChannelSettings(Guid ChannelId, string DisplayName, float Volume, bool IsMuted);

public sealed record SceneSettings(
    Guid SceneId,
    string Name,
    IReadOnlyList<ChannelSettings> Channels,
    string? PlaybackDeviceId = null,
    float MasterVolume = 1f,
    bool FollowSystemDefaultPlayback = false);

public sealed record ListenSphereSettings
{
    public const int CurrentVersion = 3;

    public int Version { get; init; } = CurrentVersion;
    public string? PlaybackDeviceId { get; init; }
    public bool FollowSystemDefaultPlayback { get; init; } = true;
    public float MasterVolume { get; init; } = 1f;
    public bool FirstRunCompleted { get; init; }
    public IReadOnlyList<SceneSettings> Scenes { get; init; } = [];
    public IReadOnlyList<Guid> PairedDeviceIds { get; init; } = [];
}

/// <summary>Loads and atomically saves versioned ListenSphere settings.</summary>
public interface ISettingsStore
{
    ValueTask<ListenSphereSettings> LoadAsync(CancellationToken cancellationToken);
    ValueTask SaveAsync(ListenSphereSettings settings, CancellationToken cancellationToken);
}
