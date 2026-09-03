using System.IO;
using System.Diagnostics;
using ListenSphere.Configuration;
using ListenSphere.Controller.Presentation;
using ListenSphere.Controller.Services;
using ListenSphere.Diagnostics;
using ListenSphere.Windows.AudioSessions;
using Serilog;

namespace ListenSphere.Controller;

public sealed class ControllerViewModel : ObservableViewModel, IAsyncDisposable
{
    private readonly ControllerSettingsCoordinator settingsCoordinator;
    private readonly IDiagnosticEventSink diagnosticEvents;
    private string senderStatusText = "发送端未启动";
    private bool initialized;
    private bool disposed;

    public ControllerViewModel(
        IWindowsAudioSessionManager sessionManager,
        ControllerNetworkViewModel network,
        ControllerSettingsCoordinator settingsCoordinator,
        ISceneService sceneService,
        ISceneSerializationService sceneSerializationService,
        IFileDialogService fileDialogService,
        IDiagnosticsExporter diagnosticsExporter,
        IDiagnosticEventSink diagnosticEvents)
    {
        Network = network;
        this.settingsCoordinator = settingsCoordinator;
        this.diagnosticEvents = diagnosticEvents;
        LocalSessions = new LocalSessionsViewModel(sessionManager, network);
        Scene = new SceneViewModel(
            sceneService,
            sceneSerializationService,
            fileDialogService,
            LocalSessions,
            network,
            () => settingsCoordinator.TrySaveAsync());
        Diagnostics = new DiagnosticsViewModel(diagnosticsExporter, diagnosticEvents);
        FirstRun = new FirstRunViewModel(() => settingsCoordinator.TrySaveAsync());
        EnableSenderCommand = new AsyncRelayCommand(EnableSenderAsync);
        Dashboard = new ControllerDashboardViewModel(this);
        settingsCoordinator.ConfigureSnapshotFactory(CaptureSettings);
        settingsCoordinator.SaveFailed += OnSettingsSaveFailed;
        LocalSessions.SettingsChanged += OnLocalSettingsChanged;
    }

    public ControllerNetworkViewModel Network { get; }
    public ControllerDashboardViewModel Dashboard { get; }
    public LocalSessionsViewModel LocalSessions { get; }
    public SceneViewModel Scene { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public FirstRunViewModel FirstRun { get; }
    public AsyncRelayCommand EnableSenderCommand { get; }

    public string SenderStatusText
    {
        get => senderStatusText;
        private set => SetField(ref senderStatusText, value);
    }

    public async Task InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        ListenSphereSettings settings;
        try
        {
            settings = await settingsCoordinator.LoadAsync(CancellationToken.None);
            if (settingsCoordinator.RecoveryPath is { } recoveryPath)
            {
                LocalSessions.ReportError(
                    $"检测到损坏的设置文件，已恢复默认设置。原文件已备份为：{Path.GetFileName(recoveryPath)}");
                diagnosticEvents.Record(
                    DiagnosticSeverity.Warning,
                    "configuration.recovered",
                    new Dictionary<string, object?> { ["backupCreated"] = true });
            }
        }
        catch (Exception exception)
        {
            settingsCoordinator.UseDefaults();
            settings = settingsCoordinator.Current;
            LocalSessions.ReportError($"设置文件无法读取，已使用默认设置：{exception.Message}");
            Log.Warning(exception, "Failed to load controller settings; defaults are in use");
        }

        Scene.Initialize(settings.Scenes);
        FirstRun.Initialize(settings.FirstRunCompleted);
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
        await LocalSessions.InitializeAsync();
    }

    private ListenSphereSettings CaptureSettings(ListenSphereSettings current) => current with
    {
        PlaybackDeviceId = Network.SelectedPlaybackDevice?.Id,
        FollowSystemDefaultPlayback = Network.FollowSystemDefaultPlayback,
        MasterVolume = Network.MasterVolumePercent / 100,
        FirstRunCompleted = !FirstRun.IsVisible,
        Scenes = Scene.Scenes.ToArray(),
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

    private void OnLocalSettingsChanged(object? sender, EventArgs args) =>
        settingsCoordinator.QueueSave();

    private void OnSettingsSaveFailed(object? sender, string message) =>
        LocalSessions.ReportError(message);

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
            LocalSessions.ReportError(string.Empty);
        }
        catch (Exception exception)
        {
            SenderStatusText = "Sender 启动失败";
            LocalSessions.ReportError($"无法启用发送连接：{exception.Message}");
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

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        LocalSessions.SettingsChanged -= OnLocalSettingsChanged;
        settingsCoordinator.SaveFailed -= OnSettingsSaveFailed;
        Dashboard.Dispose();
        await settingsCoordinator.TrySaveAsync(CancellationToken.None);
        await LocalSessions.DisposeAsync();
        await Network.DisposeAsync();
        await settingsCoordinator.DisposeAsync();
    }
}