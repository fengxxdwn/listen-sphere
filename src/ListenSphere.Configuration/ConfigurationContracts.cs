namespace ListenSphere.Configuration;

public sealed record ChannelSettings(
    Guid ChannelId,
    string DisplayName,
    float Volume,
    bool IsMuted,
    string EqualizerPreset = "原声",
    float[]? EqualizerGains = null,
    bool EqualizerEnabled = true,
    string ChannelGroup = "未分组",
    float PreampDb = 0,
    bool NoiseGateEnabled = false,
    float NoiseGateThresholdDb = -48,
    bool CompressorEnabled = false,
    float CompressorThresholdDb = -18,
    float CompressorRatio = 4,
    bool LimiterEnabled = true,
    float LimiterCeilingDb = -1,
    bool IsVoiceDuckingTrigger = false,
    bool IsVoiceDuckingTarget = false,
    float VoiceDuckingReductionDb = 12);

public sealed record GroupBusSettings(
    string Name,
    float Volume = 1f,
    bool IsMuted = false,
    string EqualizerPreset = "原声");

public enum AudioRouteMatchMode
{
    Exact,
    Contains
}

public sealed record AudioRoutingRuleSettings(
    Guid RuleId,
    string Name,
    string SourcePattern,
    string TargetGroup,
    AudioRouteMatchMode MatchMode = AudioRouteMatchMode.Exact,
    string? SourceKind = null,
    string? Transport = null,
    int Priority = 100,
    bool IsEnabled = true);

public sealed record AudioRoutingContext(
    string SourceName,
    string? SourceKind = null,
    string? Transport = null);

public sealed record ChannelLayoutSettings(
    Guid ChannelId,
    bool IsPinned = false,
    int SortOrder = 0);

public sealed record AudioOutputRouteSettings(
    Guid ChannelId,
    string DeviceId,
    string? ChannelName = null);

public static class AudioRoutingRuleEvaluator
{
    public static AudioRoutingRuleSettings? Match(
        IEnumerable<AudioRoutingRuleSettings> rules,
        AudioRoutingContext context)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(context);
        return rules
            .Where(rule => rule.IsEnabled && Matches(rule, context))
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool Matches(
        AudioRoutingRuleSettings rule,
        AudioRoutingContext context)
    {
        if (!string.IsNullOrWhiteSpace(rule.SourceKind) &&
            !string.Equals(rule.SourceKind, context.SourceKind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(rule.Transport) &&
            !string.Equals(rule.Transport, context.Transport, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return rule.MatchMode == AudioRouteMatchMode.Contains
            ? context.SourceName.Contains(rule.SourcePattern, StringComparison.OrdinalIgnoreCase)
            : string.Equals(context.SourceName, rule.SourcePattern, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record SceneSettings(
    Guid SceneId,
    string Name,
    IReadOnlyList<ChannelSettings> Channels,
    string? PlaybackDeviceId = null,
    float MasterVolume = 1f,
    bool FollowSystemDefaultPlayback = false,
    IReadOnlyList<GroupBusSettings>? GroupBuses = null);

public sealed record ListenSphereSettings
{
    public const int CurrentVersion = 15;

    public int Version { get; init; } = CurrentVersion;
    public string? PlaybackDeviceId { get; init; }
    public bool FollowSystemDefaultPlayback { get; init; } = true;
    public float MasterVolume { get; init; } = 1f;
    public float LocalSourceVolume { get; init; } = 1f;
    public bool LocalSourceMuted { get; init; }
    public bool FirstRunCompleted { get; init; }
    public IReadOnlyList<SceneSettings> Scenes { get; init; } = [];
    public IReadOnlyList<Guid> PairedDeviceIds { get; init; } = [];
    public IReadOnlyList<string> SenderSelectedApplicationKeys { get; init; } = [];
    public string SenderCaptureMode { get; init; } = "applications";
    public string? SenderCaptureDeviceId { get; init; }
    public bool AutomaticRoutingEnabled { get; init; } = true;
    public IReadOnlyList<AudioRoutingRuleSettings> AudioRoutingRules { get; init; } = [];
    public IReadOnlyList<ChannelLayoutSettings> ChannelLayouts { get; init; } = [];
    public IReadOnlyList<AudioOutputRouteSettings> AudioOutputRoutes { get; init; } = [];
    public string? MicrophoneOutputDeviceId { get; init; }
    public bool MicrophoneOutputEnabled { get; init; }
    public string? ComputerMicrophoneDeviceId { get; init; }
    public float MicrophoneOutputVolume { get; init; } = 1f;
    public bool MicrophoneOutputMuted { get; init; }
    public bool MicrophoneMonitoringEnabled { get; init; }
    public string? MicrophoneMonitoringDeviceId { get; init; }
}

/// <summary>Loads and atomically saves versioned ListenSphere settings.</summary>
public interface ISettingsStore
{
    ValueTask<ListenSphereSettings> LoadAsync(CancellationToken cancellationToken);
    ValueTask SaveAsync(ListenSphereSettings settings, CancellationToken cancellationToken);
}
