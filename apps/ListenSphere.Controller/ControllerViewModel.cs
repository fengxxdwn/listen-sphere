using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using ListenSphere.Configuration;
using ListenSphere.Diagnostics;
using ListenSphere.Windows.AudioSessions;
using Microsoft.Win32;
using Serilog;

namespace ListenSphere.Controller;

public sealed class ControllerViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly JsonSerializerOptions PresetJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly IWindowsAudioSessionManager sessionManager;
    private readonly ISettingsStore settingsStore;
    private readonly IDiagnosticsExporter diagnosticsExporter;
    private readonly IDiagnosticEventSink diagnosticEvents;
    private readonly int controllerProcessId = Environment.ProcessId;
    private readonly Dictionary<string, CancellationTokenSource> volumeDebounce =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> localSessionBaseVolumes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> localSessionBaseMutes =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim settingsPersistenceGate = new(1, 1);
    private CancellationTokenSource? settingsDebounce;
    private long settingsPersistenceRequest;
    private ListenSphereSettings settings = new();
    private SceneSettings? selectedScene;
    private string newSceneName = string.Empty;
    private string statusText = "正在读取 Windows 应用音频会话…";
    private string errorText = string.Empty;
    private string sceneStatusText = "保存当前声道、输出设备和主音量，随时一键恢复。";
    private string diagnosticStatusText = "诊断包不包含音频、会话密钥、证书或可信设备记录。";
    private string senderStatusText = "发送端未启动";
    private float localPeakPercent;
    private bool isFirstRunGuideVisible;
    private bool isSystemSoundEditorOpen;
    private bool applyingLocalSourceControl;
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
        Network.LocalSourceControlChanged += OnLocalSourceControlChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SaveSceneCommand = new AsyncRelayCommand(SaveSceneAsync);
        ApplySceneCommand = new AsyncRelayCommand(
            ApplySelectedSceneAsync,
            () => SelectedScene is not null);
        DeleteSceneCommand = new AsyncRelayCommand(
            DeleteSelectedSceneAsync,
            () => SelectedScene is not null);
        RenameSceneCommand = new AsyncRelayCommand(
            RenameSelectedSceneAsync,
            () => SelectedScene is not null && !string.IsNullOrWhiteSpace(NewSceneName));
        DuplicateSceneCommand = new AsyncRelayCommand(
            DuplicateSelectedSceneAsync,
            () => SelectedScene is not null);
        ImportSceneCommand = new AsyncRelayCommand(ImportSceneAsync);
        ExportSceneCommand = new AsyncRelayCommand(
            ExportSelectedSceneAsync,
            () => SelectedScene is not null);
        FinishGuideCommand = new AsyncRelayCommand(FinishGuideAsync);
        ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync);
        EnableSenderCommand = new AsyncRelayCommand(EnableSenderAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AudioSessionItemViewModel> Sessions { get; } = [];
    public ObservableCollection<SceneSettings> Scenes { get; } = [];
    public ControllerNetworkViewModel Network { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SaveSceneCommand { get; }
    public AsyncRelayCommand ApplySceneCommand { get; }
    public AsyncRelayCommand DeleteSceneCommand { get; }
    public AsyncRelayCommand RenameSceneCommand { get; }
    public AsyncRelayCommand DuplicateSceneCommand { get; }
    public AsyncRelayCommand ImportSceneCommand { get; }
    public AsyncRelayCommand ExportSceneCommand { get; }
    public AsyncRelayCommand FinishGuideCommand { get; }
    public AsyncRelayCommand ExportDiagnosticsCommand { get; }
    public AsyncRelayCommand EnableSenderCommand { get; }

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

    public void ReportError(string message) => ErrorText = message;

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

    public string SenderStatusText
    {
        get => senderStatusText;
        private set => SetField(ref senderStatusText, value);
    }

    public float LocalPeakPercent
    {
        get => localPeakPercent;
        private set => SetField(ref localPeakPercent, Math.Clamp(value, 0, 100));
    }

    public string NewSceneName
    {
        get => newSceneName;
        set
        {
            if (SetField(ref newSceneName, value))
            {
                RenameSceneCommand.RaiseCanExecuteChanged();
            }
        }
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
                RenameSceneCommand.RaiseCanExecuteChanged();
                DuplicateSceneCommand.RaiseCanExecuteChanged();
                ExportSceneCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsFirstRunGuideVisible
    {
        get => isFirstRunGuideVisible;
        private set => SetField(ref isFirstRunGuideVisible, value);
    }

    public bool IsSystemSoundEditorOpen
    {
        get => isSystemSoundEditorOpen;
        set => SetField(ref isSystemSoundEditorOpen, value);
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
            settings.FollowSystemDefaultPlayback,
            settings.AutomaticRoutingEnabled,
            settings.AudioRoutingRules,
            settings.ChannelLayouts,
            settings.AudioOutputRoutes,
            settings.MicrophoneOutputDeviceId,
            settings.MicrophoneOutputVolume,
            settings.MicrophoneOutputMuted,
            settings.MicrophoneMonitoringEnabled,
            settings.LocalSourceVolume,
            settings.LocalSourceMuted,
            settings.MicrophoneMonitoringDeviceId,
            settings.MicrophoneOutputEnabled,
            settings.ComputerMicrophoneDeviceId);
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
        Network.LocalSourceControlChanged -= OnLocalSourceControlChanged;
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
            await Network.RefreshAsync();
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
            Network.FollowSystemDefaultPlayback,
            Network.CaptureGroupBusSettings());
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
            Guid channelId = session.RoutingChannelId;
            ChannelSettings? saved = scene.Channels.FirstOrDefault(
                channel => channel.ChannelId == channelId) ??
                scene.Channels.FirstOrDefault(channel =>
                    channel.ChannelId == CreateLocalChannelId(session.DisplayName));
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
            scene.Channels,
            scene.GroupBuses ?? []);
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

    private async Task RenameSelectedSceneAsync()
    {
        if (SelectedScene is not { } scene || string.IsNullOrWhiteSpace(NewSceneName))
        {
            return;
        }
        string name = NewSceneName.Trim();
        if (Scenes.Any(item => item.SceneId != scene.SceneId &&
            string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            SceneStatusText = $"已有名为“{name}”的预设。";
            return;
        }
        int index = Scenes.IndexOf(scene);
        SceneSettings renamed = scene with { Name = name };
        Scenes[index] = renamed;
        SelectedScene = renamed;
        NewSceneName = string.Empty;
        if (await TryPersistSettingsAsync())
        {
            SceneStatusText = $"预设已重命名为“{name}”。";
        }
    }

    private async Task DuplicateSelectedSceneAsync()
    {
        if (SelectedScene is not { } scene)
        {
            return;
        }
        string baseName = string.IsNullOrWhiteSpace(NewSceneName)
            ? $"{scene.Name} 副本"
            : NewSceneName.Trim();
        string name = CreateUniqueSceneName(baseName);
        SceneSettings copy = scene with { SceneId = Guid.NewGuid(), Name = name };
        Scenes.Add(copy);
        SelectedScene = copy;
        NewSceneName = string.Empty;
        if (await TryPersistSettingsAsync())
        {
            SceneStatusText = $"已复制预设“{scene.Name}”为“{name}”。";
        }
    }

    private async Task ImportSceneAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入聆界调音预设",
            Filter = "聆界预设 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            await using FileStream stream = File.OpenRead(dialog.FileName);
            SceneSettings imported = await JsonSerializer.DeserializeAsync<SceneSettings>(
                stream,
                PresetJsonOptions,
                CancellationToken.None) ?? throw new InvalidDataException("预设内容为空。");
            SceneSettings normalized = NormalizeImportedScene(imported);
            Scenes.Add(normalized);
            SelectedScene = normalized;
            if (await TryPersistSettingsAsync())
            {
                SceneStatusText = $"已导入预设“{normalized.Name}”。";
            }
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException or NotSupportedException)
        {
            SceneStatusText = $"导入预设失败：{exception.Message}";
        }
    }

    private async Task ExportSelectedSceneAsync()
    {
        if (SelectedScene is not { } scene)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "导出聆界调音预设",
            Filter = "聆界预设 (*.json)|*.json",
            FileName = $"{SanitizeFileName(scene.Name)}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            await using FileStream stream = new(
                dialog.FileName,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous);
            await JsonSerializer.SerializeAsync(
                stream,
                scene,
                PresetJsonOptions,
                CancellationToken.None);
            SceneStatusText = $"已导出预设“{scene.Name}”。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SceneStatusText = $"导出预设失败：{exception.Message}";
        }
    }

    private SceneSettings NormalizeImportedScene(SceneSettings scene)
    {
        if (string.IsNullOrWhiteSpace(scene.Name))
        {
            throw new InvalidDataException("预设缺少名称。");
        }
        ChannelSettings[] channels = (scene.Channels ?? [])
            .Where(channel => channel.ChannelId != Guid.Empty)
            .Take(256)
            .Select(channel => channel with
            {
                DisplayName = string.IsNullOrWhiteSpace(channel.DisplayName)
                    ? "未命名声道"
                    : channel.DisplayName.Trim(),
                Volume = Math.Clamp(channel.Volume, 0f, 1f),
                PreampDb = Math.Clamp(channel.PreampDb, -24f, 12f),
                NoiseGateThresholdDb = Math.Clamp(channel.NoiseGateThresholdDb, -80f, -10f),
                CompressorThresholdDb = Math.Clamp(channel.CompressorThresholdDb, -40f, 0f),
                CompressorRatio = Math.Clamp(channel.CompressorRatio, 1f, 20f),
                LimiterCeilingDb = Math.Clamp(channel.LimiterCeilingDb, -12f, -0.1f),
                VoiceDuckingReductionDb = Math.Clamp(channel.VoiceDuckingReductionDb, 0f, 30f),
                EqualizerGains = channel.EqualizerGains is { Length: 10 }
                    ? channel.EqualizerGains.Select(value => Math.Clamp(value, -20f, 20f)).ToArray()
                    : null
            })
            .ToArray();
        return scene with
        {
            SceneId = Guid.NewGuid(),
            Name = CreateUniqueSceneName(scene.Name.Trim()),
            Channels = channels,
            MasterVolume = Math.Clamp(scene.MasterVolume, 0f, 1f)
        };
    }

    private string CreateUniqueSceneName(string baseName)
    {
        string candidate = baseName;
        for (var suffix = 2; Scenes.Any(scene =>
                 string.Equals(scene.Name, candidate, StringComparison.CurrentCultureIgnoreCase)); suffix++)
        {
            candidate = $"{baseName} {suffix}";
        }
        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "ListenSphere-Preset" : sanitized;
    }

    private async Task FinishGuideAsync()
    {
        IsFirstRunGuideVisible = false;
        settings = settings with { FirstRunCompleted = true };
        await TryPersistSettingsAsync();
    }

    private Task EnableSenderAsync()
    {
        try
        {
            Process? running = Process.GetProcessesByName("ListenSphere.Sender")
                .FirstOrDefault();
            if (running is not null)
            {
                if (running.MainWindowHandle != IntPtr.Zero)
                {
                    NativeWindowActivation.ShowWindow(running.MainWindowHandle, 9);
                    NativeWindowActivation.SetForegroundWindow(running.MainWindowHandle);
                }

                Network.GenerateCodeCommand.Execute(null);
                SenderStatusText = "已唤醒 Sender，并生成一次性配对码";
                return Task.CompletedTask;
            }

            string? senderPath = FindSenderExecutable();
            if (senderPath is null)
            {
                throw new FileNotFoundException(
                    "未找到 ListenSphere Sender。请将 Sender 安装在 Controller 同目录或相邻 Sender 目录。");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = senderPath,
                WorkingDirectory = Path.GetDirectoryName(senderPath)!,
                UseShellExecute = true
            });
            Network.GenerateCodeCommand.Execute(null);
            SenderStatusText = "已启动 Sender，并生成一次性配对码";
            ErrorText = string.Empty;
        }
        catch (Exception exception)
        {
            SenderStatusText = "Sender 启动失败";
            ErrorText = $"无法启用发送连接：{exception.Message}";
            Log.Error(exception, "Failed to launch ListenSphere Sender");
        }

        return Task.CompletedTask;
    }

    private static string? FindSenderExecutable()
    {
        string baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDirectory, "ListenSphere.Sender.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "Sender", "ListenSphere.Sender.exe"))
        };

        for (DirectoryInfo? directory = new(baseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            foreach (string configuration in new[] { "Release", "Debug" })
            {
                candidates.Add(Path.Combine(
                    directory.FullName,
                    "apps",
                    "ListenSphere.Sender",
                    "bin",
                    configuration,
                    "net10.0-windows",
                    "ListenSphere.Sender.exe"));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
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
            .GroupBy(session => session.RoutingChannelId)
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
                channel.IsMuted,
                channel.SelectedEqualizerPreset.Name,
                channel.EqualizerGains.ToArray(),
                channel.IsEqualizerEnabled,
                channel.SelectedChannelGroup,
                channel.PreampDb,
                channel.NoiseGateEnabled,
                channel.NoiseGateThresholdDb,
                channel.CompressorEnabled,
                channel.CompressorThresholdDb,
                channel.CompressorRatio,
                channel.LimiterEnabled,
                channel.LimiterCeilingDb,
                channel.IsVoiceDuckingTrigger,
                channel.IsVoiceDuckingTarget,
                channel.VoiceDuckingReductionDb));
        return local.Concat(remote).ToArray();
    }

    private static Guid CreateLocalChannelId(string displayName)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"ListenSphere/local/{displayName.Trim().ToUpperInvariant()}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private void OnAudioSettingsChanged(object? sender, EventArgs args)
    {
        RefreshLocalApplicationRoutes();
        QueueSettingsSave();
    }

    private void RefreshLocalApplicationRoutes()
    {
        foreach (AudioSessionItemViewModel session in Sessions)
        {
            IReadOnlyList<ControllerNetworkViewModel.ApplicationOutputRouteInfo> routes =
                Network.GetApplicationOutputRoutes(session.RoutingChannelId);
            HashSet<string> routedDeviceIds = routes
                .Select(route => route.DeviceId)
                .ToHashSet(StringComparer.Ordinal);
            session.ReplaceAdditionalOutputRoutes(
                routes,
                Network.AdditionalOutputs
                    .Where(device => !routedDeviceIds.Contains(device.DeviceId))
                    .Select(device => new AvailableApplicationOutputDeviceItemViewModel(
                        device.DeviceId,
                        device.DisplayName))
                    .ToArray(),
                (channelId, deviceId) =>
                    Network.RemoveSecondaryOutputRouteAsync(channelId, deviceId));
        }
    }

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
        long request = Interlocked.Increment(ref settingsPersistenceRequest);
        await settingsPersistenceGate.WaitAsync(cancellationToken);
        try
        {
            if (request != Volatile.Read(ref settingsPersistenceRequest))
            {
                return;
            }
            ListenSphereSettings next = settings with
            {
                Version = ListenSphereSettings.CurrentVersion,
                PlaybackDeviceId = Network.SelectedPlaybackDevice?.Id,
                FollowSystemDefaultPlayback = Network.FollowSystemDefaultPlayback,
                MasterVolume = Network.MasterVolumePercent / 100,
                FirstRunCompleted = !IsFirstRunGuideVisible,
                Scenes = Scenes.ToArray(),
                PairedDeviceIds = Network.TrustedDevices.Select(device => device.DeviceId).ToArray(),
                AutomaticRoutingEnabled = Network.AutomaticRoutingEnabled,
                AudioRoutingRules = Network.CaptureRoutingRules(),
                ChannelLayouts = Network.CaptureChannelLayouts(),
                AudioOutputRoutes = Network.CaptureOutputRoutes(),
                MicrophoneOutputDeviceId = Network.SelectedMicrophoneOutputDevice?.Id,
                MicrophoneOutputEnabled = Network.MicrophoneOutputEnabled,
                ComputerMicrophoneDeviceId = Network.SelectedComputerMicrophoneDevice?.Id,
                MicrophoneOutputVolume = Network.MicrophoneOutputVolumePercent / 100,
                MicrophoneOutputMuted = Network.MicrophoneOutputMuted,
                MicrophoneMonitoringEnabled = Network.MicrophoneMonitoringEnabled,
                MicrophoneMonitoringDeviceId = Network.SelectedMicrophoneMonitoringDevice?.Id,
                LocalSourceVolume = Network.LocalSourceVolumePercent / 100,
                LocalSourceMuted = Network.IsLocalSourceMuted
            };
            await settingsStore.SaveAsync(next, cancellationToken);
            if (request == Volatile.Read(ref settingsPersistenceRequest))
            {
                settings = next;
            }
        }
        finally
        {
            settingsPersistenceGate.Release();
        }
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

    private void OnLocalSourceControlChanged(object? sender, EventArgs args)
    {
        applyingLocalSourceControl = true;
        try
        {
            float gain = Network.LocalSourceVolumePercent / 100;
            foreach (AudioSessionItemViewModel session in Sessions)
            {
                float baseVolume = session.SessionIds
                    .Select(sessionId =>
                    {
                        if (localSessionBaseVolumes.TryGetValue(sessionId, out float value))
                        {
                            return value;
                        }

                        float fallback = gain > 0.001f
                            ? Math.Clamp(session.VolumePercent / gain, 0, 100)
                            : session.VolumePercent;
                        localSessionBaseVolumes[sessionId] = fallback;
                        return fallback;
                    })
                    .DefaultIfEmpty(session.VolumePercent)
                    .Average();
                bool baseMuted = session.SessionIds
                    .Select(sessionId =>
                    {
                        if (localSessionBaseMutes.TryGetValue(sessionId, out bool value))
                        {
                            return value;
                        }

                        bool fallback = session.IsMuted && !Network.IsLocalSourceMuted;
                        localSessionBaseMutes[sessionId] = fallback;
                        return fallback;
                    })
                    .DefaultIfEmpty(session.IsMuted)
                    .All(value => value);

                session.VolumePercent = baseVolume * gain;
                session.IsMuted = baseMuted || Network.IsLocalSourceMuted;
            }
        }
        finally
        {
            applyingLocalSourceControl = false;
        }
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
        WindowsAudioSession[] visibleSnapshots = snapshots
            .Where(snapshot =>
                snapshot.ProcessId > 0 &&
                snapshot.ProcessId != controllerProcessId)
            .ToArray();
        LocalPeakPercent = visibleSnapshots.Length == 0
            ? 0
            : visibleSnapshots.Max(snapshot => snapshot.Peak) * 100;
        HashSet<string> liveIds = visibleSnapshots.Select(snapshot => snapshot.SessionId)
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
            foreach (string sessionId in Sessions[index].SessionIds)
            {
                CancelPendingVolume(sessionId);
                localSessionBaseVolumes.Remove(sessionId);
                localSessionBaseMutes.Remove(sessionId);
            }
            Sessions.RemoveAt(index);
            _ = Network.UnregisterLocalApplicationSourceAsync(routingChannelId);
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

            AudioSessionItemViewModel? existing = Sessions.FirstOrDefault(
                item => string.Equals(
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
                Network.RegisterLocalApplicationSource(
                    item.RoutingChannelId,
                    item.ProcessId,
                    item.DisplayName,
                    item.ApplicationIdentityKey);
                float gain = Network.LocalSourceVolumePercent / 100;
                if (Math.Abs(gain - 1f) > 0.001f || Network.IsLocalSourceMuted)
                {
                    applyingLocalSourceControl = true;
                    try
                    {
                        item.VolumePercent = representative.Volume * 100 * gain;
                        item.IsMuted = applicationSessions.All(snapshot => snapshot.IsMuted) ||
                            Network.IsLocalSourceMuted;
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
                Network.RegisterLocalApplicationSource(
                    existing.RoutingChannelId,
                    existing.ProcessId,
                    existing.DisplayName,
                    existing.ApplicationIdentityKey);
            }
        }

        RefreshLocalApplicationRoutes();

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
            float gain = Network.LocalSourceVolumePercent / 100;
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
    private const long VolumeSnapshotGuardMilliseconds = 1500;
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
    private bool isEditorOpen;
    private string windowsOutputText = "Windows 输出：未知设备";
    private string[] sessionIds = [];
    private AvailableApplicationOutputDeviceItemViewModel? selectedAdditionalOutput;
    private float? pendingVolumePercent;
    private long volumeSnapshotGuardUntil;

    public AudioSessionItemViewModel(
        WindowsAudioSession snapshot,
        Action<string, float> volumeChanged,
        Action<string, bool> muteChanged)
        : this([snapshot], volumeChanged, muteChanged)
    {
    }

    public AudioSessionItemViewModel(
        IReadOnlyList<WindowsAudioSession> snapshots,
        Action<string, float> volumeChanged,
        Action<string, bool> muteChanged)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            throw new ArgumentException("At least one audio session is required.", nameof(snapshots));
        }

        ApplicationIdentityKey = CreateApplicationIdentityKey(snapshots[0]);
        RoutingChannelId = CreateStableRoutingChannelId(ApplicationIdentityKey);
        this.volumeChanged = volumeChanged;
        this.muteChanged = muteChanged;
        displayName = snapshots[0].DisplayName;
        Update(snapshots);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string SessionId => sessionIds[0];
    public IReadOnlyList<string> SessionIds => sessionIds;
    public string ApplicationIdentityKey { get; }
    public Guid RoutingChannelId { get; }
    public bool CanRouteToListenSphere => ProcessId > 0;
    public ObservableCollection<LocalApplicationOutputRouteItemViewModel>
        AdditionalOutputRoutes { get; } = [];
    public ObservableCollection<AvailableApplicationOutputDeviceItemViewModel>
        AvailableAdditionalOutputs { get; } = [];
    public AvailableApplicationOutputDeviceItemViewModel? SelectedAdditionalOutput
    {
        get => selectedAdditionalOutput;
        set => SetField(ref selectedAdditionalOutput, value);
    }
    public string AdditionalOutputSummary => AdditionalOutputRoutes.Count == 0
        ? "尚未添加聆界附加输出"
        : $"已添加 {AdditionalOutputRoutes.Count} 个附加输出";
    public bool IsEditorOpen
    {
        get => isEditorOpen;
        set => SetField(ref isEditorOpen, value);
    }

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
    public string WindowsOutputText => windowsOutputText;

    public float VolumePercent
    {
        get => volumePercent;
        set
        {
            float normalized = Math.Clamp(value, 0, 100);
            if (SetField(ref volumePercent, normalized) && !applyingSnapshot)
            {
                pendingVolumePercent = normalized;
                volumeSnapshotGuardUntil =
                    Environment.TickCount64 + VolumeSnapshotGuardMilliseconds;
                foreach (string sessionId in sessionIds)
                {
                    volumeChanged(sessionId, normalized / 100);
                }
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
                    foreach (string sessionId in sessionIds)
                    {
                        muteChanged(sessionId, value);
                    }
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

    public void Update(WindowsAudioSession snapshot) => Update([snapshot]);

    public void Update(IReadOnlyList<WindowsAudioSession> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            return;
        }

        WindowsAudioSession representative = snapshots
            .OrderByDescending(snapshot => snapshot.IsActive)
            .ThenByDescending(snapshot => snapshot.Peak)
            .First();
        applyingSnapshot = true;
        try
        {
            sessionIds = snapshots
                .Select(snapshot => snapshot.SessionId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            OnPropertyChanged(nameof(SessionId));
            OnPropertyChanged(nameof(SessionIds));
            DisplayName = representative.DisplayName;
            ProcessId = representative.ProcessId;
            ApplySnapshotVolume(representative.Volume * 100);
            IsMuted = snapshots.All(snapshot => snapshot.IsMuted);
            IsActive = snapshots.Any(snapshot => snapshot.IsActive);
            PeakPercent = snapshots.Max(snapshot => snapshot.Peak) * 100;
            Icon = ApplicationIconLoader.Load(
                representative.ProcessPath,
                representative.IconPath);
            string[] outputNames = snapshots
                .Select(snapshot => snapshot.OutputDeviceName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            string nextOutputText = outputNames.Length switch
            {
                0 => "Windows 输出：未知设备",
                1 => $"Windows 输出：{outputNames[0]}",
                _ => $"Windows 输出：{outputNames.Length} 个设备 · {string.Join(" / ", outputNames)}"
            };
            if (!string.Equals(windowsOutputText, nextOutputText, StringComparison.Ordinal))
            {
                windowsOutputText = nextOutputText;
                OnPropertyChanged(nameof(WindowsOutputText));
            }
        }
        finally
        {
            applyingSnapshot = false;
        }
    }

    public void ReplaceAdditionalOutputRoutes(
        IReadOnlyList<ControllerNetworkViewModel.ApplicationOutputRouteInfo> routes,
        IReadOnlyList<AvailableApplicationOutputDeviceItemViewModel> availableDevices,
        Func<Guid, string, Task> remove)
    {
        bool routesChanged = AdditionalOutputRoutes.Count != routes.Count ||
            AdditionalOutputRoutes.Zip(routes).Any(pair =>
                !string.Equals(
                    pair.First.DeviceId,
                    pair.Second.DeviceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    pair.First.DeviceName,
                    pair.Second.DeviceName,
                    StringComparison.Ordinal) ||
                pair.First.IsActive != pair.Second.IsActive);
        if (routesChanged)
        {
            AdditionalOutputRoutes.Clear();
            foreach (ControllerNetworkViewModel.ApplicationOutputRouteInfo route in routes)
            {
                AdditionalOutputRoutes.Add(new LocalApplicationOutputRouteItemViewModel(
                    RoutingChannelId,
                    route.DeviceId,
                    route.DeviceName,
                    route.IsActive,
                    remove));
            }

            OnPropertyChanged(nameof(AdditionalOutputSummary));
        }

        bool availableDevicesChanged =
            AvailableAdditionalOutputs.Count != availableDevices.Count ||
            !AvailableAdditionalOutputs.SequenceEqual(availableDevices);
        if (availableDevicesChanged)
        {
            string? selectedDeviceId = SelectedAdditionalOutput?.DeviceId;
            AvailableAdditionalOutputs.Clear();
            foreach (AvailableApplicationOutputDeviceItemViewModel device in availableDevices)
            {
                AvailableAdditionalOutputs.Add(device);
            }

            SelectedAdditionalOutput = AvailableAdditionalOutputs.FirstOrDefault(device =>
                string.Equals(device.DeviceId, selectedDeviceId, StringComparison.Ordinal)) ??
                AvailableAdditionalOutputs.FirstOrDefault();
        }
    }

    private void ApplySnapshotVolume(float snapshotVolumePercent)
    {
        float normalized = Math.Clamp(snapshotVolumePercent, 0, 100);
        bool guardActive =
            pendingVolumePercent.HasValue &&
            Environment.TickCount64 <= volumeSnapshotGuardUntil;
        bool confirmsPendingValue =
            pendingVolumePercent.HasValue &&
            Math.Abs(normalized - pendingVolumePercent.Value) <= 0.5f;

        if (guardActive && !confirmsPendingValue)
        {
            return;
        }

        pendingVolumePercent = null;
        volumeSnapshotGuardUntil = 0;
        VolumePercent = normalized;
    }

    public static string CreateApplicationIdentityKey(WindowsAudioSession snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ProcessPath))
        {
            try
            {
                return $"path:{Path.GetFullPath(snapshot.ProcessPath).ToUpperInvariant()}";
            }
            catch (Exception) when (
                snapshot.ProcessPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                // Fall back to the display identity below for protected/system sessions.
            }
        }

        string prefix = snapshot.ProcessId > 0 ? "app" : "system";
        return $"{prefix}:{snapshot.DisplayName.Trim().ToUpperInvariant()}";
    }

    private static Guid CreateStableRoutingChannelId(string identityKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identityKey));
        return new Guid(hash.AsSpan(0, 16));
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

public sealed class LocalApplicationOutputRouteItemViewModel
{
    public LocalApplicationOutputRouteItemViewModel(
        Guid channelId,
        string deviceId,
        string deviceName,
        bool isActive,
        Func<Guid, string, Task> remove)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        IsActive = isActive;
        RemoveCommand = new AsyncRelayCommand(() => remove(channelId, DeviceId));
    }

    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool IsActive { get; }
    public string StatusText => IsActive ? "正在输出" : "等待设备恢复";
    public AsyncRelayCommand RemoveCommand { get; }
}

public sealed record AvailableApplicationOutputDeviceItemViewModel(
    string DeviceId,
    string DeviceName);
