using System.Collections.ObjectModel;
using System.Windows;
using ListenSphere.Configuration;
using ListenSphere.Windows.AudioSessions;
using Serilog;

namespace ListenSphere.Controller.Presentation;

public sealed class LocalSessionsViewModel : ObservableViewModel, IAsyncDisposable
{
    private readonly IWindowsAudioSessionManager sessionManager;
    private readonly ControllerNetworkViewModel network;
    private readonly int controllerProcessId = Environment.ProcessId;
    private readonly Dictionary<string, CancellationTokenSource> volumeDebounce =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> localSessionBaseVolumes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> localApplicationBaseVolumes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> localSessionBaseMutes =
        new(StringComparer.Ordinal);
    private string statusText = "正在读取 Windows 应用音频会话…";
    private string errorText = string.Empty;
    private float peakPercent;
    private float previousLocalSourceVolumePercent = 100;
    private bool applyingLocalSourceControl;
    private bool initialized;
    private bool disposed;

    public LocalSessionsViewModel(
        IWindowsAudioSessionManager sessionManager,
        ControllerNetworkViewModel network)
    {
        this.sessionManager = sessionManager;
        this.network = network;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public event EventHandler? SettingsChanged;

    public ObservableCollection<AudioSessionItemViewModel> Sessions { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public string ErrorText
    {
        get => errorText;
        private set => SetField(ref errorText, value);
    }

    public float PeakPercent
    {
        get => peakPercent;
        private set => SetField(ref peakPercent, Math.Clamp(value, 0, 100));
    }

    public void ReportError(string message) => ErrorText = message;

    public async Task InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        previousLocalSourceVolumePercent = network.LocalSourceVolumePercent;
        network.AudioSettingsChanged += OnAudioSettingsChanged;
        network.LocalSourceControlChanged += OnLocalSourceControlChanged;
        sessionManager.SessionsChanged += OnSessionsChanged;
        sessionManager.MonitoringFailed += OnMonitoringFailed;
        await RefreshAsync();
        await sessionManager.StartMonitoringAsync(CancellationToken.None);
    }

    public IReadOnlyList<ChannelSettings> CaptureChannels() => Sessions
        .GroupBy(session => session.RoutingChannelId)
        .Select(group =>
        {
            AudioSessionItemViewModel session = group.First();
            return new ChannelSettings(
                group.Key,
                session.DisplayName,
                session.VolumePercent / 100,
                session.IsMuted);
        })
        .ToArray();

    public void RefreshRoutes()
    {
        foreach (AudioSessionItemViewModel session in Sessions)
        {
            IReadOnlyList<ControllerNetworkViewModel.ApplicationOutputRouteInfo> routes =
                network.GetApplicationOutputRoutes(session.RoutingChannelId);
            HashSet<string> routedDeviceIds = routes
                .Select(route => route.DeviceId)
                .ToHashSet(StringComparer.Ordinal);
            session.ReplaceAdditionalOutputRoutes(
                routes,
                network.AdditionalOutputs
                    .Where(device => !routedDeviceIds.Contains(device.DeviceId))
                    .Select(device => new AvailableApplicationOutputDeviceItemViewModel(
                        device.DeviceId,
                        device.DisplayName))
                    .ToArray(),
                (channelId, deviceId) =>
                    network.RemoveSecondaryOutputRouteAsync(channelId, deviceId));
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            await network.RefreshAsync();
            IReadOnlyList<WindowsAudioSession> sessions =
                await sessionManager.GetSessionsAsync(CancellationToken.None);
            ApplySessions(sessions);
        }
        catch (Exception exception)
        {
            ErrorText = $"读取应用音频会话失败：{exception.Message}";
            StatusText = "无法读取默认输出设备的应用会话";
            Log.Error(exception, "Failed to enumerate Windows audio sessions");
        }
    }

    private void OnAudioSettingsChanged(object? sender, EventArgs args)
    {
        RefreshRoutes();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionsChanged(object? sender, AudioSessionsChangedEventArgs args) =>
        _ = Application.Current.Dispatcher.BeginInvoke(() => ApplySessions(args.Sessions));

    private void OnLocalSourceControlChanged(object? sender, EventArgs args)
    {
        float nextVolumePercent = network.LocalSourceVolumePercent;
        float previousVolumePercent = previousLocalSourceVolumePercent;
        previousLocalSourceVolumePercent = nextVolumePercent;
        applyingLocalSourceControl = true;
        try
        {
            foreach (AudioSessionItemViewModel session in Sessions)
            {
                float restoreVolume = localApplicationBaseVolumes.GetValueOrDefault(
                    session.ApplicationIdentityKey,
                    session.VolumePercent);
                session.VolumePercent = ScaleVolumeProportionally(
                    session.VolumePercent,
                    previousVolumePercent,
                    nextVolumePercent,
                    restoreVolume,
                    out float applicationBaseVolume);
                localApplicationBaseVolumes[session.ApplicationIdentityKey] =
                    applicationBaseVolume;

                bool baseMuted = session.SessionIds
                    .Select(sessionId =>
                    {
                        if (localSessionBaseMutes.TryGetValue(sessionId, out bool value))
                        {
                            return value;
                        }

                        bool fallback = session.IsMuted && !network.IsLocalSourceMuted;
                        localSessionBaseMutes[sessionId] = fallback;
                        return fallback;
                    })
                    .DefaultIfEmpty(session.IsMuted)
                    .All(value => value);
                session.IsMuted = baseMuted || network.IsLocalSourceMuted;
            }
        }
        finally
        {
            applyingLocalSourceControl = false;
        }
    }

    internal static float ScaleVolumeProportionally(
        float currentVolumePercent,
        float previousMasterPercent,
        float nextMasterPercent,
        float restoreVolumePercent,
        out float applicationBaseVolume)
    {
        float previous = Math.Clamp(previousMasterPercent, 0, 100);
        float next = Math.Clamp(nextMasterPercent, 0, 100);
        applicationBaseVolume = previous > 0.001f
            ? Math.Clamp(currentVolumePercent * 100 / previous, 0, 100)
            : Math.Clamp(restoreVolumePercent, 0, 100);
        return Math.Clamp(applicationBaseVolume * next / 100, 0, 100);
    }

    private void OnMonitoringFailed(object? sender, AudioSessionMonitoringFailedEventArgs args) =>
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ErrorText = $"会话监控暂时中断，将自动重试：{args.Exception.Message}";
            Log.Warning(args.Exception, "Windows audio-session monitoring failed");
        });

