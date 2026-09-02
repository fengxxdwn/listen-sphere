using System.Runtime.InteropServices;
using ListenSphere.Audio.Engine;
using ListenSphere.Controller.Coordinators;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class GroupMixerCoordinatorTests
{
    private static readonly float[] FlatEqualizer = new float[10];

    [Fact]
    public async Task Process_UnknownSession_LeavesPcmUnchanged()
    {
        var provider = new FakeSettingsProvider();
        await using var coordinator = new GroupMixerCoordinator(provider);
        byte[] pcm = FloatPcm(0.4f);

        GroupMixerProcessResult result = coordinator.Process(Guid.NewGuid(), pcm);

        Assert.False(result.HasChannel);
        Assert.All(ToFloats(pcm), sample => Assert.Equal(0.4f, sample, 3));
    }

    [Fact]
    public async Task Process_AppliesChannelAndGroupGainBeforeLimiter()
    {
        Guid sessionId = Guid.NewGuid();
        var provider = new FakeSettingsProvider();
        provider.Channels[sessionId] = Channel(gain: 0.5f, group: "媒体");
        await using var coordinator = new GroupMixerCoordinator(provider);
        coordinator.UpdateBus(Bus("媒体", gain: 0.5f));
        byte[] pcm = FloatPcm(0.8f);

        GroupMixerProcessResult result = coordinator.Process(sessionId, pcm);

        Assert.True(result.HasChannel);
        Assert.Equal(0.2f, result.Peak, 3);
        Assert.All(ToFloats(pcm), sample => Assert.Equal(0.2f, sample, 3));
    }

    [Fact]
    public async Task Process_MutedGroupSilencesChannel()
    {
        Guid sessionId = Guid.NewGuid();
        var provider = new FakeSettingsProvider();
        provider.Channels[sessionId] = Channel(group: "游戏");
        await using var coordinator = new GroupMixerCoordinator(provider);
        coordinator.UpdateBus(Bus("游戏", gain: 0));
        byte[] pcm = FloatPcm(0.75f);

        GroupMixerProcessResult result = coordinator.Process(sessionId, pcm);

        Assert.Equal(0, result.Peak);
        Assert.All(ToFloats(pcm), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public async Task Process_LimiterCapsPostVolumeSignal()
    {
        Guid sessionId = Guid.NewGuid();
        var provider = new FakeSettingsProvider();
        provider.Channels[sessionId] = Channel(
            dynamics: new ChannelDynamicsSettings(
                LimiterEnabled: true,
                LimiterCeilingDb: -6));
        await using var coordinator = new GroupMixerCoordinator(provider);
        byte[] pcm = FloatPcm(1f);

        GroupMixerProcessResult result = coordinator.Process(sessionId, pcm);

        Assert.True(result.Dynamics.Limited);
        Assert.Equal(MathF.Pow(10, -6f / 20f), result.Peak, 3);
    }

    [Fact]
    public async Task Process_NoiseGateRetainsStatePerSession()
    {
        Guid sessionId = Guid.NewGuid();
        var provider = new FakeSettingsProvider();
        provider.Channels[sessionId] = Channel(
            dynamics: new ChannelDynamicsSettings(
                NoiseGateEnabled: true,
                NoiseGateThresholdDb: -30));
        await using var coordinator = new GroupMixerCoordinator(provider);
        GroupMixerProcessResult result = default;

        for (var index = 0; index < 12; index++)
        {
            result = coordinator.Process(sessionId, FloatPcm(0.001f));
        }

        Assert.True(result.Dynamics.GateClosed);
    }

    [Fact]
    public async Task VoiceTriggerDucksConfiguredTarget()
    {
        Guid triggerSession = Guid.NewGuid();
        Guid targetSession = Guid.NewGuid();
        var provider = new FakeSettingsProvider();
        provider.Channels[triggerSession] = Channel(trigger: true);
        provider.Channels[targetSession] = Channel(target: true, reduction: 18);
        await using var coordinator = new GroupMixerCoordinator(provider);
        coordinator.Process(targetSession, FloatPcm(0.5f));
        coordinator.Process(triggerSession, FloatPcm(0.5f));
        await Task.Delay(80, TestContext.Current.CancellationToken);
        byte[] target = FloatPcm(0.5f);

        GroupMixerProcessResult result = coordinator.Process(targetSession, target);

        Assert.True(result.DuckingActive);
        Assert.True(result.Peak < 0.4f);
    }

    [Fact]
    public async Task Snapshot_ProjectsBusSettingsAndActiveCounts()
    {
        var provider = new FakeSettingsProvider();
        await using var coordinator = new GroupMixerCoordinator(provider);
        float[] gains = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        coordinator.UpdateBus(new GroupMixerBusSettings("语音", 0.75f, gains));
        gains[0] = 9;

        coordinator.UpdateActiveGroups(["语音", "语音", "游戏"]);

        GroupMixerBusSnapshot bus = Assert.Single(
            coordinator.Snapshot.Buses,
            item => item.Name == "语音");
        Assert.Equal(0.75f, bus.EffectiveGain);
        Assert.Equal(1, bus.EqualizerGains[0]);
        Assert.Equal(2, bus.ChannelCount);
    }

    [Fact]
    public async Task RemoveSession_ReleasesProcessorState()
    {
        Guid sessionId = Guid.NewGuid();
        var provider = new FakeSettingsProvider();
        provider.Channels[sessionId] = Channel();
        await using var coordinator = new GroupMixerCoordinator(provider);
        coordinator.Process(sessionId, FloatPcm(0.2f));
        Assert.Equal(1, coordinator.ActiveProcessorSessions);

        coordinator.RemoveSession(sessionId);

        Assert.Equal(0, coordinator.ActiveProcessorSessions);
    }

    private static GroupMixerChannelSettings Channel(
        float gain = 1,
        string group = "未分组",
        ChannelDynamicsSettings dynamics = default,
        bool trigger = false,
        bool target = false,
        float reduction = 12) => new(
            gain,
            false,
            FlatEqualizer,
            dynamics == default ? new ChannelDynamicsSettings() : dynamics,
            group,
            trigger,
            target,
            reduction);

    private static GroupMixerBusSettings Bus(string name, float gain) =>
        new(name, gain, FlatEqualizer);

    private static byte[] FloatPcm(float value)
    {
        var pcm = new byte[3_840];
        MemoryMarshal.Cast<byte, float>(pcm.AsSpan()).Fill(value);
        return pcm;
    }

    private static float[] ToFloats(byte[] pcm) =>
        MemoryMarshal.Cast<byte, float>(pcm).ToArray();

    private sealed class FakeSettingsProvider : IGroupMixerSettingsProvider
    {
        public Dictionary<Guid, GroupMixerChannelSettings> Channels { get; } = [];

        public bool TryGetChannelSettings(
            Guid sessionId,
            out GroupMixerChannelSettings settings)
        {
            return Channels.TryGetValue(sessionId, out settings);
        }
    }
}
