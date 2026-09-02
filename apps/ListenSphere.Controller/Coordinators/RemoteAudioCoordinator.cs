using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using ListenSphere.Diagnostics;
using ListenSphere.Network;
using ListenSphere.Windows.Audio;
using ListenSphere.Windows.Bluetooth;
using ListenSphere.Windows.Usb;
using Serilog;

namespace ListenSphere.Controller.Coordinators;

internal readonly record struct RemoteAudioFrameResult(
    bool RouteToMainMixer,
    float? Peak,
    ChannelDynamicsResult Dynamics,
    bool DuckingActive);

internal readonly record struct RemoteAudioMeterUpdate(
    Guid SessionId,
    float Peak,
    ChannelDynamicsResult Dynamics,
    bool DuckingActive);

internal sealed record RemoteAudioSnapshot(
    UdpAudioReceiverStatistics Network,
    AudioPlaybackStatistics Playback,
    RemoteMixerStatistics Mixer,
    MasterLimiterStatistics Limiter,
    IReadOnlyList<UdpAudioSessionStatistics> NetworkSessions,
    IReadOnlyList<BluetoothAudioSessionStatistics> BluetoothSessions,
    string Status,
    string ErrorText,
    long Revision);

internal interface IRemoteAudioFrameProcessor
{
    ValueTask<RemoteAudioFrameResult> ProcessAsync(
        Guid sessionId,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken);
}

internal interface IRemoteAudioRuntime
{
    int UdpPort { get; }
    IAsyncEnumerable<NetworkAudioFrame> ReadNetworkFramesAsync(
        CancellationToken cancellationToken);
    IAsyncEnumerable<BluetoothPcm16Frame> ReadBluetoothFramesAsync(
        CancellationToken cancellationToken);
    IAsyncEnumerable<UsbPcm16Frame> ReadUsbFramesAsync(
        CancellationToken cancellationToken);
    UdpAudioReceiverStatistics NetworkStatistics { get; }
    IReadOnlyList<UdpAudioSessionStatistics> NetworkSessionStatistics { get; }
    IReadOnlyList<BluetoothAudioSessionStatistics> BluetoothSessionStatistics { get; }
    AudioPlaybackStatistics PlaybackStatistics { get; }
    ValueTask WritePlaybackAsync(AudioFrame frame, CancellationToken cancellationToken);
    void UpdateDiagnosticSnapshot(
        UdpAudioReceiverStatistics network,
        AudioPlaybackStatistics playback,
        RemoteMixerStatistics mixer,
        IReadOnlyList<BluetoothAudioSessionStatistics> bluetooth);
    void RecordFailure(string eventName, Exception exception);
}

