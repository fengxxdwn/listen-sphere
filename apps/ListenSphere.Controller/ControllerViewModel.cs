using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using ListenSphere.Configuration;
using ListenSphere.Diagnostics;
using ListenSphere.Windows.AudioSessions;
using Serilog;

namespace ListenSphere.Controller;

public sealed class ControllerViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IWindowsAudioSessionManager sessionManager;
    private readonly ISettingsStore settingsStore;
    private readonly IDiagnosticsExporter diagnosticsExporter;
    private readonly IDiagnosticEventSink diagnosticEvents;
    private readonly Dictionary<string, CancellationTokenSource> volumeDebounce =
        new(StringComparer.Ordinal);
    private CancellationTokenSource? settingsDebounce;
    private ListenSphereSettings settings = new();
    private SceneSettings? selectedScene;
    private string newSceneName = string.Empty;
    private string statusText = "正在读取 Windows 应用音频会话…";
    private string errorText = string.Empty;
    private string sceneStatusText = "保存当前声道、输出设备和主音量，随时一键恢复。";
    private string diagnosticStatusText = "诊断包不包含音频、会话密钥、证书或可信设备记录。";
    private bool isFirstRunGuideVisible;
    private bool initialized;
    private bool disposed;

    public ControllerViewModel(
        IWindowsAudioSessionManager sessionManager,
        ControllerNetworkViewModel network,
        ISettingsStore settingsStore,
        IDiagnosticsExporter diagnosticsExporter,
        IDiagnosticEventSink diagnosticEvents)
    {
        this.sessionManager = sessionManager;
        this.settingsStore = settingsStore;
        this.diagnosticsExporter = diagnosticsExporter;
        this.diagnosticEvents = diagnosticEvents;
        Network = network;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SaveSceneCommand = new AsyncRelayCommand(SaveSceneAsync);
        ApplySceneCommand = new AsyncRelayCommand(
            ApplySelectedSceneAsync,
            () => SelectedScene is not null);
        DeleteSceneCommand = new AsyncRelayCommand(
            DeleteSelectedSceneAsync,
            () => SelectedScene is not null);
        FinishGuideCommand = new AsyncRelayCommand(FinishGuideAsync);
        ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AudioSessionItemViewModel> Sessions { get; } = [];
    public ObservableCollection<SceneSettings> Scenes { get; } = [];
    public ControllerNetworkViewModel Network { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SaveSceneCommand { get; }
    public AsyncRelayCommand ApplySceneCommand { get; }
    public AsyncRelayCommand DeleteSceneCommand { get; }
    public AsyncRelayCommand FinishGuideCommand { get; }
    public AsyncRelayCommand ExportDiagnosticsCommand { get; }

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

    public string SceneStatusText
    {
        get => sceneStatusText;
        private set => SetField(ref sceneStatusText, value);
    }

    public string DiagnosticStatusText
    {
        get => diagnosticStatusText;
        private set => SetField(ref diagnosticStatusText, value);
    }

    public string NewSceneName
    {
        get => newSceneName;
        set => SetField(ref newSceneName, value);
    }

    public SceneSettings? SelectedScene
    {
        get => selectedScene;
        set
        {
            if (SetField(ref selectedScene, value))
            {
                ApplySceneCommand.RaiseCanExecuteChanged();
                DeleteSceneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsFirstRunGuideVisible
    {
        get => isFirstRunGuideVisible;
        private set => SetField(ref isFirstRunGuideVisible, value);
    }

    public async Task InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        try
        {
            settings = await settingsStore.LoadAsync(CancellationToken.None);
            if (settingsStore is JsonSettingsStore { LastRecoveryPath: { } recoveryPath })
            {
                ErrorText =
                    $"检测到损坏的设置文件，已恢复默认设置。原文件已备份为：{Path.GetFileName(recoveryPath)}";
                diagnosticEvents.Record(
                    DiagnosticSeverity.Warning,
                    "configuration.recovered",
                    new Dictionary<string, object?> { ["backupCreated"] = true });
            }
        }
        catch (Exception exception)
        {
            settings = new ListenSphereSettings();
            ErrorText = $"设置文件无法读取，已使用默认设置：{exception.Message}";
            Log.Warning(exception, "Failed to load controller settings; defaults are in use");
        }

        foreach (SceneSettings scene in settings.Scenes.OrderBy(scene => scene.Name))
        {
            Scenes.Add(scene);
        }

        IsFirstRunGuideVisible = !settings.FirstRunCompleted;
        Network.AudioSettingsChanged += OnAudioSettingsChanged;
        await Network.InitializeAsync(
            settings.PlaybackDeviceId,
            settings.MasterVolume,
            settings.FollowSystemDefaultPlayback);
        sessionManager.SessionsChanged += OnSessionsChanged;
        sessionManager.MonitoringFailed += OnMonitoringFailed;
        await RefreshAsync();
        await sessionManager.StartMonitoringAsync(CancellationToken.None);
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
        Network.AudioSettingsChanged -= OnAudioSettingsChanged;
        foreach (CancellationTokenSource cancellation in volumeDebounce.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        volumeDebounce.Clear();
        settingsDebounce?.Cancel();
        settingsDebounce?.Dispose();
        try
        {
            await PersistSettingsAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to persist settings during shutdown");
        }

        await Network.DisposeAsync();
        await sessionManager.StopMonitoringAsync(CancellationToken.None);
        await sessionManager.DisposeAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
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

    private async Task SaveSceneAsync()
    {
        string name = string.IsNullOrWhiteSpace(NewSceneName)
            ? $"场景 {DateTime.Now:MM-dd HH:mm}"
            : NewSceneName.Trim();
        SceneSettings? existing = Scenes.FirstOrDefault(
            scene => string.Equals(scene.Name, name, StringComparison.CurrentCultureIgnoreCase));
        var scene = new SceneSettings(
            existing?.SceneId ?? Guid.NewGuid(),
            name,
            CaptureChannels(),
            Network.SelectedPlaybackDevice?.Id,
            Network.MasterVolumePercent / 100,
            Network.FollowSystemDefaultPlayback);
        if (existing is not null)
        {
            int index = Scenes.IndexOf(existing);
            Scenes[index] = scene;
        }
        else
        {
            Scenes.Add(scene);
        }

        SelectedScene = scene;
        NewSceneName = string.Empty;
        if (await TryPersistSettingsAsync())
        {
            SceneStatusText = $"已保存场景“{scene.Name}”，共 {scene.Channels.Count} 个声道。";
        }
    }

    private async Task ApplySelectedSceneAsync()
    {
        if (SelectedScene is not { } scene)
        {
            return;
        }

        foreach (AudioSessionItemViewModel session in Sessions)
        {
            Guid channelId = CreateLocalChannelId(session.DisplayName);
            ChannelSettings? saved = scene.Channels.FirstOrDefault(
                channel => channel.ChannelId == channelId);
            if (saved is not null)
            {
                session.VolumePercent = saved.Volume * 100;
                session.IsMuted = saved.IsMuted;
            }
        }

        await Network.ApplyAudioSettingsAsync(
            scene.PlaybackDeviceId,
            scene.MasterVolume,
            scene.FollowSystemDefaultPlayback,
            scene.Channels);
        if (await TryPersistSettingsAsync())
        {
            SceneStatusText = $"已恢复场景“{scene.Name}”。未运行的应用将在下次保存时更新。";
        }
    }

    private async Task DeleteSelectedSceneAsync()
    {
        if (SelectedScene is not { } scene)
        {
            return;
        }

        Scenes.Remove(scene);
        SelectedScene = null;
        if (await TryPersistSettingsAsync())
        {
            SceneStatusText = $"已删除场景“{scene.Name}”。";
        }
    }

    private async Task FinishGuideAsync()
    {
        IsFirstRunGuideVisible = false;
        settings = settings with { FirstRunCompleted = true };
        await TryPersistSettingsAsync();
    }

    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            string destination = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ListenSphere Diagnostics");
            diagnosticEvents.Record(
                DiagnosticSeverity.Information,
                "diagnostics.export.requested");
            string archive = await diagnosticsExporter.ExportAsync(
                destination,
                CancellationToken.None);
            DiagnosticStatusText = $"诊断包已导出：{archive}";
            Process.Start(new ProcessStartInfo
            {
                FileName = destination,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            DiagnosticStatusText = $"导出诊断失败：{exception.Message}";
            Log.Error(exception, "Failed to export diagnostics");
        }
    }

    private IReadOnlyList<ChannelSettings> CaptureChannels()
    {
        IEnumerable<ChannelSettings> local = Sessions
            .GroupBy(session => CreateLocalChannelId(session.DisplayName))
            .Select(group =>
            {
                AudioSessionItemViewModel session = group.First();
                return new ChannelSettings(
                    group.Key,
                    session.DisplayName,
                    session.VolumePercent / 100,
                    session.IsMuted);
            });
        IEnumerable<ChannelSettings> remote = Network.RemoteChannels.Select(channel =>
            new ChannelSettings(
                channel.ChannelId,
                channel.DisplayName,
                channel.VolumePercent / 100,
                channel.IsMuted));
        return local.Concat(remote).ToArray();
    }

    private static Guid CreateLocalChannelId(string displayName)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"ListenSphere/local/{displayName.Trim().ToUpperInvariant()}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private void OnAudioSettingsChanged(object? sender, EventArgs args) => QueueSettingsSave();

    private void QueueSettingsSave()
    {
        settingsDebounce?.Cancel();
        settingsDebounce?.Dispose();
        settingsDebounce = new CancellationTokenSource();
        _ = PersistSettingsAfterDelayAsync(settingsDebounce);
    }

    private async Task PersistSettingsAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(300, cancellation.Token);
            await PersistSettingsAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                ErrorText = $"保存设置失败：{exception.Message}");
            Log.Warning(exception, "Failed to persist controller settings");
        }
    }

    private async Task PersistSettingsAsync(CancellationToken cancellationToken)
    {
        settings = settings with
        {
            Version = ListenSphereSettings.CurrentVersion,
            PlaybackDeviceId = Network.SelectedPlaybackDevice?.Id,
            FollowSystemDefaultPlayback = Network.FollowSystemDefaultPlayback,
            MasterVolume = Network.MasterVolumePercent / 100,
            FirstRunCompleted = !IsFirstRunGuideVisible,
            Scenes = Scenes.ToArray(),
            PairedDeviceIds = Network.TrustedDevices.Select(device => device.DeviceId).ToArray()
        };
        await settingsStore.SaveAsync(settings, cancellationToken);
    }

    private async Task<bool> TryPersistSettingsAsync()
    {
        try
        {
            await PersistSettingsAsync(CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            ErrorText = $"保存设置失败：{exception.Message}";
            Log.Warning(exception, "Failed to persist controller settings");
            return false;
        }
    }

    private void OnSessionsChanged(object? sender, AudioSessionsChangedEventArgs args)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(() => ApplySessions(args.Sessions));
    }

    private void OnMonitoringFailed(object? sender, AudioSessionMonitoringFailedEventArgs args)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ErrorText = $"会话监控暂时中断，将自动重试：{args.Exception.Message}";
            Log.Warning(args.Exception, "Windows audio-session monitoring failed");
        });
    }

    private void ApplySessions(IReadOnlyList<WindowsAudioSession> snapshots)
    {
        HashSet<string> liveIds = snapshots.Select(snapshot => snapshot.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = Sessions.Count - 1; index >= 0; index--)
        {
            if (liveIds.Contains(Sessions[index].SessionId))
            {
                continue;
            }

            CancelPendingVolume(Sessions[index].SessionId);
            Sessions.RemoveAt(index);
        }

        foreach (WindowsAudioSession snapshot in snapshots)
        {
            AudioSessionItemViewModel? existing = Sessions.FirstOrDefault(
                item => string.Equals(item.SessionId, snapshot.SessionId, StringComparison.Ordinal));
            if (existing is null)
            {
                Sessions.Add(new AudioSessionItemViewModel(
                    snapshot,
                    QueueVolumeChange,
                    SetMute));
            }
            else
            {
                existing.Update(snapshot);
            }
        }

        int activeCount = Sessions.Count(session => session.IsActive);
        StatusText = $"本机 {Sessions.Count} 个应用声道 · {activeCount} 个正在发声";
        if (!ErrorText.StartsWith("设置文件", StringComparison.Ordinal))
        {
            ErrorText = string.Empty;
        }
    }

    private void QueueVolumeChange(string sessionId, float volume)
    {
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

    private void SetMute(string sessionId, bool isMuted) => _ = SetMuteAsync(sessionId, isMuted);

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

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
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

public sealed class AudioSessionItemViewModel : INotifyPropertyChanged
{
    private readonly Action<string, float> volumeChanged;
    private readonly Action<string, bool> muteChanged;
    private bool applyingSnapshot;
    private string displayName;
    private int processId;
    private float volumePercent;
    private bool isMuted;
    private bool isActive;
    private float peakPercent;
    private ImageSource? icon;

    public AudioSessionItemViewModel(
        WindowsAudioSession snapshot,
        Action<string, float> volumeChanged,
        Action<string, bool> muteChanged)
    {
        SessionId = snapshot.SessionId;
        this.volumeChanged = volumeChanged;
        this.muteChanged = muteChanged;
        displayName = snapshot.DisplayName;
        Update(snapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string SessionId { get; }

    public string DisplayName
    {
        get => displayName;
        private set => SetField(ref displayName, value);
    }

    public int ProcessId
    {
        get => processId;
        private set
        {
            if (SetField(ref processId, value))
            {
                OnPropertyChanged(nameof(ProcessText));
            }
        }
    }

    public string ProcessText => ProcessId > 0 ? $"PID {ProcessId}" : "Windows";

    public float VolumePercent
    {
        get => volumePercent;
        set
        {
            float normalized = Math.Clamp(value, 0, 100);
            if (SetField(ref volumePercent, normalized) && !applyingSnapshot)
            {
                volumeChanged(SessionId, normalized / 100);
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
                if (!applyingSnapshot)
                {
                    muteChanged(SessionId, value);
                }
            }
        }
    }

    public string MuteButtonText => IsMuted ? "取消静音" : "静音";

    public bool IsActive
    {
        get => isActive;
        private set
        {
            if (SetField(ref isActive, value))
            {
                OnPropertyChanged(nameof(ActivityText));
                OnPropertyChanged(nameof(ActivityBrush));
            }
        }
    }

    public string ActivityText => IsActive ? "正在发声" : "静音";
    public Brush ActivityBrush => IsActive ? Brushes.SeaGreen : Brushes.Gray;

    public float PeakPercent
    {
        get => peakPercent;
        private set => SetField(ref peakPercent, value);
    }

    public ImageSource? Icon
    {
        get => icon;
        private set
        {
            if (SetField(ref icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => Icon is not null;

    public void Update(WindowsAudioSession snapshot)
    {
        applyingSnapshot = true;
        try
        {
            DisplayName = snapshot.DisplayName;
            ProcessId = snapshot.ProcessId;
            VolumePercent = snapshot.Volume * 100;
            IsMuted = snapshot.IsMuted;
            IsActive = snapshot.IsActive;
            PeakPercent = snapshot.Peak * 100;
            Icon = ApplicationIconLoader.Load(snapshot.ProcessPath, snapshot.IconPath);
        }
        finally
        {
            applyingSnapshot = false;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
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
