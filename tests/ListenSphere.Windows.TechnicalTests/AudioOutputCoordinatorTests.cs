using System.IO;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Controller.Coordinators;
using ListenSphere.Windows.Devices;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class AudioOutputCoordinatorTests
{
    [Fact]
    public async Task InitializeAsync_FollowsWindowsDefaultAndReadsEndpointState()
    {
        var secondary = new TestAudioDevice("secondary", "Secondary", false);
        var primary = new TestAudioDevice("primary", "Primary", true);
        var runtime = new FakeAudioOutputRuntime(secondary, primary);
        runtime.Volumes[primary.Id] = 0.42f;
        runtime.Mutes[primary.Id] = true;
        await using var coordinator = new AudioOutputCoordinator(runtime, () => 56_974);

        await coordinator.InitializeAsync(
            secondary.Id,
            75,
            followSystemDefault: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(primary.Id, coordinator.Snapshot.SelectedDevice?.Id);
        Assert.Equal(42, coordinator.Snapshot.MasterVolumePercent, 1);
        Assert.True(coordinator.Snapshot.IsSystemMuted);
        Assert.Equal("UDP 56974 · 播放到：Primary", coordinator.Snapshot.Status);
        Assert.Equal(2, coordinator.Snapshot.Endpoints.Count);
        Assert.Single(runtime.CreatedSinks);
        Assert.Equal(1, runtime.CreatedSinks[0].StartCount);
    }

    [Fact]
    public async Task SelectDeviceAsync_DisablesDefaultFollowingAndReplacesPlaybackOwner()
    {
        var primary = new TestAudioDevice("primary", "Primary", true);
        var secondary = new TestAudioDevice("secondary", "Secondary", false);
        var runtime = new FakeAudioOutputRuntime(primary, secondary);
        await using var coordinator = new AudioOutputCoordinator(runtime);
        await coordinator.InitializeAsync(
            primary.Id,
            100,
            true,
            TestContext.Current.CancellationToken);
        FakePlaybackSink first = runtime.CreatedSinks[0];

        await coordinator.SelectDeviceAsync(
            secondary,
            TestContext.Current.CancellationToken);

        Assert.False(coordinator.Snapshot.FollowSystemDefault);
        Assert.Equal(secondary.Id, coordinator.Snapshot.SelectedDevice?.Id);
        Assert.Equal(1, first.StopCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(2, runtime.CreatedSinks.Count);
    }

    [Fact]
    public async Task ApplySettingsAsync_PreservesSavedVolumeInsteadOfReadingWindowsVolume()
    {
        var primary = new TestAudioDevice("primary", "Primary", true);
        var runtime = new FakeAudioOutputRuntime(primary);
        runtime.Volumes[primary.Id] = 0.2f;
        await using var coordinator = new AudioOutputCoordinator(runtime);
        await coordinator.InitializeAsync(
            primary.Id,
            100,
            true,
            TestContext.Current.CancellationToken);

        await coordinator.ApplySettingsAsync(
            primary.Id,
            65,
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal(65, coordinator.Snapshot.MasterVolumePercent, 1);
        Assert.Equal(0.65f, runtime.Volumes[primary.Id], 3);
    }

    [Fact]
    public async Task EndpointVolumeDebounce_AppliesOnlyLatestValue()
    {
        var primary = new TestAudioDevice("primary", "Primary", true);
        var runtime = new FakeAudioOutputRuntime(primary);
        await using var coordinator = new AudioOutputCoordinator(runtime);
        await coordinator.InitializeAsync(
            primary.Id,
            100,
            true,
            TestContext.Current.CancellationToken);
        runtime.VolumeWrites.Clear();

        coordinator.SetEndpointVolume(primary.Id, 0.2f);
        coordinator.SetEndpointVolume(primary.Id, 0.8f);
        await Task.Delay(250, TestContext.Current.CancellationToken);

        Assert.Equal([(primary.Id, 0.8f)], runtime.VolumeWrites);
        Assert.Equal(80, Assert.Single(coordinator.Snapshot.Endpoints).VolumePercent, 1);
    }

    [Fact]
    public async Task DeviceChange_IsDebouncedAndRefreshesDefaultEndpoint()
    {
        var first = new TestAudioDevice("first", "First", true);
        var second = new TestAudioDevice("second", "Second", false);
        var runtime = new FakeAudioOutputRuntime(first, second);
        await using var coordinator = new AudioOutputCoordinator(runtime);
        await coordinator.InitializeAsync(
            first.Id,
            100,
            true,
            TestContext.Current.CancellationToken);
        long revision = coordinator.Snapshot.DeviceRevision;
        runtime.Devices =
        [first with { IsDefault = false }, second with { IsDefault = true }];

        runtime.RaiseDeviceChanged(WindowsDeviceChange.DefaultChanged);
        runtime.RaiseDeviceChanged(WindowsDeviceChange.StateChanged);
        await Task.Delay(800, TestContext.Current.CancellationToken);

        Assert.Equal(revision + 1, coordinator.Snapshot.DeviceRevision);
        Assert.Equal(second.Id, coordinator.Snapshot.SelectedDevice?.Id);
    }

    [Fact]
    public async Task WriteFailure_ReleasesSinkAndPublishesRecoveringError()
    {
        var primary = new TestAudioDevice("primary", "Primary", true);
        var runtime = new FakeAudioOutputRuntime(primary) { FailWrites = true };
        await using var coordinator = new AudioOutputCoordinator(runtime);
        await coordinator.InitializeAsync(
            primary.Id,
            100,
            true,
            TestContext.Current.CancellationToken);
        FakePlaybackSink sink = runtime.CreatedSinks[0];

        await coordinator.WriteAsync(
            new AudioFrame(new byte[16], AudioFormat.Default, 1, 0),
            TestContext.Current.CancellationToken);

        Assert.Contains("正在自动恢复", coordinator.Snapshot.ErrorText);
        Assert.Equal(1, sink.StopCount);
        Assert.Equal(1, sink.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentRefreshes_AreSerializedAndKeepSelectedDeviceInSnapshot()
    {
        var primary = new TestAudioDevice("primary", "Primary", true);
        var secondary = new TestAudioDevice("secondary", "Secondary", false);
        var runtime = new FakeAudioOutputRuntime(primary, secondary)
        {
            PlaybackDeviceReadDelayMilliseconds = 40,
            CloneDevicesOnRead = true
        };
        await using var coordinator = new AudioOutputCoordinator(runtime);
        await coordinator.InitializeAsync(
            primary.Id,
            100,
            true,
            TestContext.Current.CancellationToken);

        await Task.WhenAll(
            coordinator.RefreshAsync(TestContext.Current.CancellationToken),
            coordinator.RefreshAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, runtime.MaximumConcurrentPlaybackDeviceReads);
        Assert.Contains(
            coordinator.Snapshot.Devices,
            device => ReferenceEquals(device, coordinator.Snapshot.SelectedDevice));
    }

    private sealed record TestAudioDevice(
        string Id,
        string DisplayName,
        bool IsDefault) : IAudioDevice;

    private sealed class FakeAudioOutputRuntime(params IAudioDevice[] devices) :
        IAudioOutputRuntime
    {
        public event EventHandler<WindowsDeviceChange>? DeviceChanged;

        public IReadOnlyList<IAudioDevice> Devices { get; set; } = devices;
        public Dictionary<string, float> Volumes { get; } = devices.ToDictionary(
            device => device.Id,
            _ => 1f,
            StringComparer.Ordinal);
        public Dictionary<string, bool> Mutes { get; } = devices.ToDictionary(
            device => device.Id,
            _ => false,
            StringComparer.Ordinal);
        public List<(string DeviceId, float Volume)> VolumeWrites { get; } = [];
        public List<FakePlaybackSink> CreatedSinks { get; } = [];
        public bool FailWrites { get; set; }
        public bool CloneDevicesOnRead { get; set; }
        public int PlaybackDeviceReadDelayMilliseconds { get; set; }
        public int MaximumConcurrentPlaybackDeviceReads { get; private set; }
        private int activePlaybackDeviceReads;

        public async ValueTask<IReadOnlyList<IAudioDevice>> GetPlaybackDevicesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int active = Interlocked.Increment(ref activePlaybackDeviceReads);
            MaximumConcurrentPlaybackDeviceReads = Math.Max(
                MaximumConcurrentPlaybackDeviceReads,
                active);
            try
            {
                if (PlaybackDeviceReadDelayMilliseconds > 0)
                {
                    await Task.Delay(
                        PlaybackDeviceReadDelayMilliseconds,
                        cancellationToken);
                }
                return CloneDevicesOnRead
                    ? Devices.Select(device =>
                        (IAudioDevice)new TestAudioDevice(
                            device.Id,
                            device.DisplayName,
                            device.IsDefault)).ToArray()
                    : Devices;
            }
            finally
            {
                Interlocked.Decrement(ref activePlaybackDeviceReads);
            }
        }

        public ValueTask<float> GetVolumeAsync(
            string deviceId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Volumes.GetValueOrDefault(deviceId, 1f));

        public ValueTask SetVolumeAsync(
            string deviceId,
            float volume,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volumes[deviceId] = volume;
            VolumeWrites.Add((deviceId, volume));
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> GetMuteAsync(
            string deviceId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Mutes.GetValueOrDefault(deviceId));

        public ValueTask SetMuteAsync(
            string deviceId,
            bool isMuted,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mutes[deviceId] = isMuted;
            return ValueTask.CompletedTask;
        }

        public IAudioPlaybackSink CreatePlaybackSink(IAudioDevice device)
        {
            var sink = new FakePlaybackSink(FailWrites);
            CreatedSinks.Add(sink);
            return sink;
        }

        public void RaiseDeviceChanged(WindowsDeviceChange change) =>
            DeviceChanged?.Invoke(this, change);
    }

    private sealed class FakePlaybackSink(bool failWrites) :
        IAudioPlaybackSink,
        IAudioPlaybackDiagnostics
    {
        public AudioFormat InputFormat => AudioFormat.Default;
        public AudioPlaybackStatistics Statistics { get; } = new(1, 0, 0, 0, 10, 0);
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken) =>
            failWrites
                ? ValueTask.FromException(new IOException("write failed"))
                : ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
