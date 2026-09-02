using ListenSphere.Audio.Abstractions;
using ListenSphere.Controller.Coordinators;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class MicrophoneHubCoordinatorTests
{
    private static readonly Guid ComputerMicrophoneChannelId = Guid.Parse(
        "603669e2-6a20-4d81-8bc0-24df59ed6d42");

    [Fact]
    public async Task InitializeAsync_ProjectsDevicesAndKeepsHubDisabledByDefault()
    {
        var speaker = new TestAudioDevice("speaker", "Speaker", true);
        var cableInput = new TestAudioDevice("cable-in", "CABLE Input (VB-Audio)", false);
        var microphone = new TestAudioDevice("mic", "Desktop Microphone", true);
        var cableOutput = new TestAudioDevice("cable-out", "CABLE Output (VB-Audio)", false);
        var runtime = new FakeMicrophoneHubRuntime(microphone, cableOutput);
        await using var coordinator = new MicrophoneHubCoordinator(
            runtime,
            ComputerMicrophoneChannelId);

        await coordinator.InitializeAsync(
            [speaker, cableInput],
            speaker.Id,
            null,
            null,
            null,
            false,
            false,
            80,
            false,
            TestContext.Current.CancellationToken);

        Assert.False(coordinator.Snapshot.IsEnabled);
        Assert.Equal(cableInput.Id, coordinator.Snapshot.SelectedVirtualOutput?.Id);
        Assert.Equal(speaker.Id, coordinator.Snapshot.SelectedMonitoringDevice?.Id);
        Assert.Equal(microphone.Id, coordinator.Snapshot.SelectedComputerMicrophone?.Id);
        Assert.Equal("麦克风中枢已关闭", coordinator.Snapshot.OutputStatus);
        Assert.Empty(runtime.Captures);
    }

    [Fact]
    public async Task ComputerCapture_WritesVirtualOutputAndMonitoringWithConfiguredGain()
    {
        var speaker = new TestAudioDevice("speaker", "Speaker", true);
        var cableInput = new TestAudioDevice("cable-in", "CABLE Input (VB-Audio)", false);
        var microphone = new TestAudioDevice("mic", "Desktop Microphone", true);
        var runtime = new FakeMicrophoneHubRuntime(microphone);
        await using var coordinator = new MicrophoneHubCoordinator(
            runtime,
            ComputerMicrophoneChannelId);
        await coordinator.InitializeAsync(
            [speaker, cableInput],
            speaker.Id,
            cableInput.Id,
            speaker.Id,
            microphone.Id,
            true,
            true,
            50,
            false,
            TestContext.Current.CancellationToken);
        byte[] pcm = FloatPcm(0.5f, -0.5f);

        await Assert.Single(runtime.Captures).EmitAsync(pcm);

        Assert.Equal(2, runtime.Playbacks.Count);
        foreach (FakePlaybackSink playback in runtime.Playbacks)
        {
            AudioFrame frame = Assert.Single(playback.Frames);
            Assert.Equal(0.25f, BitConverter.ToSingle(frame.Data.Span[..4]), 3);
            Assert.Equal(-0.25f, BitConverter.ToSingle(frame.Data.Span[4..8]), 3);
        }
        Assert.True(coordinator.Snapshot.AggregatePeakPercent > 0);
        Assert.True(coordinator.Snapshot.ComputerPeakPercent > 0);
    }

    [Fact]
    public async Task DisableHub_DisposesCaptureAndRoutesAndClearsPeaks()
    {
        var speaker = new TestAudioDevice("speaker", "Speaker", true);
        var cableInput = new TestAudioDevice("cable-in", "CABLE Input", false);
        var microphone = new TestAudioDevice("mic", "Desktop Microphone", true);
        var runtime = new FakeMicrophoneHubRuntime(microphone);
        await using var coordinator = new MicrophoneHubCoordinator(
            runtime,
            ComputerMicrophoneChannelId);
        await coordinator.InitializeAsync(
            [speaker, cableInput], speaker.Id, cableInput.Id, speaker.Id, microphone.Id,
            true, true, 100, false, TestContext.Current.CancellationToken);
        FakeCaptureSource capture = Assert.Single(runtime.Captures);
        await capture.EmitAsync(FloatPcm(0.25f, 0.25f));

        await coordinator.SetEnabledAsync(false);

        Assert.Equal(1, capture.DisposeCount);
        Assert.All(runtime.Playbacks, sink => Assert.Equal(1, sink.DisposeCount));
        Assert.False(coordinator.Snapshot.IsMonitoringEnabled);
        Assert.Equal(0, coordinator.Snapshot.AggregatePeakPercent);
        Assert.Equal(0, coordinator.Snapshot.ComputerPeakPercent);
    }

    [Fact]
    public async Task SelectComputerMicrophone_ChangesWindowsDefaultAndRestartsCapture()
    {
        var speaker = new TestAudioDevice("speaker", "Speaker", true);
        var first = new TestAudioDevice("first", "First Microphone", true);
        var second = new TestAudioDevice("second", "Second Microphone", false);
        var runtime = new FakeMicrophoneHubRuntime(first, second);
        await using var coordinator = new MicrophoneHubCoordinator(
            runtime,
            ComputerMicrophoneChannelId);
        await coordinator.InitializeAsync(
            [speaker], speaker.Id, null, speaker.Id, first.Id,
            true, false, 100, false, TestContext.Current.CancellationToken);
        FakeCaptureSource firstCapture = Assert.Single(runtime.Captures);

        await coordinator.SelectComputerMicrophoneAsync(
            second,
            TestContext.Current.CancellationToken);

        Assert.Equal([second.Id], runtime.DefaultDeviceWrites);
        Assert.Equal(second.Id, coordinator.Snapshot.SelectedComputerMicrophone?.Id);
        Assert.Equal(1, firstCapture.DisposeCount);
        Assert.Equal(second.Id, runtime.Captures[1].SourceDeviceId);
    }

    [Fact]
    public async Task SelectComputerMicrophone_WhenWindowsRejectsChange_RevertsSelection()
    {
        var first = new TestAudioDevice("first", "First Microphone", true);
        var second = new TestAudioDevice("second", "Second Microphone", false);
        var runtime = new FakeMicrophoneHubRuntime(first, second)
        {
            FailDefaultDeviceChange = true
        };
        await using var coordinator = new MicrophoneHubCoordinator(
            runtime,
            ComputerMicrophoneChannelId);
        await coordinator.InitializeAsync(
            [], null, null, null, first.Id,
            true, false, 100, false, TestContext.Current.CancellationToken);

        await coordinator.SelectComputerMicrophoneAsync(
            second,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, coordinator.Snapshot.SelectedComputerMicrophone?.Id);
        Assert.Contains("无法修改 Windows 默认麦克风", coordinator.Snapshot.ErrorText);
        Assert.Equal(first.Id, runtime.Captures[^1].SourceDeviceId);
    }

    private static byte[] FloatPcm(float left, float right)
    {
        byte[] pcm = new byte[8];
        BitConverter.TryWriteBytes(pcm.AsSpan(0, 4), left);
        BitConverter.TryWriteBytes(pcm.AsSpan(4, 4), right);
        return pcm;
    }

    private sealed record TestAudioDevice(
        string Id,
        string DisplayName,
        bool IsDefault) : IAudioDevice;

    private sealed class FakeMicrophoneHubRuntime(params IAudioDevice[] recordingDevices) :
        IMicrophoneHubRuntime
    {
        public IReadOnlyList<IAudioDevice> RecordingDevices { get; } = recordingDevices;
        public List<string> DefaultDeviceWrites { get; } = [];
        public List<FakeCaptureSource> Captures { get; } = [];
        public List<FakePlaybackSink> Playbacks { get; } = [];
        public bool FailDefaultDeviceChange { get; set; }

        public ValueTask<IReadOnlyList<IAudioDevice>> GetRecordingDevicesAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(RecordingDevices);

        public ValueTask SetDefaultRecordingDeviceAsync(
            string deviceId,
            CancellationToken cancellationToken)
        {
            DefaultDeviceWrites.Add(deviceId);
            return FailDefaultDeviceChange
                ? ValueTask.FromException(new InvalidOperationException("rejected"))
                : ValueTask.CompletedTask;
        }

        public IAudioCaptureSource CreateRecordingCapture(string deviceId)
        {
            var capture = new FakeCaptureSource(deviceId);
            Captures.Add(capture);
            return capture;
        }

        public IAudioPlaybackSink CreatePlaybackSink(IAudioDevice device)
        {
            var playback = new FakePlaybackSink(device.Id);
            Playbacks.Add(playback);
            return playback;
        }
    }

    private sealed class FakeCaptureSource(string sourceDeviceId) : IAudioCaptureSource
    {
        private IAudioFrameSink? sink;
        public string SourceDeviceId { get; } = sourceDeviceId;
        public AudioFormat OutputFormat => AudioFormat.Default;
        public int DisposeCount { get; private set; }

        public ValueTask StartAsync(IAudioFrameSink nextSink, CancellationToken cancellationToken)
        {
            sink = nextSink;
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

    private sealed class FakePlaybackSink(string deviceId) : IAudioPlaybackSink
    {
        public string DeviceId { get; } = deviceId;
        public AudioFormat InputFormat => AudioFormat.Default;
        public List<AudioFrame> Frames { get; } = [];
        public int DisposeCount { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            Frames.Add(frame with { Data = frame.Data.ToArray() });
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