    private void ApplySessions(IReadOnlyList<WindowsAudioSession> snapshots)
    {
        WindowsAudioSession[] visibleSnapshots = snapshots
            .Where(snapshot =>
                snapshot.ProcessId > 0 &&
                snapshot.ProcessId != controllerProcessId)
            .ToArray();
        PeakPercent = visibleSnapshots.Length == 0
            ? 0
            : visibleSnapshots.Max(snapshot => snapshot.Peak) * 100;
        HashSet<string> liveIds = visibleSnapshots
            .Select(snapshot => snapshot.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string staleId in localSessionBaseVolumes.Keys
                     .Where(sessionId => !liveIds.Contains(sessionId))
                     .ToArray())
        {
            localSessionBaseVolumes.Remove(staleId);
            localSessionBaseMutes.Remove(staleId);
            CancelPendingVolume(staleId);
        }

        IGrouping<string, WindowsAudioSession>[] applicationGroups = visibleSnapshots
            .GroupBy(
                AudioSessionItemViewModel.CreateApplicationIdentityKey,
                StringComparer.Ordinal)
            .ToArray();
        HashSet<string> liveApplicationKeys = applicationGroups
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = Sessions.Count - 1; index >= 0; index--)
        {
            if (liveApplicationKeys.Contains(Sessions[index].ApplicationIdentityKey))
            {
                continue;
            }

            Guid routingChannelId = Sessions[index].RoutingChannelId;
            localApplicationBaseVolumes.Remove(Sessions[index].ApplicationIdentityKey);
            foreach (string sessionId in Sessions[index].SessionIds)
            {
                CancelPendingVolume(sessionId);
                localSessionBaseVolumes.Remove(sessionId);
                localSessionBaseMutes.Remove(sessionId);
            }
            Sessions.RemoveAt(index);
            _ = network.UnregisterLocalApplicationSourceAsync(routingChannelId);
        }

