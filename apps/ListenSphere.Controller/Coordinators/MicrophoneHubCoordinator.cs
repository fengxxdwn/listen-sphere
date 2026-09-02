using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using ListenSphere.Windows.Audio;
using Serilog;

namespace ListenSphere.Controller.Coordinators;

internal sealed record MicrophoneHubSnapshot(
    IReadOnlyList<IAudioDevice> RecordingDevices,
    IReadOnlyList<IAudioDevice> VirtualOutputDevices,
    IReadOnlyList<IAudioDevice> MonitoringDevices,
    IAudioDevice? SelectedComputerMicrophone,
    IAudioDevice? SelectedVirtualOutput,
    IAudioDevice? SelectedMonitoringDevice,
    bool IsEnabled,
    bool IsMonitoringEnabled,
    float VolumePercent,
    bool IsMuted,
    float AggregatePeakPercent,
    float ComputerPeakPercent,
    string OutputStatus,
    string ActivityStatus,
    string ErrorText,
    long Revision)
{
    public static MicrophoneHubSnapshot Empty { get; } = new(
        Array.Empty<IAudioDevice>(),
        Array.Empty<IAudioDevice>(),
        Array.Empty<IAudioDevice>(),
        null,
        null,
        null,
        false,
        false,
        100,
        false,
        0,
        0,
        "麦克风中枢已关闭",
        string.Empty,
        string.Empty,
        0);
}

internal interface IMicrophoneHubRuntime
{
    ValueTask<IReadOnlyList<IAudioDevice>> GetRecordingDevicesAsync(
        CancellationToken cancellationToken);
    ValueTask SetDefaultRecordingDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken);
    IAudioCaptureSource CreateRecordingCapture(string deviceId);
    IAudioPlaybackSink CreatePlaybackSink(IAudioDevice device);
}

internal sealed class ControllerMicrophoneHubRuntime(
    IAudioDeviceManager deviceManager,
    IWasapiRecordingCaptureSourceFactory recordingCaptureFactory) : IMicrophoneHubRuntime
{
    public ValueTask<IReadOnlyList<IAudioDevice>> GetRecordingDevicesAsync(
        CancellationToken cancellationToken) =>
        deviceManager.GetRecordingDevicesAsync(cancellationToken);

    public ValueTask SetDefaultRecordingDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        deviceManager.SetDefaultRecordingDeviceAsync(deviceId, cancellationToken);

    public IAudioCaptureSource CreateRecordingCapture(string deviceId) =>
        recordingCaptureFactory.Create(deviceId);

    public IAudioPlaybackSink CreatePlaybackSink(IAudioDevice device) =>
        new WasapiPlaybackSink(
            device.Id,
            device is WindowsAudioDevice { IsBluetooth: true }
                ? WasapiPlaybackProfile.BluetoothResilient
                : WasapiPlaybackProfile.Standard);
}

internal sealed class MicrophoneHubCoordinator : IAsyncDisposable
{
    private readonly IMicrophoneHubRuntime runtime;
    private readonly Guid computerMicrophoneChannelId;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SecondaryPlaybackRoute> outputRoutes = [];
    private readonly ConcurrentDictionary<Guid, SecondaryPlaybackRoute> monitoringRoutes = [];
    private readonly ConcurrentDictionary<Guid, PeakState> peaks = [];
    private MicrophoneHubSnapshot snapshot = MicrophoneHubSnapshot.Empty;
    private IAudioCaptureSource? computerCapture;
    private string? computerCaptureDeviceId;
    private long lastPeakPublishAt;
    private bool disposed;

    public MicrophoneHubCoordinator(
        IAudioDeviceManager deviceManager,
        IWasapiRecordingCaptureSourceFactory recordingCaptureFactory,
        Guid computerMicrophoneChannelId)
        : this(
            new ControllerMicrophoneHubRuntime(deviceManager, recordingCaptureFactory),
            computerMicrophoneChannelId)
    {
    }

    internal MicrophoneHubCoordinator(
        IMicrophoneHubRuntime runtime,
        Guid computerMicrophoneChannelId)
    {
        this.runtime = runtime;
        this.computerMicrophoneChannelId = computerMicrophoneChannelId;
    }

    public event EventHandler<MicrophoneHubSnapshot>? SnapshotChanged;
    public event EventHandler? SettingsChanged;