internal sealed class ControllerRemoteAudioRuntime(
    ListenSphereControlServer server,
    BluetoothRfcommProbeHost bluetoothHost,
    UsbAccessoryHost usbHost,
    AudioOutputCoordinator audioOutput,
    DiagnosticArchiveService diagnostics) : IRemoteAudioRuntime
{
    public int UdpPort => server.AudioReceiver.Port;
    public UdpAudioReceiverStatistics NetworkStatistics => server.AudioReceiver.Statistics;
    public IReadOnlyList<UdpAudioSessionStatistics> NetworkSessionStatistics =>
        server.AudioReceiver.SessionStatistics.ToArray();
    public IReadOnlyList<BluetoothAudioSessionStatistics> BluetoothSessionStatistics =>
        bluetoothHost.SessionStatistics.ToArray();
    public AudioPlaybackStatistics PlaybackStatistics => audioOutput.PlaybackStatistics;

    public IAsyncEnumerable<NetworkAudioFrame> ReadNetworkFramesAsync(
        CancellationToken cancellationToken) =>
        server.AudioReceiver.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<BluetoothPcm16Frame> ReadBluetoothFramesAsync(
        CancellationToken cancellationToken) =>
        bluetoothHost.ReadAudioFramesAsync(cancellationToken);

    public IAsyncEnumerable<UsbPcm16Frame> ReadUsbFramesAsync(
        CancellationToken cancellationToken) =>
        usbHost.ReadAudioFramesAsync(cancellationToken);

    public ValueTask WritePlaybackAsync(
        AudioFrame frame,
        CancellationToken cancellationToken) =>
        audioOutput.WriteAsync(frame, cancellationToken);

    public void UpdateDiagnosticSnapshot(
        UdpAudioReceiverStatistics network,
        AudioPlaybackStatistics playback,
        RemoteMixerStatistics mixer,
        IReadOnlyList<BluetoothAudioSessionStatistics> bluetooth)
    {
        BluetoothAudioSessionStatistics[] sessions = bluetooth.ToArray();
        diagnostics.UpdateRuntimeSnapshot(new DiagnosticRuntimeSnapshot(
            DateTimeOffset.UtcNow,
            typeof(RemoteAudioCoordinator).Assembly.GetName().Version?.ToString() ?? "unknown",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.TickCount64 / 1000,
            network.DatagramsReceived,
            network.InvalidDatagrams,
            network.AuthenticationFailures,
            network.ConcealmentFrames,
            network.EstimatedLostDatagrams,
            network.LateDatagrams,
            network.OutputOverflows,
            network.OutputQueueDepth,
            network.ActiveSessions,
            network.JitterBufferedFrames,
            network.AdaptiveTargetMilliseconds,
            network.EstimatedJitterMilliseconds,
            sessions.Sum(session => session.EncodedFrames),
            sessions.Sum(session => session.DecodedFrames),
            sessions.Sum(session => session.TimestampGaps),
            sessions.Sum(session => session.QueueDrops),
            sessions.Sum(session => session.DecodeFailures),
            sessions.Sum(session => session.QueueDepth),
            mixer.MixedFrames,
            mixer.StreamUnderflows,
            mixer.StreamOverflows,
            mixer.ClippedSamples,
            playback.FramesWritten,
            playback.BufferUnderruns,
            playback.BufferOverflows,
            playback.DriftCorrections,
            playback.BufferedMilliseconds,
            playback.EstimatedClockDriftPpm));
    }

    public void RecordFailure(string eventName, Exception exception) =>
        diagnostics.Record(
            DiagnosticSeverity.Error,
            eventName,
            new Dictionary<string, object?>
            {
                ["exceptionType"] = exception.GetType().Name
            });
}

internal sealed class RemoteAudioCoordinator : IAsyncDisposable
{
    private const int FloatStereoFrameBytes = 3_840;
    private const int SamplesPerChannel = 480;
    private readonly IRemoteAudioRuntime runtime;
    private readonly IRemoteAudioFrameProcessor processor;
    private readonly RemotePcmMixer mixer = new(
        FloatStereoFrameBytes,
        startupFrames: 6,
        maximumFrames: 30,
        hardClipOutput: false);
    private readonly MasterSoftLimiter limiter = new();
    private readonly ConcurrentDictionary<Guid, long> meterUpdates = [];
    private readonly CancellationTokenSource lifetime = new();
    private Task? networkLoop;
    private Task? bluetoothLoop;
    private Task? usbLoop;
    private Task? mixerLoop;
    private long revision;
    private bool initialized;
    private bool disposed;

    public RemoteAudioCoordinator(
        IRemoteAudioRuntime runtime,
        IRemoteAudioFrameProcessor processor)
    {
        this.runtime = runtime;
        this.processor = processor;
    }

    public event EventHandler<RemoteAudioSnapshot>? SnapshotChanged;
    public event EventHandler<RemoteAudioMeterUpdate>? MeterChanged;

    public RemoteMixerStatistics MixerStatistics => mixer.Statistics;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (initialized)
        {
            return;
        }