        foreach (IGrouping<string, WindowsAudioSession> group in applicationGroups)
        {
            WindowsAudioSession[] applicationSessions = group.ToArray();
            WindowsAudioSession representative = applicationSessions
                .OrderByDescending(snapshot => snapshot.IsActive)
                .ThenByDescending(snapshot => snapshot.Peak)
                .First();
            foreach (WindowsAudioSession snapshot in applicationSessions)
            {
                localSessionBaseVolumes.TryAdd(snapshot.SessionId, snapshot.Volume * 100);
                localSessionBaseMutes.TryAdd(snapshot.SessionId, snapshot.IsMuted);
            }

            AudioSessionItemViewModel? existing = Sessions.FirstOrDefault(item =>
                string.Equals(
                    item.ApplicationIdentityKey,
                    group.Key,
                    StringComparison.Ordinal));
            if (existing is null)
            {
                var item = new AudioSessionItemViewModel(
                    applicationSessions,
                    QueueVolumeChange,
                    SetMute);
                Sessions.Add(item);
                localApplicationBaseVolumes[item.ApplicationIdentityKey] =
                    representative.Volume * 100;
                network.RegisterLocalApplicationSource(
                    item.RoutingChannelId,
                    item.ProcessId,
                    item.DisplayName,
                    item.ApplicationIdentityKey);
                float gain = network.LocalSourceVolumePercent / 100;
                if (Math.Abs(gain - 1f) > 0.001f || network.IsLocalSourceMuted)
                {
                    applyingLocalSourceControl = true;
                    try
                    {
                        item.VolumePercent = representative.Volume * 100 * gain;
                        item.IsMuted = applicationSessions.All(snapshot => snapshot.IsMuted) ||
                            network.IsLocalSourceMuted;
                    }
                    finally
                    {
                        applyingLocalSourceControl = false;
                    }
                }
            }
            else
            {
                existing.Update(applicationSessions);
                network.RegisterLocalApplicationSource(
                    existing.RoutingChannelId,
                    existing.ProcessId,
                    existing.DisplayName,
                    existing.ApplicationIdentityKey);
            }
        }

        RefreshRoutes();
        int activeCount = Sessions.Count(session => session.IsActive);
        StatusText = $"本机 {Sessions.Count} 个应用 · {activeCount} 个正在发声";
        if (!ErrorText.StartsWith("设置文件", StringComparison.Ordinal))
        {
            ErrorText = string.Empty;
        }
    }

    private void QueueVolumeChange(string sessionId, float volume)
    {
        if (!applyingLocalSourceControl)
        {
            float gain = network.LocalSourceVolumePercent / 100;
            localSessionBaseVolumes[sessionId] = gain > 0.001f
                ? Math.Clamp(volume * 100 / gain, 0, 100)
                : volume * 100;
        }

        CancelPendingVolume(sessionId);
        var cancellation = new CancellationTokenSource();
        volumeDebounce[sessionId] = cancellation;
        _ = ApplyVolumeAfterDelayAsync(sessionId, volume, cancellation);
    }

    private async Task ApplyVolumeAfterDelayAsync(
        string sessionId,
        float volume,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(120, cancellation.Token);
            await sessionManager.SetVolumeAsync(sessionId, volume, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                ErrorText = $"设置应用音量失败：{exception.Message}");
        }
        finally
        {
            if (volumeDebounce.TryGetValue(sessionId, out CancellationTokenSource? current) &&
                ReferenceEquals(current, cancellation))
            {
                volumeDebounce.Remove(sessionId);
            }
            cancellation.Dispose();
        }
    }

    private void SetMute(string sessionId, bool isMuted)
    {
        if (!applyingLocalSourceControl)
        {
            localSessionBaseMutes[sessionId] = isMuted;
        }
        _ = SetMuteAsync(sessionId, isMuted);
    }

    private async Task SetMuteAsync(string sessionId, bool isMuted)
    {
        try
        {
            await sessionManager.SetMuteAsync(sessionId, isMuted, CancellationToken.None);
        }
        catch (Exception exception)
        {
            ErrorText = $"设置应用静音失败：{exception.Message}";
        }
    }

    private void CancelPendingVolume(string sessionId)
    {
        if (!volumeDebounce.Remove(sessionId, out CancellationTokenSource? cancellation))
        {
            return;
        }
        cancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        sessionManager.SessionsChanged -= OnSessionsChanged;
        sessionManager.MonitoringFailed -= OnMonitoringFailed;
        network.AudioSettingsChanged -= OnAudioSettingsChanged;
        network.LocalSourceControlChanged -= OnLocalSourceControlChanged;
        foreach (CancellationTokenSource cancellation in volumeDebounce.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        volumeDebounce.Clear();
        await sessionManager.StopMonitoringAsync(CancellationToken.None);
        await sessionManager.DisposeAsync();
    }
}