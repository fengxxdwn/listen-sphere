using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using ListenSphere.Device;
using ListenSphere.Diagnostics;
using ListenSphere.Network;
using ListenSphere.Windows.Audio;
using ListenSphere.Windows.Devices;
using Serilog;

namespace ListenSphere.Controller;

public sealed class ControllerNetworkViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly ListenSphereControlServer server;
    private readonly PairingCodeService pairingCodes;
    private readonly ITrustedDeviceStore trustStore;
    private readonly IAudioDeviceManager audioDeviceManager;
    private readonly IAudioOutputVolumeController outputVolume;
    private readonly IWindowsDeviceNotificationSource deviceNotifications;
    private readonly DiagnosticArchiveService diagnostics;
    private readonly CancellationTokenSource audioLifetime = new();
    private readonly SemaphoreSlim playbackGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RemoteChannelItemViewModel> channelsBySession = [];
    private readonly RemotePcmMixer remoteMixer = new(3_840);
    private MdnsControllerPublisher? publisher;
    private IAudioPlaybackSink? playback;
    private Task? audioLoop;
    private Task? mixerLoop;
    private TrustedDevice? selectedTrustedDevice;
    private IAudioDevice? selectedPlaybackDevice;
    private CancellationTokenSource? volumeDebounce;
    private CancellationTokenSource? deviceChangeDebounce;
    private string pairingCode = "------";
    private string pairingHint = "点击“生成验证码”以允许新设备配对。";
    private string networkStatus = "控制服务尚未启动";
    private string audioStatus = "远程音频接收尚未启动";
    private string audioErrorText = string.Empty;
    private float masterVolumePercent = 100;
    private bool followSystemDefaultPlayback = true;
    private bool initialized;
    private bool applyingSettings;
    private bool disposed;

    public ControllerNetworkViewModel(
        ListenSphereControlServer server,
        PairingCodeService pairingCodes,
        ITrustedDeviceStore trustStore,
        IAudioDeviceManager audioDeviceManager,
        IAudioOutputVolumeController outputVolume,
        IWindowsDeviceNotificationSource deviceNotifications,
        DiagnosticArchiveService diagnostics,
        LocalDeviceIdentity identity)
    {
        this.server = server;
        this.pairingCodes = pairingCodes;
        this.trustStore = trustStore;
        this.audioDeviceManager = audioDeviceManager;
        this.outputVolume = outputVolume;
        this.deviceNotifications = deviceNotifications;
        this.diagnostics = diagnostics;
        Identity = identity;
        GenerateCodeCommand = new AsyncRelayCommand(GenerateCodeAsync);
        RevokeCommand = new AsyncRelayCommand(
            RevokeSelectedAsync,
            () => SelectedTrustedDevice is not null);
        RefreshOutputsCommand = new AsyncRelayCommand(RefreshOutputsAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? AudioSettingsChanged;

    public LocalDeviceIdentity Identity { get; }
    public ObservableCollection<TrustedDevice> TrustedDevices { get; } = [];
    public ObservableCollection<IAudioDevice> PlaybackDevices { get; } = [];
    public ObservableCollection<RemoteChannelItemViewModel> RemoteChannels { get; } = [];
    public AsyncRelayCommand GenerateCodeCommand { get; }
    public AsyncRelayCommand RevokeCommand { get; }
    public AsyncRelayCommand RefreshOutputsCommand { get; }

    public string PairingCode
    {
        get => pairingCode;
        private set => SetField(ref pairingCode, value);
    }

    public string PairingHint
    {
        get => pairingHint;
        private set => SetField(ref pairingHint, value);
    }

    public string NetworkStatus
    {
        get => networkStatus;
        private set => SetField(ref networkStatus, value);
    }

    public string AudioStatus
    {
        get => audioStatus;
        private set => SetField(ref audioStatus, value);
    }

    public string AudioErrorText
    {
        get => audioErrorText;
        private set => SetField(ref audioErrorText, value);
    }

    public float MasterVolumePercent
    {
        get => masterVolumePercent;
        set
        {
            float normalized = Math.Clamp(value, 0, 100);
            if (!SetField(ref masterVolumePercent, normalized) || applyingSettings)
            {
                return;
            }

            QueueMasterVolumeChange();
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IAudioDevice? SelectedPlaybackDevice
    {
        get => selectedPlaybackDevice;
        set
        {
            if (!SetField(ref selectedPlaybackDevice, value) || applyingSettings || value is null)
            {
                return;
            }

            if (followSystemDefaultPlayback)
            {
                followSystemDefaultPlayback = false;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(FollowSystemDefaultPlayback)));
            }

            _ = SwitchPlaybackAsync(value);
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool FollowSystemDefaultPlayback
    {
        get => followSystemDefaultPlayback;
        set
        {
            if (!SetField(ref followSystemDefaultPlayback, value) || applyingSettings)
            {
                return;
            }

            if (value)
            {
                _ = RefreshOutputsAsync(null);
            }

            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public TrustedDevice? SelectedTrustedDevice
    {
        get => selectedTrustedDevice;
        set
        {
            if (SetField(ref selectedTrustedDevice, value))
            {
                RevokeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task InitializeAsync(
        string? preferredPlaybackDeviceId = null,
        float preferredMasterVolume = 1f,
        bool followSystemDefault = true)
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        applyingSettings = true;
        MasterVolumePercent = Math.Clamp(preferredMasterVolume, 0f, 1f) * 100;
        FollowSystemDefaultPlayback = followSystemDefault;
        applyingSettings = false;
        server.PeerChanged += OnPeerChanged;
        deviceNotifications.Changed += OnDeviceChanged;
        await server.StartAsync();
        publisher = new MdnsControllerPublisher(Identity, server.Port);
        NetworkStatus =
            $"正在发布 {ListenSphereDiscovery.QualifiedServiceName} · TCP {server.Port}";
        await RefreshOutputsAsync(preferredPlaybackDeviceId);
        audioLoop = ConsumeAudioAsync(audioLifetime.Token);
        mixerLoop = PlayMixedAudioAsync(audioLifetime.Token);
        await RefreshTrustedDevicesAsync();
        UpdateDiagnosticSnapshot(
            server.AudioReceiver.Statistics,
            (playback as IAudioPlaybackDiagnostics)?.Statistics ??
            new AudioPlaybackStatistics(0, 0, 0, 0, 0, 0),
            remoteMixer.Statistics);
    }

    public async Task ApplyAudioSettingsAsync(
        string? playbackDeviceId,
        float masterVolume,
        bool followSystemDefault,
        IReadOnlyList<ListenSphere.Configuration.ChannelSettings> channels)
    {
        applyingSettings = true;
        try
        {
            MasterVolumePercent = Math.Clamp(masterVolume, 0f, 1f) * 100;
            FollowSystemDefaultPlayback = followSystemDefault;
            foreach (ListenSphere.Configuration.ChannelSettings settings in channels)
            {
                RemoteChannelItemViewModel? channel = RemoteChannels.FirstOrDefault(
                    item => item.ChannelId == settings.ChannelId);
                channel?.Apply(settings.Volume, settings.IsMuted);
            }

            IAudioDevice? output = followSystemDefault
                ? PlaybackDevices.FirstOrDefault(device => device.IsDefault)
                : PlaybackDevices.FirstOrDefault(device =>
                    string.Equals(device.Id, playbackDeviceId, StringComparison.Ordinal));
            if (output is not null)
            {
                SelectedPlaybackDevice = output;
            }
        }
        finally
        {
            applyingSettings = false;
        }

        if (SelectedPlaybackDevice is not null)
        {
            await SwitchPlaybackAsync(SelectedPlaybackDevice);
            await SetMasterVolumeAsync();
        }

        AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshOutputsAsync() =>
        await RefreshOutputsAsync(SelectedPlaybackDevice?.Id);

    private async Task RefreshOutputsAsync(string? preferredDeviceId)
    {
        try
        {
            IReadOnlyList<IAudioDevice> devices =
                await audioDeviceManager.GetPlaybackDevicesAsync(CancellationToken.None);
            applyingSettings = true;
            PlaybackDevices.Clear();
            foreach (IAudioDevice device in devices)
            {
                PlaybackDevices.Add(device);
            }

            SelectedPlaybackDevice =
                (FollowSystemDefaultPlayback
                    ? devices.FirstOrDefault(device => device.IsDefault)
                    : devices.FirstOrDefault(device =>
                        string.Equals(device.Id, preferredDeviceId, StringComparison.Ordinal))) ??
                devices.FirstOrDefault(device => device.IsDefault) ??
                devices.FirstOrDefault();
            applyingSettings = false;
            if (SelectedPlaybackDevice is null)
            {
                await playbackGate.WaitAsync();
                try
                {
                    if (playback is not null)
                    {
                        IAudioPlaybackSink previous = playback;
                        playback = null;
                        await DisposePlaybackSafelyAsync(previous);
                    }
                }
                finally
                {
                    playbackGate.Release();
                }

                AudioStatus = "没有可用的 Windows 播放设备；远程音频将被接收但不播放。";
                return;
            }

            await SwitchPlaybackAsync(SelectedPlaybackDevice);
            await SetMasterVolumeAsync();
        }
        catch (Exception exception)
        {
            applyingSettings = false;
            AudioErrorText = $"刷新输出设备失败：{exception.Message}";
            Log.Error(exception, "Failed to refresh playback endpoints");
        }
    }

    private async Task SwitchPlaybackAsync(IAudioDevice output)
    {
        await playbackGate.WaitAsync();
        try
        {
            AudioErrorText = string.Empty;
            if (playback is not null)
            {
                IAudioPlaybackSink previous = playback;
                playback = null;
                await DisposePlaybackSafelyAsync(previous);
            }

            var sink = new WasapiPlaybackSink(output.Id);
            sink.PlaybackStopped += OnPlaybackStopped;
            try
            {
                await sink.StartAsync(CancellationToken.None);
                playback = sink;
            }
            catch
            {
                await DisposePlaybackSafelyAsync(sink);
                throw;
            }

            AudioStatus = $"UDP {server.AudioReceiver.Port} · 播放到：{output.DisplayName}";
            diagnostics.Record(
                DiagnosticSeverity.Information,
                "playback.endpoint.ready",
                new Dictionary<string, object?> { ["isDefault"] = output.IsDefault });
        }
        catch (Exception exception)
        {
            playback = null;
            AudioErrorText = $"无法使用输出设备“{output.DisplayName}”：{exception.Message}";
            Log.Error(exception, "Failed to switch playback endpoint {DeviceId}", output.Id);
            diagnostics.Record(
                DiagnosticSeverity.Error,
                "playback.endpoint.failed",
                new Dictionary<string, object?> { ["exceptionType"] = exception.GetType().Name });
        }
        finally
        {
            playbackGate.Release();
        }
    }

    private async Task ConsumeAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            long displayedFrames = 0;
            await foreach (NetworkAudioFrame frame in
                server.AudioReceiver.ReadAllAsync(cancellationToken))
            {
                channelsBySession.TryGetValue(frame.SessionId, out var channel);
                float gain = channel?.EffectiveGain ?? 1f;
                PcmGainProcessor.Apply(frame.Pcm, gain);
                remoteMixer.Enqueue(frame.SessionId, frame.Pcm);

                displayedFrames++;
                if (displayedFrames % 10 == 0 && channel is not null)
                {
                    float peak = AudioLevelCalculator.Calculate(
                        MemoryMarshal.Cast<byte, float>(frame.Pcm)).Peak;
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                        channel.PeakPercent = peak * 100);
                }

            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AudioStatus = "远程音频接收已停止";
        }
        catch (Exception exception)
        {
            AudioErrorText = $"远程音频播放异常：{exception.Message}";
            Log.Error(exception, "Remote audio playback loop failed");
            diagnostics.Record(
                DiagnosticSeverity.Error,
                "playback.loop.failed",
                new Dictionary<string, object?> { ["exceptionType"] = exception.GetType().Name });
        }
    }

    private async Task PlayMixedAudioAsync(CancellationToken cancellationToken)
    {
        var mixedPcm = new byte[3_840];
        ulong timestamp = 0;
        long ticks = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (remoteMixer.TryMixNext(mixedPcm))
                {
                    await WritePlaybackFrameAsync(
                        new AudioFrame(
                            mixedPcm,
                            AudioFormat.Default,
                            480,
                            timestamp),
                        cancellationToken).ConfigureAwait(false);
                }

                timestamp += 480;
                ticks++;
                if (ticks % 100 == 0)
                {
                    UdpAudioReceiverStatistics network = server.AudioReceiver.Statistics;
                    AudioPlaybackStatistics audio =
                        (playback as IAudioPlaybackDiagnostics)?.Statistics ??
                        new AudioPlaybackStatistics(0, 0, 0, 0, 0, 0);
                    RemoteMixerStatistics mixer = remoteMixer.Statistics;
                    UpdateDiagnosticSnapshot(network, audio, mixer);
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                        AudioStatus =
                            $"UDP {server.AudioReceiver.Port} · 混音 {mixer.ActiveStreams} 路" +
                            $" · 缺帧 {mixer.StreamUnderflows:N0}" +
                            $" · 丢包 {network.EstimatedLostDatagrams:N0}" +
                            $" · 队列溢出 {mixer.StreamOverflows:N0}" +
                            $" · 播放缓冲 {audio.BufferedMilliseconds} ms" +
                            $" · 漂移 {audio.EstimatedClockDriftPpm:F0} ppm");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            AudioErrorText = $"远程混音播放异常：{exception.Message}";
            Log.Error(exception, "Remote mixer playback loop failed");
            diagnostics.Record(
                DiagnosticSeverity.Error,
                "mixer.loop.failed",
                new Dictionary<string, object?> { ["exceptionType"] = exception.GetType().Name });
        }
    }

    private async Task WritePlaybackFrameAsync(
        AudioFrame frame,
        CancellationToken cancellationToken)
    {
        IAudioPlaybackSink? failedPlayback = null;
        Exception? playbackFailure = null;
        await playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (playback is not null)
            {
                try
                {
                    await playback.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    failedPlayback = playback;
                    playback = null;
                    playbackFailure = exception;
                }
            }
        }
        finally
        {
            playbackGate.Release();
        }

        if (failedPlayback is not null && playbackFailure is not null)
        {
            await DisposePlaybackSafelyAsync(failedPlayback).ConfigureAwait(false);
            AudioErrorText = $"播放设备暂时不可用，正在自动恢复：{playbackFailure.Message}";
            diagnostics.Record(
                DiagnosticSeverity.Warning,
                "playback.write.failed",
                new Dictionary<string, object?>
                {
                    ["exceptionType"] = playbackFailure.GetType().Name
                });
            OnDeviceChanged(this, WindowsDeviceChange.StateChanged);
        }
    }

    private void OnDeviceChanged(object? sender, WindowsDeviceChange change)
    {
        diagnostics.Record(
            DiagnosticSeverity.Information,
            "windows.audio-device.changed",
            new Dictionary<string, object?> { ["change"] = change.ToString() });
        deviceChangeDebounce?.Cancel();
        deviceChangeDebounce?.Dispose();
        deviceChangeDebounce = new CancellationTokenSource();
        _ = RecoverPlaybackAfterDeviceChangeAsync(change, deviceChangeDebounce.Token);
    }

    private async Task RecoverPlaybackAfterDeviceChangeAsync(
        WindowsDeviceChange change,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(600, cancellationToken);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                AudioStatus = $"检测到音频设备{GetDeviceChangeText(change)}，正在恢复播放…";
            });
            Task refresh = await Application.Current.Dispatcher.InvokeAsync(
                () => RefreshOutputsAsync(SelectedPlaybackDevice?.Id));
            await refresh;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Automatic playback device recovery failed");
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                AudioErrorText = $"播放设备自动恢复失败：{exception.Message}");
        }
    }

    private void OnPlaybackStopped(object? sender, WasapiPlaybackStoppedEventArgs args)
    {
        diagnostics.Record(
            args.Exception is null ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
            "playback.unexpected-stop",
            args.Exception is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["exceptionType"] = args.Exception.GetType().Name
                });
        OnDeviceChanged(this, WindowsDeviceChange.StateChanged);
    }

    private void UpdateDiagnosticSnapshot(
        UdpAudioReceiverStatistics network,
        AudioPlaybackStatistics audio,
        RemoteMixerStatistics mixer)
    {
        diagnostics.UpdateRuntimeSnapshot(new DiagnosticRuntimeSnapshot(
            DateTimeOffset.UtcNow,
            typeof(ControllerNetworkViewModel).Assembly.GetName().Version?.ToString() ?? "unknown",
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
            mixer.MixedFrames,
            mixer.StreamUnderflows,
            mixer.StreamOverflows,
            mixer.ClippedSamples,
            audio.FramesWritten,
            audio.BufferUnderruns,
            audio.BufferOverflows,
            audio.DriftCorrections,
            audio.BufferedMilliseconds,
            audio.EstimatedClockDriftPpm));
    }

    private static string GetDeviceChangeText(WindowsDeviceChange change) => change switch
    {
        WindowsDeviceChange.Added => "接入",
        WindowsDeviceChange.Removed => "移除",
        WindowsDeviceChange.DefaultChanged => "默认项变化",
        _ => "状态变化"
    };

    private async Task DisposePlaybackSafelyAsync(IAudioPlaybackSink sink)
    {
        if (sink is WasapiPlaybackSink wasapiSink)
        {
            wasapiSink.PlaybackStopped -= OnPlaybackStopped;
        }

        try
        {
            await sink.StopAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Playback stop failed during recovery");
        }

        try
        {
            await sink.DisposeAsync();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Playback dispose failed during recovery");
        }
    }

    private void QueueMasterVolumeChange()
    {
        volumeDebounce?.Cancel();
        volumeDebounce?.Dispose();
        volumeDebounce = new CancellationTokenSource();
        _ = SetMasterVolumeAfterDelayAsync(volumeDebounce);
    }

    private async Task SetMasterVolumeAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(120, cancellation.Token);
            await SetMasterVolumeAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                AudioErrorText = $"设置主音量失败：{exception.Message}");
        }
    }

    private async Task SetMasterVolumeAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedPlaybackDevice is not null)
        {
            await outputVolume.SetVolumeAsync(
                SelectedPlaybackDevice.Id,
                MasterVolumePercent / 100,
                cancellationToken);
        }
    }

    private Task GenerateCodeAsync()
    {
        PairingCode code = pairingCodes.Generate();
        PairingCode = code.Value;
        PairingHint = $"验证码单次有效，{code.ExpiresAt.ToLocalTime():HH:mm:ss} 过期。";
        return Task.CompletedTask;
    }

    private async Task RevokeSelectedAsync()
    {
        if (SelectedTrustedDevice is null)
        {
            return;
        }

        Guid deviceId = SelectedTrustedDevice.DeviceId;
        string name = SelectedTrustedDevice.DisplayName;
        await server.RevokeAsync(deviceId, CancellationToken.None);
        NetworkStatus = $"已撤销设备：{name}";
        RemoteChannelItemViewModel? channel =
            RemoteChannels.FirstOrDefault(item => item.ChannelId == deviceId);
        if (channel is not null)
        {
            RemoteChannels.Remove(channel);
        }

        await RefreshTrustedDevicesAsync();
        AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshTrustedDevicesAsync()
    {
        IReadOnlyList<TrustedDevice> devices = await trustStore.GetAllAsync(
            CancellationToken.None);
        TrustedDevices.Clear();
        foreach (TrustedDevice device in devices.OrderBy(item => item.DisplayName))
        {
            TrustedDevices.Add(device);
            EnsureRemoteChannel(device.DeviceId, device.DisplayName);
        }

        SelectedTrustedDevice = null;
    }

    private RemoteChannelItemViewModel EnsureRemoteChannel(Guid deviceId, string displayName)
    {
        RemoteChannelItemViewModel? existing =
            RemoteChannels.FirstOrDefault(item => item.ChannelId == deviceId);
        if (existing is not null)
        {
            existing.DisplayName = displayName;
            return existing;
        }

        var channel = new RemoteChannelItemViewModel(
            deviceId,
            displayName,
            () => AudioSettingsChanged?.Invoke(this, EventArgs.Empty));
        RemoteChannels.Add(channel);
        return channel;
    }

    private void OnPeerChanged(object? sender, ControlPeerEvent args)
    {
        diagnostics.Record(
            args.State == DeviceConnectionState.Faulted
                ? DiagnosticSeverity.Warning
                : DiagnosticSeverity.Information,
            "network.peer.state",
            new Dictionary<string, object?> { ["state"] = args.State.ToString() });
        _ = Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                NetworkStatus = args.Device is null
                    ? args.Message
                    : $"{args.Device.DisplayName} · {args.State} · {args.Message}";
                if (args.Device is null)
                {
                    return;
                }

                RemoteChannelItemViewModel channel =
                    EnsureRemoteChannel(args.Device.DeviceId, args.Device.DisplayName);
                channel.ConnectionState = args.State;
                if (args.State == DeviceConnectionState.Streaming && args.AudioSessionId is Guid sessionId)
                {
                    channel.SessionId = sessionId;
                    channelsBySession[sessionId] = channel;
                    remoteMixer.RegisterStream(sessionId);
                }
                else if (args.State == DeviceConnectionState.Offline)
                {
                    if (args.AudioSessionId is Guid endedSession)
                    {
                        channelsBySession.TryRemove(endedSession, out _);
                        remoteMixer.RemoveStream(endedSession);
                    }

                    channel.SessionId = null;
                    channel.PeakPercent = 0;
                }

                if (args.State is DeviceConnectionState.Connected or DeviceConnectionState.Streaming)
                {
                    PairingCode = "------";
                    PairingHint = "设备已建立证书固定信任；后续将自动重连。";
                    await RefreshTrustedDevicesAsync();
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Failed to apply Controller peer state");
                NetworkStatus = $"更新设备状态失败：{exception.Message}";
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        server.PeerChanged -= OnPeerChanged;
        deviceNotifications.Changed -= OnDeviceChanged;
        volumeDebounce?.Cancel();
        volumeDebounce?.Dispose();
        deviceChangeDebounce?.Cancel();
        deviceChangeDebounce?.Dispose();
        await audioLifetime.CancelAsync();
        publisher?.Dispose();
        publisher = null;
        await server.DisposeAsync();
        if (audioLoop is not null)
        {
            await audioLoop;
        }

        if (mixerLoop is not null)
        {
            await mixerLoop;
        }

        await playbackGate.WaitAsync();
        try
        {
            if (playback is not null)
            {
                await DisposePlaybackSafelyAsync(playback);
                playback = null;
            }
        }
        finally
        {
            playbackGate.Release();
        }

        playbackGate.Dispose();
        audioLifetime.Dispose();
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class RemoteChannelItemViewModel : INotifyPropertyChanged
{
    private readonly Action settingsChanged;
    private string displayName;
    private DeviceConnectionState connectionState;
    private float volumePercent = 100;
    private bool isMuted;
    private float peakPercent;
    private bool applying;

    public RemoteChannelItemViewModel(Guid channelId, string displayName, Action settingsChanged)
    {
        ChannelId = channelId;
        this.displayName = displayName;
        this.settingsChanged = settingsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid ChannelId { get; }
    public Guid? SessionId { get; set; }

    public string DisplayName
    {
        get => displayName;
        set => SetField(ref displayName, value);
    }

    public DeviceConnectionState ConnectionState
    {
        get => connectionState;
        set
        {
            if (SetField(ref connectionState, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsOnline));
            }
        }
    }

    public string StatusText => ConnectionState switch
    {
        DeviceConnectionState.Streaming => "正在传输",
        DeviceConnectionState.Connected => "已连接",
        DeviceConnectionState.Faulted => "连接异常",
        _ => "离线"
    };

    public bool IsOnline => ConnectionState is
        DeviceConnectionState.Connected or DeviceConnectionState.Streaming;

    public float VolumePercent
    {
        get => volumePercent;
        set
        {
            if (SetField(ref volumePercent, Math.Clamp(value, 0, 100)) && !applying)
            {
                settingsChanged();
            }
        }
    }

    public bool IsMuted
    {
        get => isMuted;
        set
        {
            if (SetField(ref isMuted, value))
            {
                OnPropertyChanged(nameof(MuteButtonText));
                if (!applying)
                {
                    settingsChanged();
                }
            }
        }
    }

    public string MuteButtonText => IsMuted ? "取消静音" : "静音";

    public float PeakPercent
    {
        get => peakPercent;
        set => SetField(ref peakPercent, Math.Clamp(value, 0, 100));
    }

    public float EffectiveGain => IsMuted ? 0f : VolumePercent / 100;

    public void Apply(float volume, bool isMuted)
    {
        applying = true;
        try
        {
            VolumePercent = Math.Clamp(volume, 0f, 1f) * 100;
            IsMuted = isMuted;
        }
        finally
        {
            applying = false;
        }
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
