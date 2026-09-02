using System.Collections.Concurrent;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Windows.Audio;
using ListenSphere.Windows.Devices;
using Serilog;

namespace ListenSphere.Controller.Coordinators;

internal sealed record AudioOutputEndpointSnapshot(
    IAudioDevice Device,
    float VolumePercent,
    bool IsMuted);

internal sealed record AudioOutputSnapshot(
    IReadOnlyList<IAudioDevice> Devices,
    IReadOnlyList<AudioOutputEndpointSnapshot> Endpoints,
    IAudioDevice? SelectedDevice,
    bool FollowSystemDefault,
    float MasterVolumePercent,
    bool IsSystemMuted,
    string Status,
    string ErrorText,
    long DeviceRevision)
{
    public static AudioOutputSnapshot Empty { get; } = new(
        Array.Empty<IAudioDevice>(),
        Array.Empty<AudioOutputEndpointSnapshot>(),
        null,
        true,
        100,
        false,
        "远程音频接收尚未启动",
        string.Empty,
        0);
}

internal enum AudioOutputEventKind
{
    DeviceChanged,
    EndpointReady,
    EndpointFailed,
    WriteFailed,
    UnexpectedStop
}

internal sealed record AudioOutputEvent(
    AudioOutputEventKind Kind,
    string? DeviceId = null,
    bool? IsDefault = null,
    WindowsDeviceChange? DeviceChange = null,
    Exception? Exception = null);

internal interface IAudioOutputRuntime
{
    event EventHandler<WindowsDeviceChange>? DeviceChanged;

    ValueTask<IReadOnlyList<IAudioDevice>> GetPlaybackDevicesAsync(
        CancellationToken cancellationToken);

    ValueTask<float> GetVolumeAsync(string deviceId, CancellationToken cancellationToken);

    ValueTask SetVolumeAsync(
        string deviceId,
        float volume,
        CancellationToken cancellationToken);

    ValueTask<bool> GetMuteAsync(string deviceId, CancellationToken cancellationToken);

    ValueTask SetMuteAsync(
        string deviceId,
        bool isMuted,
        CancellationToken cancellationToken);

    IAudioPlaybackSink CreatePlaybackSink(IAudioDevice device);
}

internal sealed class ControllerAudioOutputRuntime(
    IAudioDeviceManager deviceManager,
    IAudioOutputVolumeController outputVolume,
    IWindowsDeviceNotificationSource deviceNotifications) : IAudioOutputRuntime
{
    public event EventHandler<WindowsDeviceChange>? DeviceChanged
    {
        add => deviceNotifications.Changed += value;
        remove => deviceNotifications.Changed -= value;
    }

    public ValueTask<IReadOnlyList<IAudioDevice>> GetPlaybackDevicesAsync(
        CancellationToken cancellationToken) =>
        deviceManager.GetPlaybackDevicesAsync(cancellationToken);

    public ValueTask<float> GetVolumeAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        outputVolume.GetVolumeAsync(deviceId, cancellationToken);

    public ValueTask SetVolumeAsync(
        string deviceId,
        float volume,
        CancellationToken cancellationToken) =>
        outputVolume.SetVolumeAsync(deviceId, volume, cancellationToken);

    public ValueTask<bool> GetMuteAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        outputVolume.GetMuteAsync(deviceId, cancellationToken);

    public ValueTask SetMuteAsync(
        string deviceId,
        bool isMuted,
        CancellationToken cancellationToken) =>
        outputVolume.SetMuteAsync(deviceId, isMuted, cancellationToken);

    public IAudioPlaybackSink CreatePlaybackSink(IAudioDevice device) =>
        new WasapiPlaybackSink(
            device.Id,
            device is WindowsAudioDevice { IsBluetooth: true }
                ? WasapiPlaybackProfile.BluetoothResilient
                : WasapiPlaybackProfile.Standard);
}