    public MicrophoneHubSnapshot Snapshot => Volatile.Read(ref snapshot);

    public async Task InitializeAsync(
        IReadOnlyList<IAudioDevice> playbackDevices,
        string? primaryPlaybackDeviceId,
        string? preferredVirtualOutputId,
        string? preferredMonitoringDeviceId,
        string? preferredComputerMicrophoneId,
        bool isEnabled,
        bool isMonitoringEnabled,
        float volumePercent,
        bool isMuted,
        CancellationToken cancellationToken = default)
    {
        Update(current => current with
        {
            IsEnabled = isEnabled,
            IsMonitoringEnabled = isEnabled && isMonitoringEnabled,
            VolumePercent = Math.Clamp(volumePercent, 0, 100),
            IsMuted = isMuted
        });
        await RefreshDevicesAsync(
            playbackDevices,
            primaryPlaybackDeviceId,
            preferredVirtualOutputId,
            preferredMonitoringDeviceId,
            preferredComputerMicrophoneId,
            cancellationToken).ConfigureAwait(false);
        if (isEnabled)
        {
            await EnsureComputerCaptureAsync().ConfigureAwait(false);
        }
    }

    public async Task RefreshDevicesAsync(
        IReadOnlyList<IAudioDevice> playbackDevices,
        string? primaryPlaybackDeviceId,
        string? preferredVirtualOutputId = null,
        string? preferredMonitoringDeviceId = null,
        string? preferredComputerMicrophoneId = null,
        CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<IAudioDevice> recordingDevices = await runtime
                .GetRecordingDevicesAsync(cancellationToken).ConfigureAwait(false);
            IAudioDevice[] playback = playbackDevices.ToArray();
            IAudioDevice[] recording = recordingDevices.ToArray();
            IAudioDevice[] virtualOutputs = playback
                .Where(device => IsVirtualMicrophoneRenderEndpoint(device.DisplayName))
                .ToArray();
            MicrophoneHubSnapshot current = Snapshot;
            IAudioDevice? selectedVirtual = FindById(
                    virtualOutputs,
                    preferredVirtualOutputId ?? current.SelectedVirtualOutput?.Id) ??
                virtualOutputs.FirstOrDefault();
            IAudioDevice? selectedMonitoring = FindById(
                    playback,
                    preferredMonitoringDeviceId ?? current.SelectedMonitoringDevice?.Id) ??
                FindById(playback, primaryPlaybackDeviceId);
            IAudioDevice? selectedComputer = recording.FirstOrDefault(device => device.IsDefault) ??
                FindById(
                    recording,
                    preferredComputerMicrophoneId ??
                    current.SelectedComputerMicrophone?.Id) ??
                recording.FirstOrDefault();
            bool captureChanged = !SameDevice(
                current.SelectedComputerMicrophone,
                selectedComputer);
            Update(value => value with
            {
                RecordingDevices = recording,
                VirtualOutputDevices = virtualOutputs,
                MonitoringDevices = playback,
                SelectedVirtualOutput = selectedVirtual,
                SelectedMonitoringDevice = selectedMonitoring,
                SelectedComputerMicrophone = selectedComputer,
                ErrorText = string.Empty
            });
            await ResetRoutesAsync(outputRoutes).ConfigureAwait(false);
            await ResetRoutesAsync(monitoringRoutes).ConfigureAwait(false);
            if (captureChanged)
            {
                await RestartComputerCaptureAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportError($"刷新麦克风设备失败：{exception.Message}");
            Log.Error(exception, "Failed to refresh recording endpoints");
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task SelectVirtualOutputAsync(
        IAudioDevice? device,
        CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IAudioDevice? selected = FindById(Snapshot.VirtualOutputDevices, device?.Id);
            if (SameDevice(Snapshot.SelectedVirtualOutput, selected))
            {
                return;
            }
            Update(current => current with { SelectedVirtualOutput = selected });
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            await ResetRoutesAsync(outputRoutes).ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task SelectMonitoringDeviceAsync(
        IAudioDevice? device,
        CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IAudioDevice? selected = FindById(Snapshot.MonitoringDevices, device?.Id);
            if (SameDevice(Snapshot.SelectedMonitoringDevice, selected))
            {
                return;
            }
            Update(current => current with { SelectedMonitoringDevice = selected });
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            await ResetRoutesAsync(monitoringRoutes).ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task SelectComputerMicrophoneAsync(
        IAudioDevice? device,
        CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IAudioDevice? previous = Snapshot.SelectedComputerMicrophone;
            IAudioDevice? selected = FindById(Snapshot.RecordingDevices, device?.Id);
            if (SameDevice(previous, selected))
            {
                return;
            }
            Update(current => current with { SelectedComputerMicrophone = selected });
            if (selected is null)
            {
                await RestartComputerCaptureAsync().ConfigureAwait(false);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            try
            {
                await runtime.SetDefaultRecordingDeviceAsync(selected.Id, cancellationToken)
                    .ConfigureAwait(false);
                await RestartComputerCaptureAsync().ConfigureAwait(false);
                Update(current => current with
                {
                    ActivityStatus = $"Windows 默认麦克风已切换为 {selected.DisplayName}。",
                    ErrorText = string.Empty
                });
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warning(exception,
                    "Failed to set Windows default recording endpoint {DeviceId}",
                    selected.Id);
                Update(current => current with { SelectedComputerMicrophone = previous });
                await RestartComputerCaptureAsync().ConfigureAwait(false);
                ReportError($"无法修改 Windows 默认麦克风：{exception.Message}");
            }
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        if (Snapshot.IsEnabled == enabled)
        {
            return;
        }
        Update(current => current with
        {
            IsEnabled = enabled,
            IsMonitoringEnabled = enabled && current.IsMonitoringEnabled,
            AggregatePeakPercent = enabled ? current.AggregatePeakPercent : 0,
            ComputerPeakPercent = enabled ? current.ComputerPeakPercent : 0
        });
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (enabled)
        {
            await EnsureComputerCaptureAsync().ConfigureAwait(false);
            return;
        }
        peaks.Clear();
        await StopComputerCaptureAsync().ConfigureAwait(false);
        await ResetRoutesAsync(outputRoutes).ConfigureAwait(false);
        await ResetRoutesAsync(monitoringRoutes).ConfigureAwait(false);
    }

    public async Task SetMonitoringEnabledAsync(bool enabled)
    {
        bool normalized = Snapshot.IsEnabled && enabled;
        if (Snapshot.IsMonitoringEnabled == normalized)
        {
            return;
        }
        Update(current => current with { IsMonitoringEnabled = normalized });
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (!normalized)
        {
            await ResetRoutesAsync(monitoringRoutes).ConfigureAwait(false);
        }
    }

    public void SetVolume(float volumePercent)
    {
        float normalized = Math.Clamp(volumePercent, 0, 100);
        if (Math.Abs(Snapshot.VolumePercent - normalized) < 0.01f)
        {
            return;
        }
        Update(current => current with { VolumePercent = normalized });
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMuted(bool muted)
    {
        if (Snapshot.IsMuted == muted)
        {
            return;
        }
        Update(current => current with { IsMuted = muted });
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask WriteOutputAsync(
        Guid sourceId,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {
        MicrophoneHubSnapshot current = Snapshot;
        if (!current.IsEnabled)
        {
            return;
        }
        ObservePeak(sourceId, pcm);
        if (current.SelectedVirtualOutput is not null)
        {
            await WriteRouteAsync(
                outputRoutes,
                sourceId,
                current.SelectedVirtualOutput,
                pcm,
                timestamp,
                "麦克风输出",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask WriteMonitoringAsync(
        Guid sourceId,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {
        MicrophoneHubSnapshot current = Snapshot;
        if (!current.IsEnabled || !current.IsMonitoringEnabled ||
            current.SelectedMonitoringDevice is null)
        {
            return;
        }
        await WriteRouteAsync(
            monitoringRoutes,
            sourceId,
            current.SelectedMonitoringDevice,
            pcm,
            timestamp,
            "麦克风监听",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveSourceAsync(Guid sourceId)
    {
        await RemoveRouteAsync(outputRoutes, sourceId).ConfigureAwait(false);
        await RemoveRouteAsync(monitoringRoutes, sourceId).ConfigureAwait(false);
        peaks.TryRemove(sourceId, out _);
        PublishPeaks(Environment.TickCount64, force: true);
    }

    private async ValueTask WriteRouteAsync(
        ConcurrentDictionary<Guid, SecondaryPlaybackRoute> routes,
        Guid sourceId,
        IAudioDevice device,
        byte[] pcm,
        ulong timestamp,
        string routeName,
        CancellationToken cancellationToken)
    {
        if (!routes.TryGetValue(sourceId, out SecondaryPlaybackRoute? route))
        {
            var candidate = new SecondaryPlaybackRoute(runtime.CreatePlaybackSink(device));
            try
            {
                await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
                if (!routes.TryAdd(sourceId, candidate))
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }
                route = routes.GetValueOrDefault(sourceId);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                Log.Warning(exception, "Failed to start {RouteName} {DeviceId}", routeName, device.Id);
                ReportError($"{routeName}“{device.DisplayName}”暂不可用：{exception.Message}");
                return;
            }
        }
        if (route is null)
        {
            return;
        }
        try
        {
            MicrophoneHubSnapshot current = Snapshot;
            float gain = current.IsMuted ? 0f : current.VolumePercent / 100f;
            await route.WriteAsync(pcm, timestamp, cancellationToken, gain)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (routes.TryRemove(sourceId, out SecondaryPlaybackRoute? failed))
            {
                await failed.DisposeAsync().ConfigureAwait(false);
            }
            Log.Warning(exception, "{RouteName} write failed", routeName);
            ReportError($"{routeName}中断，刷新设备后可恢复：{exception.Message}");
        }
    }

    private async Task EnsureComputerCaptureAsync()
    {
        MicrophoneHubSnapshot current = Snapshot;
        string? desiredDeviceId = current.IsEnabled
            ? current.SelectedComputerMicrophone?.Id
            : null;
        await captureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (computerCapture is not null && string.Equals(
                    computerCaptureDeviceId,
                    desiredDeviceId,
                    StringComparison.Ordinal))
            {
                return;
            }
            if (computerCapture is not null)
            {
                await computerCapture.DisposeAsync().ConfigureAwait(false);
                computerCapture = null;
                computerCaptureDeviceId = null;
            }
            if (desiredDeviceId is null)
            {
                return;
            }
            IAudioCaptureSource capture = runtime.CreateRecordingCapture(desiredDeviceId);
            try
            {
                await capture.StartAsync(
                    new ComputerMicrophoneFrameSink(this),
                    lifetime.Token).ConfigureAwait(false);
                computerCapture = capture;
                computerCaptureDeviceId = desiredDeviceId;
            }
            catch (Exception exception)
            {
                await capture.DisposeAsync().ConfigureAwait(false);
                ReportError(
                    $"电脑麦克风“{current.SelectedComputerMicrophone!.DisplayName}”无法启动：{exception.Message}");
                Log.Warning(exception, "Failed to capture computer microphone {DeviceId}",
                    desiredDeviceId);
            }
        }
        finally
        {
            captureGate.Release();
        }
    }

    private async Task RestartComputerCaptureAsync()
    {
        await StopComputerCaptureAsync().ConfigureAwait(false);
        if (Snapshot.IsEnabled)
        {
            await EnsureComputerCaptureAsync().ConfigureAwait(false);
        }
    }

    private async Task StopComputerCaptureAsync()
    {
        IAudioCaptureSource? capture;
        await captureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            capture = computerCapture;
            computerCapture = null;
            computerCaptureDeviceId = null;
        }
        finally
        {
            captureGate.Release();
        }
        if (capture is not null)
        {
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        peaks.TryRemove(computerMicrophoneChannelId, out _);
        PublishPeaks(Environment.TickCount64, force: true);
    }

    private void ObservePeak(Guid sourceId, byte[] pcm)
    {
        float peak = AudioLevelCalculator.Calculate(
            MemoryMarshal.Cast<byte, float>(pcm)).Peak * 100;
        long now = Environment.TickCount64;
        peaks[sourceId] = new PeakState(peak, now);
        PublishPeaks(now, force: false);
    }

    private void PublishPeaks(long now, bool force)
    {
        long previous = Interlocked.Read(ref lastPeakPublishAt);
        if (!force && (now - previous < 33 ||
            Interlocked.CompareExchange(ref lastPeakPublishAt, now, previous) != previous))
        {
            return;
        }
        float aggregate = peaks
            .Where(pair => now - pair.Value.ObservedAtMilliseconds <= 500)
            .Select(pair => pair.Value.PeakPercent)
            .DefaultIfEmpty(0)
            .Max();
        float computer = peaks.TryGetValue(
                computerMicrophoneChannelId,
                out PeakState state) &&
            now - state.ObservedAtMilliseconds <= 500
                ? state.PeakPercent
                : 0;
        Update(current => current with
        {
            AggregatePeakPercent = aggregate,
            ComputerPeakPercent = computer
        });
    }

    private async Task ResetRoutesAsync(
        ConcurrentDictionary<Guid, SecondaryPlaybackRoute> routes)
    {
        foreach ((Guid sourceId, SecondaryPlaybackRoute route) in routes.ToArray())
        {
            if (routes.TryRemove(sourceId, out _))
            {
                await route.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task RemoveRouteAsync(
        ConcurrentDictionary<Guid, SecondaryPlaybackRoute> routes,
        Guid sourceId)
    {
        if (routes.TryRemove(sourceId, out SecondaryPlaybackRoute? route))
        {
            await route.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ReportError(string error) =>
        Update(current => current with { ErrorText = error });

    private void Update(Func<MicrophoneHubSnapshot, MicrophoneHubSnapshot> update)
    {
        MicrophoneHubSnapshot next;
        MicrophoneHubSnapshot current;
        do
        {
            current = Snapshot;
            next = update(current);
            next = next with
            {
                OutputStatus = GetOutputStatus(next),
                Revision = current.Revision + 1
            };
        }
        while (!ReferenceEquals(
            Interlocked.CompareExchange(ref snapshot, next, current),
            current));
        SnapshotChanged?.Invoke(this, next);
    }

    private static string GetOutputStatus(MicrophoneHubSnapshot value)
    {
        if (!value.IsEnabled)
        {
            return "麦克风中枢已关闭";
        }
        if (value.SelectedVirtualOutput is null)
        {
            return "未检测到虚拟音频线；需安装 VB-CABLE、VoiceMeeter 或聆界虚拟麦克风驱动";
        }
        IAudioDevice? recording = FindPairedRecordingEndpoint(
            value.SelectedVirtualOutput,
            value.RecordingDevices);
        return recording is null
            ? $"已找到 {value.SelectedVirtualOutput.DisplayName}，但未找到配对的 Windows 录音端点"
            : $"已绑定录音设备：{recording.DisplayName}";
    }

    private static bool IsVirtualMicrophoneRenderEndpoint(string displayName)
    {
        string normalized = displayName.Trim();
        return normalized.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Virtual Cable", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("虚拟音频", StringComparison.OrdinalIgnoreCase);
    }

    private static IAudioDevice? FindPairedRecordingEndpoint(
        IAudioDevice renderDevice,
        IEnumerable<IAudioDevice> recordingDevices)
    {
        string[] preferredNames = renderDevice.DisplayName.Contains(
                "CABLE",
                StringComparison.OrdinalIgnoreCase)
            ? ["CABLE Output", "VB-Audio"]
            : renderDevice.DisplayName.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase)
                ? ["VoiceMeeter Output", "VoiceMeeter"]
                : ["Virtual Cable", "虚拟音频"];
        return recordingDevices.FirstOrDefault(device => preferredNames.Any(name =>
            device.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static IAudioDevice? FindById(
        IEnumerable<IAudioDevice> devices,
        string? deviceId) => deviceId is null
        ? null
        : devices.FirstOrDefault(device => string.Equals(
            device.Id,
            deviceId,
            StringComparison.Ordinal));

    private static bool SameDevice(IAudioDevice? left, IAudioDevice? right) =>
        string.Equals(left?.Id, right?.Id, StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await StopComputerCaptureAsync().ConfigureAwait(false);
        await ResetRoutesAsync(outputRoutes).ConfigureAwait(false);
        await ResetRoutesAsync(monitoringRoutes).ConfigureAwait(false);
        captureGate.Dispose();
        mutationGate.Dispose();
        lifetime.Dispose();
    }

    private readonly record struct PeakState(float PeakPercent, long ObservedAtMilliseconds);

    private sealed class ComputerMicrophoneFrameSink(
        MicrophoneHubCoordinator owner) : IAudioFrameSink
    {
        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            byte[] pcm = frame.Data.ToArray();
            await owner.WriteOutputAsync(
                owner.computerMicrophoneChannelId,
                pcm,
                frame.Timestamp,
                cancellationToken).ConfigureAwait(false);
            await owner.WriteMonitoringAsync(
                owner.computerMicrophoneChannelId,
                pcm,
                frame.Timestamp,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
