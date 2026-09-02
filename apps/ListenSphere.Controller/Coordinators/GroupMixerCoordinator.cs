using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ListenSphere.Audio.Engine;

namespace ListenSphere.Controller.Coordinators;

internal readonly record struct GroupMixerChannelSettings(
    float EffectiveGain,
    bool EqualizerEnabled,
    IReadOnlyList<float> EqualizerGains,
    ChannelDynamicsSettings Dynamics,
    string GroupName,
    bool IsVoiceDuckingTrigger,
    bool IsVoiceDuckingTarget,
    float VoiceDuckingReductionDb);

internal sealed record GroupMixerBusSettings(
    string Name,
    float EffectiveGain,
    IReadOnlyList<float> EqualizerGains)
{
    public bool HasEqualization => EqualizerGains.Any(gain => gain != 0);
}

internal readonly record struct GroupMixerProcessResult(
    bool HasChannel,
    float Peak,
    ChannelDynamicsResult Dynamics,
    bool DuckingActive);

internal sealed record GroupMixerBusSnapshot(
    string Name,
    float EffectiveGain,
    IReadOnlyList<float> EqualizerGains,
    int ChannelCount);

internal sealed record GroupMixerSnapshot(
    IReadOnlyList<GroupMixerBusSnapshot> Buses,
    long Revision)
{
    public static GroupMixerSnapshot Empty { get; } = new([], 0);
}

internal interface IGroupMixerSettingsProvider
{
    bool TryGetChannelSettings(
        Guid sessionId,
        out GroupMixerChannelSettings settings);
}

internal sealed class GroupMixerCoordinator : IAsyncDisposable
{
    private readonly IGroupMixerSettingsProvider settingsProvider;
    private readonly ConcurrentDictionary<Guid, GraphicEqualizer> channelEqualizers = [];
    private readonly ConcurrentDictionary<Guid, GraphicEqualizer> groupEqualizers = [];
    private readonly ConcurrentDictionary<Guid, ChannelDynamicsProcessor> dynamicsProcessors = [];
    private readonly ConcurrentDictionary<Guid, ChannelDynamicsResult> dynamicsResults = [];
    private readonly ConcurrentDictionary<string, GroupMixerBusSettings> buses =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> channelCounts =
        new(StringComparer.Ordinal);
    private readonly VoiceDuckingController voiceDucking = new();
    private long revision;
    private bool disposed;

    public GroupMixerCoordinator(IGroupMixerSettingsProvider settingsProvider)
    {
        this.settingsProvider = settingsProvider;
    }

    public event EventHandler<GroupMixerSnapshot>? SnapshotChanged;

    public GroupMixerSnapshot Snapshot { get; private set; } = GroupMixerSnapshot.Empty;

    internal int ActiveProcessorSessions => dynamicsProcessors.Count;

    public void UpdateBus(GroupMixerBusSettings settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Name);
        if (settings.EqualizerGains.Count != GraphicEqualizer.Frequencies.Length)
        {
            throw new ArgumentException(
                "A group bus equalizer requires ten bands.",
                nameof(settings));
        }

        buses[settings.Name] = settings with
        {
            EffectiveGain = Math.Clamp(settings.EffectiveGain, 0f, 1f),
            EqualizerGains = settings.EqualizerGains.ToArray()
        };
        PublishSnapshot();
    }

    public void UpdateActiveGroups(IEnumerable<string> activeGroupNames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Dictionary<string, int> nextCounts = activeGroupNames
            .GroupBy(name => name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        channelCounts.Clear();
        foreach ((string name, int count) in nextCounts)
        {
            channelCounts[name] = count;
        }
        PublishSnapshot();
    }

    public GroupMixerProcessResult Process(Guid sessionId, Span<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!settingsProvider.TryGetChannelSettings(sessionId, out var channel))
        {
            return default;
        }
        if (pcm.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                "Float32 PCM length must be divisible by four.",
                nameof(pcm));
        }

        Span<float> samples = MemoryMarshal.Cast<byte, float>(pcm);
        ChannelDynamicsProcessor dynamics = dynamicsProcessors.GetOrAdd(
            sessionId,
            _ => new ChannelDynamicsProcessor());
        ChannelDynamicsResult result = dynamics.ProcessBeforeEqualizer(
            samples,
            channel.Dynamics);
        dynamicsResults[sessionId] = result;
        if (channel.IsVoiceDuckingTrigger)
        {
            voiceDucking.ObserveVoice(samples);
        }

        if (channel.EqualizerEnabled)
        {
            channelEqualizers.GetOrAdd(sessionId, _ => new GraphicEqualizer())
                .Process(samples, channel.EqualizerGains);
        }

        float groupGain = 1f;
        if (buses.TryGetValue(channel.GroupName, out GroupMixerBusSettings? bus))
        {
            groupGain = bus.EffectiveGain;
            if (bus.HasEqualization)
            {
                groupEqualizers.GetOrAdd(sessionId, _ => new GraphicEqualizer())
                    .Process(samples, bus.EqualizerGains);
            }
        }

        bool duckingActive = channel.IsVoiceDuckingTarget && voiceDucking.IsVoiceActive;
        float duckingGain = channel.IsVoiceDuckingTarget
            ? voiceDucking.GetTargetGain(channel.VoiceDuckingReductionDb)
            : 1f;
        PcmGainProcessor.Apply(
            pcm,
            channel.EffectiveGain * groupGain * duckingGain);

        dynamicsResults.TryGetValue(sessionId, out ChannelDynamicsResult previous);
        result = dynamics.ApplyLimiter(samples, channel.Dynamics, previous);
        dynamicsResults[sessionId] = result;
        float peak = AudioLevelCalculator.Calculate(samples).Peak;
        return new GroupMixerProcessResult(true, peak, result, duckingActive);
    }

    public void RemoveSession(Guid sessionId)
    {
        channelEqualizers.TryRemove(sessionId, out _);
        groupEqualizers.TryRemove(sessionId, out _);
        dynamicsProcessors.TryRemove(sessionId, out _);
        dynamicsResults.TryRemove(sessionId, out _);
    }

    private void PublishSnapshot()
    {
        GroupMixerBusSnapshot[] snapshots = buses.Values
            .OrderBy(bus => bus.Name, StringComparer.Ordinal)
            .Select(bus => new GroupMixerBusSnapshot(
                bus.Name,
                bus.EffectiveGain,
                bus.EqualizerGains.ToArray(),
                channelCounts.TryGetValue(bus.Name, out int count) ? count : 0))
            .ToArray();
        Snapshot = new GroupMixerSnapshot(
            snapshots,
            Interlocked.Increment(ref revision));
        SnapshotChanged?.Invoke(this, Snapshot);
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        channelEqualizers.Clear();
        groupEqualizers.Clear();
        dynamicsProcessors.Clear();
        dynamicsResults.Clear();
        buses.Clear();
        channelCounts.Clear();
        voiceDucking.Reset();
        return ValueTask.CompletedTask;
    }
}