/// <summary>
/// Owns Windows playback endpoint enumeration, primary playback, endpoint volume state,
/// hot-plug recovery and output-related debouncing. It deliberately does not own local
/// application capture or secondary-route membership; those move in R2 batch 4.
/// </summary>
internal sealed class AudioOutputCoordinator : IAsyncDisposable
{
    private readonly IAudioOutputRuntime runtime;
    private readonly Func<int> audioPort;
    private readonly SemaphoreSlim playbackGate = new(1, 1);
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource>
        endpointVolumeDebounces = new(StringComparer.Ordinal);
    private AudioOutputSnapshot snapshot = AudioOutputSnapshot.Empty;
    private IAudioPlaybackSink? playback;
    private CancellationTokenSource? masterVolumeDebounce;
    private CancellationTokenSource? deviceChangeDebounce;
    private bool initialized;
    private bool disposed;

    public AudioOutputCoordinator(
        IAudioDeviceManager deviceManager,
        IAudioOutputVolumeController outputVolume,
        IWindowsDeviceNotificationSource deviceNotifications,
        Func<int> audioPort)
        : this(
            new ControllerAudioOutputRuntime(
                deviceManager,
                outputVolume,
                deviceNotifications),
            audioPort)
    {
    }

    internal AudioOutputCoordinator(IAudioOutputRuntime runtime, Func<int>? audioPort = null)
    {
        this.runtime = runtime;
        this.audioPort = audioPort ?? (() => 0);
        runtime.DeviceChanged += OnDeviceChanged;
    }

    public event EventHandler<AudioOutputSnapshot>? SnapshotChanged;
    public event EventHandler<AudioOutputEvent>? OutputEvent;

    public AudioOutputSnapshot Snapshot => Volatile.Read(ref snapshot);

    public AudioPlaybackStatistics PlaybackStatistics =>
        (Volatile.Read(ref playback) as IAudioPlaybackDiagnostics)?.Statistics ??
        new AudioPlaybackStatistics(0, 0, 0, 0, 0, 0);