        initialized = true;
        networkLoop = ConsumeNetworkAsync(lifetime.Token);
        bluetoothLoop = ConsumeBluetoothAsync(lifetime.Token);
        usbLoop = ConsumeUsbAsync(lifetime.Token);
        mixerLoop = PlayMixedAudioAsync(lifetime.Token);
        PublishSnapshot();
    }

    public void RegisterSession(Guid sessionId, int preferredStartupFrames = 6) =>
        mixer.RegisterStream(sessionId, preferredStartupFrames);

    public void RemoveSession(Guid sessionId)
    {
        mixer.RemoveStream(sessionId);
        meterUpdates.TryRemove(sessionId, out _);
    }

    private async Task ConsumeNetworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (NetworkAudioFrame frame in
                runtime.ReadNetworkFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessFrameAsync(
                    frame.SessionId,
                    frame.Pcm,
                    frame.Timestamp,
                    preferredStartupFrames: 6,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PublishSnapshot(statusOverride: "远程音频接收已停止");
        }
        catch (Exception exception)
        {
            ReportFailure("远程音频播放异常", "playback.loop.failed", exception);
        }
    }

    private async Task ConsumeBluetoothAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (BluetoothPcm16Frame frame in
                runtime.ReadBluetoothFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                byte[] pcm = ConvertPcm16ToStereoFloat(
                    frame.Pcm,
                    frame.ChannelCount,
                    BluetoothRfcommProbeHost.FrameSamples);
                await ProcessFrameAsync(
                    frame.SessionId,
                    pcm,
                    frame.Timestamp,
                    preferredStartupFrames: 18,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFailure("蓝牙音频播放异常", "bluetooth.playback.loop.failed", exception);
        }
    }

    private async Task ConsumeUsbAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (UsbPcm16Frame frame in
                runtime.ReadUsbFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                byte[] pcm = ConvertPcm16ToStereoFloat(
                    frame.Pcm,
                    frame.ChannelCount,
                    UsbAccessoryHost.FrameSamples);
                await ProcessFrameAsync(
                    frame.SessionId,
                    pcm,
                    frame.Timestamp,
                    preferredStartupFrames: 6,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFailure("USB 音频播放异常", "usb.playback.loop.failed", exception);
        }
    }

    private async ValueTask ProcessFrameAsync(
        Guid sessionId,
        byte[] pcm,
        ulong timestamp,
        int preferredStartupFrames,
        CancellationToken cancellationToken)
    {
        RemoteAudioFrameResult result = await processor.ProcessAsync(
            sessionId,
            pcm,
            timestamp,
            cancellationToken).ConfigureAwait(false);
        if (result.RouteToMainMixer)
        {
            mixer.RegisterStream(sessionId, preferredStartupFrames);
            mixer.Enqueue(sessionId, pcm);
        }

        if (result.Peak is float peak && ShouldPublishMeter(sessionId))
        {
            MeterChanged?.Invoke(
                this,
                new RemoteAudioMeterUpdate(
                    sessionId,
                    peak,
                    result.Dynamics,
                    result.DuckingActive));
        }
    }

    private bool ShouldPublishMeter(Guid sessionId)
    {
        long now = Stopwatch.GetTimestamp();
        if (meterUpdates.TryGetValue(sessionId, out long previous) &&
            Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(33))
        {
            return false;
        }

        meterUpdates[sessionId] = now;
        return true;
    }

    private async Task PlayMixedAudioAsync(CancellationToken cancellationToken)
    {
        var mixedPcm = new byte[FloatStereoFrameBytes];
        ulong timestamp = 0;
        long ticks = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (mixer.TryMixNext(mixedPcm))
                {
                    limiter.Process(mixedPcm);
                    await runtime.WritePlaybackAsync(
                        new AudioFrame(
                            mixedPcm,
                            AudioFormat.Default,
                            SamplesPerChannel,
                            timestamp),
                        cancellationToken).ConfigureAwait(false);
                }

                timestamp += SamplesPerChannel;
                ticks++;
                if (ticks % 100 == 0)
                {
                    PublishSnapshot();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFailure("远程混音播放异常", "mixer.loop.failed", exception);
        }
    }

    private void PublishSnapshot(string? statusOverride = null, string errorText = "")
    {
        UdpAudioReceiverStatistics network = runtime.NetworkStatistics;
        AudioPlaybackStatistics playback = runtime.PlaybackStatistics;
        RemoteMixerStatistics mixerStatistics = mixer.Statistics;
        MasterLimiterStatistics limiterStatistics = limiter.Statistics;
        UdpAudioSessionStatistics[] networkSessions =
            runtime.NetworkSessionStatistics.ToArray();
        BluetoothAudioSessionStatistics[] bluetoothSessions =
            runtime.BluetoothSessionStatistics.ToArray();
        runtime.UpdateDiagnosticSnapshot(
            network,
            playback,
            mixerStatistics,
            bluetoothSessions);
        string status = statusOverride ??
            $"UDP {runtime.UdpPort} · 混音 {mixerStatistics.ActiveStreams} 路" +
            $" · 缺帧 {mixerStatistics.StreamUnderflows:N0}" +
            $" · 丢包 {network.EstimatedLostDatagrams:N0}" +
            $" · 补偿 {network.ConcealmentFrames:N0}" +
            $" · 网络缓冲 {network.AdaptiveTargetMilliseconds} ms" +
            $" · 抖动 {network.EstimatedJitterMilliseconds:F1} ms" +
            $" · 接收溢出 {network.OutputOverflows:N0}" +
            $" · 混音溢出 {mixerStatistics.StreamOverflows:N0}" +
            $" · 限幅 {limiterStatistics.LimitedSamples:N0}" +
            $" · 播放缓冲 {playback.BufferedMilliseconds} ms" +
            $" · 漂移 {playback.EstimatedClockDriftPpm:F0} ppm" +
            FormatBluetoothStatus(bluetoothSessions);
        SnapshotChanged?.Invoke(
            this,
            new RemoteAudioSnapshot(
                network,
                playback,
                mixerStatistics,
                limiterStatistics,
                networkSessions,
                bluetoothSessions,
                status,
                errorText,
                Interlocked.Increment(ref revision)));
    }

    private void ReportFailure(string prefix, string eventName, Exception exception)
    {
        Log.Error(exception, "Remote audio coordinator failed in {EventName}", eventName);
        runtime.RecordFailure(eventName, exception);
        PublishSnapshot(errorText: $"{prefix}：{exception.Message}");
    }

    internal static byte[] ConvertPcm16ToStereoFloat(
        byte[] source,
        int channelCount,
        int frameSamples)
    {
        if (channelCount is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        int requiredBytes = checked(frameSamples * channelCount * sizeof(short));
        if (source.Length < requiredBytes)
        {
            throw new InvalidDataException(
                $"PCM16 frame requires {requiredBytes} bytes but received {source.Length}.");
        }

        var pcm = new byte[checked(frameSamples * 2 * sizeof(float))];
        Span<float> stereo = MemoryMarshal.Cast<byte, float>(pcm.AsSpan());
        ReadOnlySpan<byte> input = source;
        for (int sample = 0; sample < frameSamples; sample++)
        {
            int sourceIndex = sample * channelCount;
            float left = BinaryPrimitives.ReadInt16LittleEndian(
                input.Slice(sourceIndex * sizeof(short), sizeof(short))) / 32768f;
            float right = channelCount == 2
                ? BinaryPrimitives.ReadInt16LittleEndian(
                    input.Slice((sourceIndex + 1) * sizeof(short), sizeof(short))) / 32768f
                : left;
            stereo[sample * 2] = left;
            stereo[(sample * 2) + 1] = right;
        }

        return pcm;
    }

    private static string FormatBluetoothStatus(
        IReadOnlyList<BluetoothAudioSessionStatistics> sessions)
    {
        if (sessions.Count == 0)
        {
            return string.Empty;
        }

        long drops = sessions.Sum(session => session.QueueDrops);
        long gaps = sessions.Sum(session => session.TimestampGaps);
        int depth = sessions.Sum(session => session.QueueDepth);
        string formats = string.Join(
            "/",
            sessions.Select(session => session.Codec.ToString())
                .Distinct(StringComparer.Ordinal));
        return $" · 蓝牙 {formats} · 队列 {depth} · 缺口 {gaps} · 丢帧 {drops}";
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lifetime.CancelAsync();
        Task?[] candidates = [networkLoop, bluetoothLoop, usbLoop, mixerLoop];
        Task[] loops = candidates
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (loops.Length > 0)
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        lifetime.Dispose();
    }
}
