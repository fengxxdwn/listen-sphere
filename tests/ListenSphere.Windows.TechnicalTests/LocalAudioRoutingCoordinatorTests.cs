using System.Collections.Concurrent;
using System.Diagnostics;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Configuration;
using ListenSphere.Controller.Coordinators;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class LocalAudioRoutingCoordinatorTests
{
    private static readonly Guid LocalSoundChannelId = Guid.Parse(
        "4c5f5426-fdc1-47b3-b7b4-60c31c6ea601");

    [Fact]
    public async Task ConfigureRoutes_ReconcilesSavedRouteAndStartsSystemCapture()
    {
        var primary = new TestAudioDevice("primary", "Primary", true);
        var secondary = new TestAudioDevice("secondary", "Secondary", false);
        var runtime = new FakeLocalAudioRoutingRuntime();
        await using var coordinator = new LocalAudioRoutingCoordinator(
            runtime,
            LocalSoundChannelId);
        coordinator.ConfigureRoutes(
        [
            new AudioOutputRouteSettings(
                LocalSoundChannelId,
                secondary.Id,
                "本地声音")
        ]);

        await coordinator.UpdateOutputDevicesAsync(
            [primary, secondary],
            [
                new AudioOutputEndpointSnapshot(primary, 65, false),
                new AudioOutputEndpointSnapshot(secondary, 40, true)
            ],
            primary.Id,
            TestContext.Current.CancellationToken);

        FakePlaybackSink playback = Assert.Single(runtime.Playbacks);
        Assert.Equal(1, playback.StartCount);
        FakeCaptureSource capture = Assert.Single(runtime.SystemCaptures);
        Assert.Equal(primary.Id, capture.SourceKey);
        Assert.Equal(1, capture.StartCount);
        LocalAudioOutputDeviceSnapshot output = Assert.Single(coordinator.Snapshot.Outputs);
        Assert.Equal(40, output.VolumePercent);
        Assert.True(output.IsMuted);
        Assert.True(Assert.Single(output.Routes).IsActive);
    }

    [Fact]
    public async Task RemoveRoute_ImmediatelyRemovesProjectionAndDisposesOwners()
    {
        var primary = new TestAudioDevice("primary", "Primary", true);
        var secondary = new TestAudioDevice("secondary", "Secondary", false);
        var runtime = new FakeLocalAudioRoutingRuntime();
        await using var coordinator = new LocalAudioRoutingCoordinator(
            runtime,
            LocalSoundChannelId);
        await coordinator.UpdateOutputDevicesAsync(
            [primary, secondary],
            [],
            primary.Id,
            TestContext.Current.CancellationToken);
        Assert.True(await coordinator.AddRouteAsync(
            LocalSoundChannelId,
            secondary.Id,
            "本地声音",
            TestContext.Current.CancellationToken));

        await coordinator.RemoveRouteAsync(LocalSoundChannelId, secondary.Id);

        Assert.Empty(coordinator.CaptureRoutes());
        Assert.Empty(Assert.Single(coordinator.Snapshot.Outputs).Routes);
        Assert.Equal(1, Assert.Single(runtime.Playbacks).StopCount);
        Assert.Equal(1, Assert.Single(runtime.Playbacks).DisposeCount);
        Assert.Equal(1, Assert.Single(runtime.SystemCaptures).DisposeCount);
    }

    [Fact]
    public async Task ApplicationRoute_OwnsProcessCaptureUntilSourceIsUnregistered()
    {
        Guid channelId = Guid.NewGuid();
        var primary = new TestAudioDevice("primary", "Primary", true);
        var secondary = new TestAudioDevice("secondary", "Secondary", false);
        var runtime = new FakeLocalAudioRoutingRuntime();
        await using var coordinator = new LocalAudioRoutingCoordinator(
            runtime,
            LocalSoundChannelId);
        await coordinator.UpdateOutputDevicesAsync(
            [primary, secondary],
            [],
            primary.Id,
            TestContext.Current.CancellationToken);
        coordinator.RegisterApplicationSource(channelId, 42, "Player", "player.exe");

        Assert.True(await coordinator.AddRouteAsync(
            channelId,
            secondary.Id,
            "Player",
            TestContext.Current.CancellationToken));
        FakeCaptureSource capture = Assert.Single(runtime.ProcessCaptures);
        Assert.Equal("42", capture.SourceKey);
        Assert.Equal(1, capture.StartCount);

        await coordinator.UnregisterApplicationSourceAsync(channelId);

        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal("Player", Assert.Single(
            coordinator.GetRoutes(channelId)).SourceName);
    }

    [Fact]
    public async Task LocalSourceGain_IsAppliedBeforeSecondaryPlayback()
    {
        var primary = new TestAudioDevice("primary", "Primary", true);
        var secondary = new TestAudioDevice("secondary", "Secondary", false);
        var runtime = new FakeLocalAudioRoutingRuntime();
        await using var coordinator = new LocalAudioRoutingCoordinator(
            runtime,
            LocalSoundChannelId);
        await coordinator.UpdateOutputDevicesAsync(
            [primary, secondary],
            [],
            primary.Id,
            TestContext.Current.CancellationToken);
        coordinator.SetLocalSourceGain(0.5f, false);
        Assert.True(await coordinator.AddRouteAsync(
            LocalSoundChannelId,
            secondary.Id,
            "本地声音",
            TestContext.Current.CancellationToken));
        byte[] pcm = new byte[8];
        BitConverter.TryWriteBytes(pcm.AsSpan(0, 4), 0.5f);
        BitConverter.TryWriteBytes(pcm.AsSpan(4, 4), -0.5f);

        await Assert.Single(runtime.SystemCaptures).EmitAsync(pcm);

        AudioFrame frame = Assert.Single(Assert.Single(runtime.Playbacks).Frames);
        Assert.Equal(0.25f, BitConverter.ToSingle(frame.Data.Span[..4]), 3);
        Assert.Equal(-0.25f, BitConverter.ToSingle(frame.Data.Span[4..8]), 3);
    }

    [Fact]
    public async Task RouteMutations_DoNotSynchronouslyBlockTheCallingThread()
    {
        var primary = new TestAudioDevice("primary", "Primary", true);
        var secondary = new TestAudioDevice("secondary", "Secondary", false);
        var runtime = new FakeLocalAudioRoutingRuntime(
            playbackStartDelay: TimeSpan.FromMilliseconds(400),
            playbackStopDelay: TimeSpan.FromMilliseconds(400));
        await using var coordinator = new LocalAudioRoutingCoordinator(
            runtime,
            LocalSoundChannelId);
        await coordinator.UpdateOutputDevicesAsync(
            [primary, secondary],
            [],
            primary.Id,
            TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        Task<bool> addTask = coordinator.AddRouteAsync(
            LocalSoundChannelId,
            secondary.Id,
            "本地声音",
            TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
        Assert.False(addTask.IsCompleted);
        Assert.True(await addTask);

        stopwatch.Restart();
        Task removeTask = coordinator.RemoveRouteAsync(LocalSoundChannelId, secondary.Id);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
        Assert.False(removeTask.IsCompleted);
        await removeTask;
        Assert.Empty(coordinator.CaptureRoutes());
    }

    [Fact]
    public async Task RemoveRoute_DuringSlowAdd_RemovesProjectionImmediatelyAndDoesNotReappear()
    {
        var primary = new TestAudioDevice("primary", "Primary", true);
        var secondary = new TestAudioDevice("secondary", "Secondary", false);
        var runtime = new FakeLocalAudioRoutingRuntime(
            playbackStartDelay: TimeSpan.FromMilliseconds(600));
        await using var coordinator = new LocalAudioRoutingCoordinator(runtime, LocalSoundChannelId);
        await coordinator.UpdateOutputDevicesAsync(
            [primary, secondary], [], primary.Id, TestContext.Current.CancellationToken);

        Task<bool> addTask = coordinator.AddRouteAsync(
            LocalSoundChannelId, secondary.Id, "本地声音", TestContext.Current.CancellationToken);
        Assert.True(runtime.PlaybackStartEntered.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
        Assert.Single(coordinator.CaptureRoutes());
        Assert.Single(Assert.Single(coordinator.Snapshot.Outputs).Routes);

        Task removeTask = coordinator.RemoveRouteAsync(LocalSoundChannelId, secondary.Id);

        Assert.Empty(coordinator.CaptureRoutes());
        Assert.Empty(Assert.Single(coordinator.Snapshot.Outputs).Routes);
        await removeTask;
        Assert.False(await addTask);
        Assert.Empty(coordinator.CaptureRoutes());
        Assert.Empty(Assert.Single(coordinator.Snapshot.Outputs).Routes);
        FakePlaybackSink playback = Assert.Single(runtime.Playbacks);
        Assert.Equal(1, playback.StopCount);
        Assert.Equal(1, playback.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentSnapshotPublications_UseUniqueMonotonicRevisions()
    {
        var primary = new TestAudioDevice("primary", "Primary", true);
        var secondary = new TestAudioDevice("secondary", "Secondary", false);
        await using var coordinator = new LocalAudioRoutingCoordinator(
            new FakeLocalAudioRoutingRuntime(),
            LocalSoundChannelId);
        await coordinator.UpdateOutputDevicesAsync(
            [primary, secondary],
            [],
            primary.Id,
            TestContext.Current.CancellationToken);
        long initialRevision = coordinator.Snapshot.Revision;
        var revisions = new ConcurrentBag<long>();
        coordinator.SnapshotChanged += (_, snapshot) => revisions.Add(snapshot.Revision);
        using var start = new ManualResetEventSlim(false);
        Task[] publications = Enumerable.Range(1, 64)
            .Select(index => Task.Run(() =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                coordinator.RegisterApplicationSource(
                    Guid.NewGuid(),
                    index,
                    $"Application {index}",
                    $"application-{index}");
            }, TestContext.Current.CancellationToken))
            .ToArray();

        start.Set();
        await Task.WhenAll(publications);

        Assert.Equal(64, revisions.Count);
        Assert.Equal(64, revisions.Distinct().Count());
        Assert.Equal(initialRevision + 64, coordinator.Snapshot.Revision);
    }

    private sealed record TestAudioDevice(
        string Id,
        string DisplayName,
        bool IsDefault) : IAudioDevice;

    private sealed class FakeLocalAudioRoutingRuntime(
        TimeSpan? playbackStartDelay = null,
        TimeSpan? playbackStopDelay = null) : ILocalAudioRoutingRuntime
    {
        public List<FakePlaybackSink> Playbacks { get; } = [];
        public List<FakeCaptureSource> SystemCaptures { get; } = [];
        public List<FakeCaptureSource> ProcessCaptures { get; } = [];
        public ManualResetEventSlim PlaybackStartEntered { get; } = new(false);

        public IAudioPlaybackSink CreatePlaybackSink(IAudioDevice device)
        {
            var sink = new FakePlaybackSink(
                playbackStartDelay ?? TimeSpan.Zero,
                playbackStopDelay ?? TimeSpan.Zero,
                PlaybackStartEntered);
            Playbacks.Add(sink);
            return sink;
        }

        public IAudioCaptureSource CreateSystemLoopback(string deviceId)
        {
            var source = new FakeCaptureSource(deviceId);
            SystemCaptures.Add(source);
            return source;
        }

        public IAudioCaptureSource CreateProcessLoopback(int processId)
        {
            var source = new FakeCaptureSource(processId.ToString());
            ProcessCaptures.Add(source);
            return source;
        }
    }

    private sealed class FakeCaptureSource(string sourceKey) : IAudioCaptureSource
    {
        private IAudioFrameSink? sink;
        public string SourceKey { get; } = sourceKey;
        public AudioFormat OutputFormat => AudioFormat.Default;
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }

        public ValueTask StartAsync(
            IAudioFrameSink nextSink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sink = nextSink;
            StartCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask EmitAsync(byte[] pcm) => sink!.WriteAsync(
            new AudioFrame(pcm, AudioFormat.Default, 1, 1),
            CancellationToken.None);
    }

    private sealed class FakePlaybackSink(
        TimeSpan startDelay,
        TimeSpan stopDelay,
        ManualResetEventSlim startEntered) : IAudioPlaybackSink
    {
        public AudioFormat InputFormat => AudioFormat.Default;
        public List<AudioFrame> Frames { get; } = [];
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            startEntered.Set();
            if (startDelay > TimeSpan.Zero)
            {
                Thread.Sleep(startDelay);
            }
            StartCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            Frames.Add(frame with { Data = frame.Data.ToArray() });
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            if (stopDelay > TimeSpan.Zero)
            {
                Thread.Sleep(stopDelay);
            }
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
