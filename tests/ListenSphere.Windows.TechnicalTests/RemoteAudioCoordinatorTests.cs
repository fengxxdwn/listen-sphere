using System.IO;
using System.Threading.Channels;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using ListenSphere.Controller.Coordinators;
using ListenSphere.Network;
using ListenSphere.Protocol;
using ListenSphere.Windows.Bluetooth;
using ListenSphere.Windows.Usb;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class RemoteAudioCoordinatorTests
{
    [Fact]
    public void ConvertPcm16ToStereoFloat_DuplicatesMonoSamples()
    {
        byte[] pcm16 = [0x00, 0x40, 0x00, 0xC0];

        byte[] converted = RemoteAudioCoordinator.ConvertPcm16ToStereoFloat(
            pcm16,
            channelCount: 1,
            frameSamples: 2);

        Assert.Equal(
            [0.5f, 0.5f, -0.5f, -0.5f],
            ToFloatArray(converted));
    }

    [Fact]
    public void ConvertPcm16ToStereoFloat_PreservesStereoLayout()
    {
        byte[] pcm16 = [0x00, 0x20, 0x00, 0xE0];

        byte[] converted = RemoteAudioCoordinator.ConvertPcm16ToStereoFloat(
            pcm16,
            channelCount: 2,
            frameSamples: 1);

        Assert.Equal([0.25f, -0.25f], ToFloatArray(converted));
    }

    [Fact]
    public void ConvertPcm16ToStereoFloat_RejectsMalformedFrames()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemoteAudioCoordinator.ConvertPcm16ToStereoFloat([], 3, 1));
        Assert.Throws<InvalidDataException>(() =>
            RemoteAudioCoordinator.ConvertPcm16ToStereoFloat([0], 1, 1));
    }

    [Fact]
    public async Task Initialize_IsIdempotent_AndDisposeStopsAllReaders()
    {
        var runtime = new FakeRemoteAudioRuntime();
        var processor = new FakeProcessor(routeToMainMixer: false);
        var coordinator = new RemoteAudioCoordinator(runtime, processor);

        coordinator.Initialize();
        coordinator.Initialize();
        await coordinator.DisposeAsync();

        Assert.Equal(1, runtime.NetworkReaderStarts);
        Assert.Equal(1, runtime.BluetoothReaderStarts);
        Assert.Equal(1, runtime.UsbReaderStarts);
        Assert.True(runtime.DiagnosticUpdates >= 1);
    }

    [Fact]
    public async Task NetworkFrames_RunProcessorAndReachMainMixer()
    {
        var runtime = new FakeRemoteAudioRuntime();
        var processor = new FakeProcessor(routeToMainMixer: true);
        await using var coordinator = new RemoteAudioCoordinator(runtime, processor);
        Guid sessionId = Guid.NewGuid();
        coordinator.Initialize();

        for (uint index = 0; index < 8; index++)
        {
            runtime.NetworkFrames.Writer.TryWrite(new NetworkAudioFrame(
                sessionId,
                1,
                index,
                index * 480,
                FloatPcm(0.1f),
                false));
        }

        await runtime.PlaybackWritten.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.True(processor.ProcessedFrames >= 6);
        Assert.NotEmpty(runtime.PlaybackFrames);
        Assert.True(coordinator.MixerStatistics.MixedFrames > 0);
    }

    [Fact]
    public async Task MicrophoneFrames_BypassMainMixer()
    {
        var runtime = new FakeRemoteAudioRuntime();
        var processor = new FakeProcessor(routeToMainMixer: false);
        await using var coordinator = new RemoteAudioCoordinator(runtime, processor);
        coordinator.Initialize();
        runtime.NetworkFrames.Writer.TryWrite(new NetworkAudioFrame(
            Guid.NewGuid(),
            1,
            1,
            480,
            FloatPcm(0.2f),
            false));

        await processor.Processed.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(runtime.PlaybackFrames);
        Assert.Equal(0, coordinator.MixerStatistics.ActiveStreams);
    }

    [Fact]
    public async Task RemoveSession_ClearsMixerOwnership()
    {
        var runtime = new FakeRemoteAudioRuntime();
        var processor = new FakeProcessor(routeToMainMixer: false);
        await using var coordinator = new RemoteAudioCoordinator(runtime, processor);
        Guid sessionId = Guid.NewGuid();

        coordinator.RegisterSession(sessionId, preferredStartupFrames: 18);
        Assert.Equal(1, coordinator.MixerStatistics.ActiveStreams);

        coordinator.RemoveSession(sessionId);

        Assert.Equal(0, coordinator.MixerStatistics.ActiveStreams);
    }

    [Fact]
    public async Task Snapshot_KeepsLossConcealmentAndReceiverOverflowIndependent()
    {
        var runtime = new FakeRemoteAudioRuntime
        {
            NetworkStatistics = new UdpAudioReceiverStatistics(
                2_000, 0, 0, 0, 0, 800, 720, 1_205, 4, 9, 12, 1, 10, 120, 8.5)
        };
        var processor = new FakeProcessor(routeToMainMixer: false);
        await using var coordinator = new RemoteAudioCoordinator(runtime, processor);
        RemoteAudioSnapshot? observed = null;
        coordinator.SnapshotChanged += (_, snapshot) => observed = snapshot;

        coordinator.Initialize();

        Assert.NotNull(observed);
        Assert.Equal(1_205, observed.Network.EstimatedLostDatagrams);
        Assert.Equal(720, observed.Network.ConcealmentFrames);
        Assert.Equal(9, observed.Network.OutputOverflows);
        Assert.Equal(0, observed.Mixer.StreamOverflows);
        Assert.Contains("丢包 1,205", observed.Status, StringComparison.Ordinal);
        Assert.Contains("补偿 720", observed.Status, StringComparison.Ordinal);
        Assert.Contains("接收溢出 9", observed.Status, StringComparison.Ordinal);
        Assert.Contains("混音溢出 0", observed.Status, StringComparison.Ordinal);
    }

    private static byte[] FloatPcm(float value)
    {
        var result = new byte[3_840];
        Span<float> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
            result.AsSpan());
        samples.Fill(value);
        return result;
    }

    private static float[] ToFloatArray(byte[] pcm) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pcm)
            .ToArray();

    private sealed class FakeProcessor(bool routeToMainMixer) : IRemoteAudioFrameProcessor
    {
        public TaskCompletionSource Processed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessedFrames { get; private set; }

        public ValueTask<RemoteAudioFrameResult> ProcessAsync(
            Guid sessionId,
            byte[] pcm,
            ulong timestamp,
            CancellationToken cancellationToken)
        {
            ProcessedFrames++;
            Processed.TrySetResult();
            return ValueTask.FromResult(new RemoteAudioFrameResult(
                routeToMainMixer,
                0.2f,
                default,
                false));
        }
    }

    private sealed class FakeRemoteAudioRuntime : IRemoteAudioRuntime
    {
        public Channel<NetworkAudioFrame> NetworkFrames { get; } =
            Channel.CreateUnbounded<NetworkAudioFrame>();
        public Channel<BluetoothPcm16Frame> BluetoothFrames { get; } =
            Channel.CreateUnbounded<BluetoothPcm16Frame>();
        public Channel<UsbPcm16Frame> UsbFrames { get; } =
            Channel.CreateUnbounded<UsbPcm16Frame>();
        public List<AudioFrame> PlaybackFrames { get; } = [];
        public TaskCompletionSource PlaybackWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int NetworkReaderStarts { get; private set; }
        public int BluetoothReaderStarts { get; private set; }
        public int UsbReaderStarts { get; private set; }
        public int DiagnosticUpdates { get; private set; }
        public int UdpPort => 56974;
        public UdpAudioReceiverStatistics NetworkStatistics { get; set; } = new(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 60, 0);
        public IReadOnlyList<UdpAudioSessionStatistics> NetworkSessionStatistics => [];
        public IReadOnlyList<BluetoothAudioSessionStatistics> BluetoothSessionStatistics => [];
        public AudioPlaybackStatistics PlaybackStatistics { get; } = new(0, 0, 0, 0, 0, 0);

        public IAsyncEnumerable<NetworkAudioFrame> ReadNetworkFramesAsync(
            CancellationToken cancellationToken)
        {
            NetworkReaderStarts++;
            return NetworkFrames.Reader.ReadAllAsync(cancellationToken);
        }

        public IAsyncEnumerable<BluetoothPcm16Frame> ReadBluetoothFramesAsync(
            CancellationToken cancellationToken)
        {
            BluetoothReaderStarts++;
            return BluetoothFrames.Reader.ReadAllAsync(cancellationToken);
        }

        public IAsyncEnumerable<UsbPcm16Frame> ReadUsbFramesAsync(
            CancellationToken cancellationToken)
        {
            UsbReaderStarts++;
            return UsbFrames.Reader.ReadAllAsync(cancellationToken);
        }

        public ValueTask WritePlaybackAsync(
            AudioFrame frame,
            CancellationToken cancellationToken)
        {
            PlaybackFrames.Add(new AudioFrame(
                frame.Data.ToArray(),
                frame.Format,
                frame.SampleFrames,
                frame.Timestamp));
            PlaybackWritten.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void UpdateDiagnosticSnapshot(
            UdpAudioReceiverStatistics network,
            AudioPlaybackStatistics playback,
            RemoteMixerStatistics mixer,
            IReadOnlyList<BluetoothAudioSessionStatistics> bluetooth) =>
            DiagnosticUpdates++;

        public void RecordFailure(string eventName, Exception exception)
        {
        }
    }
}