    public async Task InitializeAsync(
        string? preferredDeviceId,
        float preferredMasterVolume,
        bool followSystemDefault,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            Update(current => current with
            {
                FollowSystemDefault = followSystemDefault,
                MasterVolumePercent = Math.Clamp(preferredMasterVolume, 0, 100)
            });
            await RefreshCoreAsync(
                preferredDeviceId,
                synchronizeMasterVolume: true,
                cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(Snapshot.SelectedDevice?.Id, true, cancellationToken);

    public async Task ApplySettingsAsync(
        string? preferredDeviceId,
        float masterVolumePercent,
        bool followSystemDefault,
        CancellationToken cancellationToken = default)
    {
        Update(current => current with
        {
            FollowSystemDefault = followSystemDefault,
            MasterVolumePercent = Math.Clamp(masterVolumePercent, 0, 100)
        });
        await RefreshCoreAsync(
            preferredDeviceId,
            synchronizeMasterVolume: false,
            cancellationToken).ConfigureAwait(false);
        IAudioDevice? selected = Snapshot.SelectedDevice;
        if (selected is not null)
        {
            await runtime.SetVolumeAsync(
                selected.Id,
                Snapshot.MasterVolumePercent / 100,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SelectDeviceAsync(
        IAudioDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IAudioDevice selected = Snapshot.Devices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, device.Id, StringComparison.Ordinal)) ?? device;
            Update(current => current with
            {
                SelectedDevice = selected,
                FollowSystemDefault = false
            });
            await SwitchPlaybackAsync(selected, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async Task SetFollowSystemDefaultAsync(
        bool followSystemDefault,
        CancellationToken cancellationToken = default)
    {
        if (Snapshot.FollowSystemDefault == followSystemDefault)
        {
            return;
        }

        Update(current => current with { FollowSystemDefault = followSystemDefault });
        if (followSystemDefault)
        {
            await RefreshCoreAsync(null, true, cancellationToken).ConfigureAwait(false);
        }
    }

    public void SetMasterVolume(float volumePercent)
    {
        float normalized = Math.Clamp(volumePercent, 0, 100);
        if (Math.Abs(Snapshot.MasterVolumePercent - normalized) < 0.01f)
        {
            return;
        }

        Update(current => current with { MasterVolumePercent = normalized });
        masterVolumeDebounce?.Cancel();
        masterVolumeDebounce?.Dispose();
        masterVolumeDebounce = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = SetMasterVolumeAfterDelayAsync(masterVolumeDebounce);
    }

    public async Task SetSystemMuteAsync(
        bool isMuted,
        CancellationToken cancellationToken = default)
    {
        IAudioDevice? selected = Snapshot.SelectedDevice;
        if (selected is null || Snapshot.IsSystemMuted == isMuted)
        {
            return;
        }

        Update(current => current with { IsSystemMuted = isMuted });
        try
        {
            await runtime.SetMuteAsync(selected.Id, isMuted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportError($"设置系统静音失败：{exception.Message}");
            Log.Warning(exception, "Failed to set output endpoint mute");
        }
    }

    public void SetEndpointVolume(string deviceId, float volume)
    {
        float normalized = Math.Clamp(volume, 0, 1);
        UpdateEndpoint(deviceId, endpoint => endpoint with
        {
            VolumePercent = normalized * 100
        });
        if (endpointVolumeDebounces.TryRemove(
                deviceId,
                out CancellationTokenSource? previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        endpointVolumeDebounces[deviceId] = cancellation;
        _ = SetEndpointVolumeAfterDelayAsync(deviceId, normalized, cancellation);
    }

    public async Task SetEndpointMuteAsync(
        string deviceId,
        bool isMuted,
        CancellationToken cancellationToken = default)
    {
        UpdateEndpoint(deviceId, endpoint => endpoint with { IsMuted = isMuted });
        try
        {
            await runtime.SetMuteAsync(deviceId, isMuted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportError($"设置输出设备静音失败：{exception.Message}");
            Log.Warning(exception, "Failed to set output endpoint mute for {DeviceId}", deviceId);
        }
    }

    public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
    {
        IAudioPlaybackSink? failed = null;
        Exception? failure = null;
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
                    failed = playback;
                    playback = null;
                    failure = exception;
                }
            }
        }
        finally
        {
            playbackGate.Release();
        }

        if (failed is not null && failure is not null)
        {
            await DisposePlaybackSafelyAsync(failed).ConfigureAwait(false);
            ReportError($"播放设备暂时不可用，正在自动恢复：{failure.Message}");
            OutputEvent?.Invoke(this, new AudioOutputEvent(
                AudioOutputEventKind.WriteFailed,
                Snapshot.SelectedDevice?.Id,
                Exception: failure));
            ScheduleDeviceRecovery(WindowsDeviceChange.StateChanged);
        }
    }

    private async Task RefreshCoreAsync(
        string? preferredDeviceId,
        bool synchronizeMasterVolume,
        CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreUnderLockAsync(
                preferredDeviceId,
                synchronizeMasterVolume,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task RefreshCoreUnderLockAsync(
        string? preferredDeviceId,
        bool synchronizeMasterVolume,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            IReadOnlyList<IAudioDevice> devices = await runtime
                .GetPlaybackDevicesAsync(cancellationToken)
                .ConfigureAwait(false);
            IAudioDevice[] deviceSnapshot = devices.ToArray();
            AudioOutputSnapshot current = Snapshot;
            IAudioDevice? selected =
                (current.FollowSystemDefault
                    ? deviceSnapshot.FirstOrDefault(device => device.IsDefault)
                    : deviceSnapshot.FirstOrDefault(device => string.Equals(
                        device.Id,
                        preferredDeviceId ?? current.SelectedDevice?.Id,
                        StringComparison.Ordinal))) ??
                deviceSnapshot.FirstOrDefault(device => device.IsDefault) ??
                deviceSnapshot.FirstOrDefault();
            AudioOutputEndpointSnapshot[] endpoints = await ReadEndpointsAsync(
                deviceSnapshot,
                cancellationToken).ConfigureAwait(false);
            Update(value => value with
            {
                Devices = deviceSnapshot,
                Endpoints = endpoints,
                SelectedDevice = selected,
                DeviceRevision = value.DeviceRevision + 1,
                ErrorText = string.Empty
            });

            if (selected is null)
            {
                await ClearPlaybackAsync(cancellationToken).ConfigureAwait(false);
                Update(value => value with
                {
                    Status = "没有可用的 Windows 播放设备；远程音频将被接收但不播放。"
                });
                return;
            }

            await SwitchPlaybackAsync(selected, synchronizeMasterVolume, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportError($"刷新输出设备失败：{exception.Message}");
            Log.Error(exception, "Failed to refresh playback endpoints");
        }
    }

    private async Task<AudioOutputEndpointSnapshot[]> ReadEndpointsAsync(
        IReadOnlyList<IAudioDevice> devices,
        CancellationToken cancellationToken)
    {
        var result = new List<AudioOutputEndpointSnapshot>(devices.Count);
        foreach (IAudioDevice device in devices)
        {
            float volume = 100;
            bool muted = false;
            try
            {
                volume = await runtime.GetVolumeAsync(device.Id, cancellationToken)
                    .ConfigureAwait(false) * 100;
                muted = await runtime.GetMuteAsync(device.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warning(exception, "Failed to read output endpoint state for {DeviceId}", device.Id);
            }
            result.Add(new AudioOutputEndpointSnapshot(device, volume, muted));
        }
        return result.ToArray();
    }

    private async Task SwitchPlaybackAsync(
        IAudioDevice output,
        bool synchronizeMasterVolume,
        CancellationToken cancellationToken)
    {
        await playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (playback is not null)
            {
                IAudioPlaybackSink previous = playback;
                playback = null;
                await DisposePlaybackSafelyAsync(previous).ConfigureAwait(false);
            }

            IAudioPlaybackSink sink = runtime.CreatePlaybackSink(output);
            SubscribePlaybackStopped(sink);
            try
            {
                await sink.StartAsync(cancellationToken).ConfigureAwait(false);
                playback = sink;
            }
            catch
            {
                await DisposePlaybackSafelyAsync(sink).ConfigureAwait(false);
                throw;
            }

            float master = Snapshot.MasterVolumePercent;
            if (synchronizeMasterVolume)
            {
                master = Math.Clamp(
                    await runtime.GetVolumeAsync(output.Id, cancellationToken)
                        .ConfigureAwait(false),
                    0,
                    1) * 100;
            }
            bool muted = await runtime.GetMuteAsync(output.Id, cancellationToken)
                .ConfigureAwait(false);
            Update(current => current with
            {
                SelectedDevice = output,
                MasterVolumePercent = master,
                IsSystemMuted = muted,
                Status = $"UDP {audioPort()} · 播放到：{output.DisplayName}",
                ErrorText = string.Empty
            });
            OutputEvent?.Invoke(this, new AudioOutputEvent(
                AudioOutputEventKind.EndpointReady,
                output.Id,
                output.IsDefault));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            playback = null;
            ReportError($"无法使用输出设备“{output.DisplayName}”：{exception.Message}");
            Log.Error(exception, "Failed to switch playback endpoint {DeviceId}", output.Id);
            OutputEvent?.Invoke(this, new AudioOutputEvent(
                AudioOutputEventKind.EndpointFailed,
                output.Id,
                output.IsDefault,
                Exception: exception));
        }
        finally
        {
            playbackGate.Release();
        }
    }

    private async Task ClearPlaybackAsync(CancellationToken cancellationToken)
    {
        await playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (playback is not null)
            {
                IAudioPlaybackSink previous = playback;
                playback = null;
                await DisposePlaybackSafelyAsync(previous).ConfigureAwait(false);
            }
        }
        finally
        {
            playbackGate.Release();
        }
    }

    private void OnDeviceChanged(object? sender, WindowsDeviceChange change)
    {
        OutputEvent?.Invoke(this, new AudioOutputEvent(
            AudioOutputEventKind.DeviceChanged,
            DeviceChange: change));
        ScheduleDeviceRecovery(change);
    }

    private void ScheduleDeviceRecovery(WindowsDeviceChange change)
    {
        deviceChangeDebounce?.Cancel();
        deviceChangeDebounce?.Dispose();
        deviceChangeDebounce = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = RecoverAfterDeviceChangeAsync(change, deviceChangeDebounce.Token);
    }

    private async Task RecoverAfterDeviceChangeAsync(
        WindowsDeviceChange change,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
            Update(current => current with
            {
                Status = $"检测到音频设备{GetDeviceChangeText(change)}，正在恢复播放…"
            });
            await RefreshCoreAsync(Snapshot.SelectedDevice?.Id, true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportError($"播放设备自动恢复失败：{exception.Message}");
            Log.Error(exception, "Automatic playback device recovery failed");
        }
    }

    private void OnPlaybackStopped(object? sender, WasapiPlaybackStoppedEventArgs args)
    {
        OutputEvent?.Invoke(this, new AudioOutputEvent(
            AudioOutputEventKind.UnexpectedStop,
            Snapshot.SelectedDevice?.Id,
            Exception: args.Exception));
        ScheduleDeviceRecovery(WindowsDeviceChange.StateChanged);
    }

    private void SubscribePlaybackStopped(IAudioPlaybackSink sink)
    {
        if (sink is WasapiPlaybackSink wasapi)
        {
            wasapi.PlaybackStopped += OnPlaybackStopped;
        }
    }

    private void UnsubscribePlaybackStopped(IAudioPlaybackSink sink)
    {
        if (sink is WasapiPlaybackSink wasapi)
        {
            wasapi.PlaybackStopped -= OnPlaybackStopped;
        }
    }

    private async Task DisposePlaybackSafelyAsync(IAudioPlaybackSink sink)
    {
        UnsubscribePlaybackStopped(sink);
        try
        {
            await sink.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Playback stop failed during recovery");
        }
        try
        {
            await sink.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Playback dispose failed during recovery");
        }
    }

    private async Task SetMasterVolumeAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(120, cancellation.Token).ConfigureAwait(false);
            IAudioDevice? selected = Snapshot.SelectedDevice;
            if (selected is not null)
            {
                await runtime.SetVolumeAsync(
                    selected.Id,
                    Snapshot.MasterVolumePercent / 100,
                    cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportError($"设置主音量失败：{exception.Message}");
        }
    }

    private async Task SetEndpointVolumeAfterDelayAsync(
        string deviceId,
        float volume,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(120, cancellation.Token).ConfigureAwait(false);
            await runtime.SetVolumeAsync(deviceId, volume, cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportError($"设置输出设备音量失败：{exception.Message}");
        }
        finally
        {
            if (endpointVolumeDebounces.TryGetValue(deviceId, out var current) &&
                ReferenceEquals(current, cancellation))
            {
                endpointVolumeDebounces.TryRemove(deviceId, out _);
            }
            cancellation.Dispose();
        }
    }

    private void UpdateEndpoint(
        string deviceId,
        Func<AudioOutputEndpointSnapshot, AudioOutputEndpointSnapshot> update)
    {
        AudioOutputEndpointSnapshot[] endpoints = Snapshot.Endpoints
            .Select(endpoint => string.Equals(
                    endpoint.Device.Id,
                    deviceId,
                    StringComparison.Ordinal)
                ? update(endpoint)
                : endpoint)
            .ToArray();
        Update(current => current with { Endpoints = endpoints });
    }

    private void ReportError(string value) =>
        Update(current => current with { ErrorText = value });

    private void Update(Func<AudioOutputSnapshot, AudioOutputSnapshot> update)
    {
        AudioOutputSnapshot next;
        AudioOutputSnapshot current;
        do
        {
            current = Snapshot;
            next = update(current);
        }
        while (!ReferenceEquals(
            Interlocked.CompareExchange(ref snapshot, next, current),
            current));
        SnapshotChanged?.Invoke(this, next);
    }

    private static string GetDeviceChangeText(WindowsDeviceChange change) => change switch
    {
        WindowsDeviceChange.Added => "接入",
        WindowsDeviceChange.Removed => "移除",
        WindowsDeviceChange.DefaultChanged => "默认项变化",
        _ => "状态变化"
    };

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        runtime.DeviceChanged -= OnDeviceChanged;
        await lifetime.CancelAsync().ConfigureAwait(false);
        masterVolumeDebounce?.Cancel();
        masterVolumeDebounce?.Dispose();
        deviceChangeDebounce?.Cancel();
        deviceChangeDebounce?.Dispose();
        foreach (CancellationTokenSource cancellation in endpointVolumeDebounces.Values)
        {
            cancellation.Cancel();
        }
        endpointVolumeDebounces.Clear();
        await refreshGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await ClearPlaybackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            refreshGate.Release();
        }
        playbackGate.Dispose();
        initializationGate.Dispose();
        refreshGate.Dispose();
        lifetime.Dispose();
    }
}
