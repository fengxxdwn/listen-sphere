using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using ListenSphere.Configuration;
using ListenSphere.Network;
using ListenSphere.Windows.Audio;
using ListenSphere.Windows.AudioSessions;
using ListenSphere.Windows.Devices;
using Microsoft.Win32;
using Serilog;

namespace ListenSphere.Sender;

public sealed class SenderViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const int AudioFrameBytes = 480 * 2 * sizeof(float);
    private readonly IAudioDeviceManager deviceManager;
    private readonly IWasapiCaptureSourceFactory captureFactory;
    private readonly IProcessLoopbackCaptureSourceFactory processCaptureFactory;
    private readonly IWindowsAudioSessionManager sessionManager;
    private readonly ITestTonePlayer testTonePlayer;
    private readonly IDebugWaveRecorder debugWaveRecorder;
    private readonly IWindowsDeviceNotificationSource deviceNotifications;
    private readonly ISettingsStore settingsStore;
    private readonly DispatcherTimer statisticsTimer;
    private readonly HashSet<string> selectedApplicationKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private WindowsAudioDevice? selectedDevice;
    private WasapiLoopbackCaptureSource? captureSource;
    private readonly List<WasapiProcessLoopbackCaptureSource> processCaptureSources = [];
    private readonly List<ApplicationCapturePipeline> applicationPipelines = [];
    private readonly SemaphoreSlim applicationPipelineGate = new(1, 1);
    private Guid? systemAudioChannelId;
    private bool applicationCaptureRunning;
    private bool isApplicationMode = true;
    private CancellationTokenSource? captureLifetime;
    private CancellationTokenSource? deviceChangeDebounce;
    private CancellationTokenSource? preferencesSaveDebounce;
    private ListenSphereSettings senderSettings = new();
    private string statusText = "正在读取 Windows 输出设备…";
    private string statisticsText = "尚未开始捕获";
    private string errorText = string.Empty;
    private double peakPercent;
    private double rmsPercent;
    private bool initialized;
    private bool captureRequested;
    private bool stoppingCapture;
    private bool recoveringCapture;
    private bool networkStreamsNeedReopen;
    private bool loadingPreferences;
    private bool disposed;

    public SenderViewModel(
        IAudioDeviceManager deviceManager,
        IWasapiCaptureSourceFactory captureFactory,
        IProcessLoopbackCaptureSourceFactory processCaptureFactory,
        IWindowsAudioSessionManager sessionManager,
        ITestTonePlayer testTonePlayer,
        IDebugWaveRecorder debugWaveRecorder,
        IWindowsDeviceNotificationSource deviceNotifications,
        ISettingsStore settingsStore,
        SenderNetworkViewModel network)
    {
        this.deviceManager = deviceManager;
        this.captureFactory = captureFactory;
        this.processCaptureFactory = processCaptureFactory;
        this.sessionManager = sessionManager;
        this.testTonePlayer = testTonePlayer;
        this.debugWaveRecorder = debugWaveRecorder;
        this.deviceNotifications = deviceNotifications;
        this.settingsStore = settingsStore;
        Network = network;
        Network.AudioSessionChanged += OnAudioSessionChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshSourcesAsync, () => !IsCapturing);
        SelectApplicationsCommand = new AsyncRelayCommand(
            () => SelectCaptureModeAsync(true),
            () => !IsCapturing && !IsApplicationMode);
        SelectSystemCommand = new AsyncRelayCommand(
            () => SelectCaptureModeAsync(false),
            () => !IsCapturing && IsApplicationMode);
        ToggleCaptureCommand = new AsyncRelayCommand(
            ToggleCaptureAsync,
            CanStartOrStopCapture);
        TestToneCommand = new AsyncRelayCommand(
            PlayTestToneAsync,
            () => SelectedDevice is not null && !IsCapturing);
        RecordDebugWaveCommand = new AsyncRelayCommand(
            RecordDebugWaveAsync,
            () => SelectedDevice is not null && !IsCapturing);
        statisticsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, UpdateStatistics, Application.Current.Dispatcher);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<WindowsAudioDevice> Devices { get; } = [];
    public ObservableCollection<SenderApplicationItemViewModel> Applications { get; } = [];
    public SenderNetworkViewModel Network { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SelectApplicationsCommand { get; }
    public AsyncRelayCommand SelectSystemCommand { get; }
    public AsyncRelayCommand ToggleCaptureCommand { get; }
    public AsyncRelayCommand TestToneCommand { get; }
    public AsyncRelayCommand RecordDebugWaveCommand { get; }

    public WindowsAudioDevice? SelectedDevice
    {
        get => selectedDevice;
        set
        {
            if (SetField(ref selectedDevice, value))
            {
                RaiseCommandStates();
                QueuePreferencesSave();
            }
        }
    }

    public bool IsApplicationMode => isApplicationMode;
    public bool IsSystemMode => !isApplicationMode;
    public int SelectedApplicationCount => Applications.Count(item => item.IsSelected);
    public bool IsCapturing => captureLifetime is not null;
    public string ToggleCaptureText => stoppingCapture
        ? "正在停止…"
        : IsCapturing ? "停止捕获" : "开始捕获";

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public string StatisticsText
    {
        get => statisticsText;
        private set => SetField(ref statisticsText, value);
    }

    public string ErrorText
    {
        get => errorText;
        private set => SetField(ref errorText, value);
    }

    public double PeakPercent
    {
        get => peakPercent;
        private set => SetField(ref peakPercent, value);
    }

    public double RmsPercent
    {
        get => rmsPercent;
        private set => SetField(ref rmsPercent, value);
    }

    public async Task InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        loadingPreferences = true;
        senderSettings = await settingsStore.LoadAsync(CancellationToken.None);
        foreach (string key in senderSettings.SenderSelectedApplicationKeys)
        {
            selectedApplicationKeys.Add(key);
        }
        isApplicationMode = !string.Equals(
            senderSettings.SenderCaptureMode,
            "system",
            StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(IsApplicationMode));
        OnPropertyChanged(nameof(IsSystemMode));
        deviceNotifications.Changed += OnDeviceChanged;
        sessionManager.SessionsChanged += OnSessionsChanged;
        sessionManager.MonitoringFailed += OnSessionMonitoringFailed;
        await Network.InitializeAsync();
        await RefreshDevicesAsync();
        ApplyApplicationSessions(
            await sessionManager.GetSessionsAsync(CancellationToken.None));
        await sessionManager.StartMonitoringAsync(CancellationToken.None);
        loadingPreferences = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        statisticsTimer.Stop();
        deviceNotifications.Changed -= OnDeviceChanged;
        sessionManager.SessionsChanged -= OnSessionsChanged;
        sessionManager.MonitoringFailed -= OnSessionMonitoringFailed;
        deviceChangeDebounce?.Cancel();
        deviceChangeDebounce?.Dispose();
        preferencesSaveDebounce?.Cancel();
        preferencesSaveDebounce?.Dispose();
        captureRequested = false;
        await StopCaptureAsync();
        await sessionManager.StopMonitoringAsync(CancellationToken.None);
        await sessionManager.DisposeAsync();
        Network.AudioSessionChanged -= OnAudioSessionChanged;
        await Network.DisposeAsync();
        await SavePreferencesAsync(CancellationToken.None);
        applicationPipelineGate.Dispose();
    }

    private async Task RefreshSourcesAsync()
    {
        await RefreshDevicesAsync();
        ApplyApplicationSessions(
            await sessionManager.GetSessionsAsync(CancellationToken.None));
    }

    private Task SelectCaptureModeAsync(bool applications)
    {
        if (isApplicationMode == applications)
        {
            return Task.CompletedTask;
        }

        isApplicationMode = applications;
        OnPropertyChanged(nameof(IsApplicationMode));
        OnPropertyChanged(nameof(IsSystemMode));
        StatusText = applications
            ? "请选择需要转发声音的应用。"
            : "将捕获所选 Windows 输出设备的全部声音。";
        QueuePreferencesSave();
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    private async Task RefreshDevicesAsync()
    {
        ErrorText = string.Empty;
        try
        {
            var previousId = SelectedDevice?.Id;
            var devices = await deviceManager.GetPlaybackDevicesAsync(CancellationToken.None);
            Devices.Clear();
            foreach (var device in devices.Cast<WindowsAudioDevice>())
            {
                Devices.Add(device);
            }

            SelectedDevice =
                Devices.FirstOrDefault(device => device.Id == previousId) ??
                Devices.FirstOrDefault(device => device.Id == senderSettings.SenderCaptureDeviceId) ??
                Devices.FirstOrDefault(device => device.IsDefault) ??
                Devices.FirstOrDefault();
            StatusText = Devices.Count == 0
                ? "没有找到活动的 Windows 输出设备。"
                : $"已找到 {Devices.Count} 个输出设备。请选择设备后开始捕获。";
        }
        catch (Exception exception)
        {
            ErrorText = $"读取音频设备失败：{exception.Message}";
            StatusText = "无法读取输出设备";
            Log.Error(exception, "Failed to enumerate Windows audio devices");
        }
    }

    private void OnSessionsChanged(object? sender, AudioSessionsChangedEventArgs args) =>
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
            ApplyApplicationSessions(args.Sessions));

    private void OnSessionMonitoringFailed(
        object? sender,
        AudioSessionMonitoringFailedEventArgs args) =>
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ErrorText = $"读取应用声音状态失败，将自动重试：{args.Exception.Message}";
            Log.Warning(args.Exception, "Sender application-session monitoring failed");
        });

    private void ApplyApplicationSessions(IReadOnlyList<WindowsAudioSession> sessions)
    {
        var groups = sessions
            .Where(session => session.ProcessId > 0 &&
                session.ProcessId != Environment.ProcessId)
            .GroupBy(CreateApplicationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, StringComparer.OrdinalIgnoreCase);

        for (int index = Applications.Count - 1; index >= 0; index--)
        {
            if (!groups.ContainsKey(Applications[index].IdentityKey))
            {
                Applications.RemoveAt(index);
            }
        }

        foreach ((string key, IGrouping<string, WindowsAudioSession> group) in groups)
        {
            WindowsAudioSession representative = group
                .OrderByDescending(session => session.IsActive)
                .ThenByDescending(session => session.Peak)
                .First();
            int[] processIds = group.Select(session => session.ProcessId).Distinct().ToArray();
            float peak = group.Max(session => session.Peak) * 100;
            bool active = group.Any(session => session.IsActive);
            SenderApplicationItemViewModel? existing = Applications.FirstOrDefault(
                item => string.Equals(item.IdentityKey, key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new SenderApplicationItemViewModel(
                    key,
                    representative.DisplayName,
                    representative.ProcessPath,
                    processIds,
                    active,
                    peak,
                    selectedApplicationKeys.Contains(key),
                    OnApplicationSelectionChanged);
                Applications.Add(existing);
            }
            else
            {
                existing.Update(
                    representative.DisplayName,
                    representative.ProcessPath,
                    processIds,
                    active,
                    peak);
            }
        }

        OnPropertyChanged(nameof(SelectedApplicationCount));
        ToggleCaptureCommand.RaiseCanExecuteChanged();
        if (!IsCapturing && IsApplicationMode)
        {
            StatusText = Applications.Count == 0
                ? "当前没有检测到正在使用声音的应用。"
                : $"检测到 {Applications.Count} 个应用；已选择 {SelectedApplicationCount} 个。";
        }
        else if (captureRequested && IsApplicationMode)
        {
            _ = ReconcileApplicationPipelinesSafelyAsync();
        }
    }

    private static string CreateApplicationKey(WindowsAudioSession session) =>
        string.IsNullOrWhiteSpace(session.ProcessPath)
            ? $"{session.DisplayName}|{session.ProcessId}"
            : session.ProcessPath;

    private void OnApplicationSelectionChanged(SenderApplicationItemViewModel application)
    {
        if (application.IsSelected)
        {
            selectedApplicationKeys.Add(application.IdentityKey);
        }
        else
        {
            selectedApplicationKeys.Remove(application.IdentityKey);
        }

        OnPropertyChanged(nameof(SelectedApplicationCount));
        StatusText = $"已选择 {SelectedApplicationCount} 个应用。";
        RaiseCommandStates();
        QueuePreferencesSave();
        if (captureRequested && IsApplicationMode)
        {
            _ = ReconcileApplicationPipelinesSafelyAsync();
        }
    }

    private async Task ToggleCaptureAsync()
    {
        if (IsCapturing)
        {
            captureRequested = false;
            await StopCaptureAsync();
        }
        else
        {
            await StartCaptureAsync();
        }
    }

    private async Task StartCaptureAsync()
    {
        if (IsApplicationMode && SelectedApplicationCount == 0)
        {
            ErrorText = "请至少选择一个应用。";
            return;
        }
        if (IsSystemMode && SelectedDevice is null)
        {
            return;
        }

        ErrorText = string.Empty;
        try
        {
            captureLifetime = new CancellationTokenSource();
            if (IsApplicationMode)
            {
                await StartApplicationCaptureAsync(captureLifetime.Token);
            }
            else
            {
                await StartSystemCaptureAsync(captureLifetime.Token);
            }
            statisticsTimer.Start();
            captureRequested = true;
            StatusText = IsApplicationMode
                ? $"正在转发 {SelectedApplicationCount} 个应用的声音"
                : $"正在捕获：{SelectedDevice!.DisplayName}";
            OnPropertyChanged(nameof(IsCapturing));
            OnPropertyChanged(nameof(ToggleCaptureText));
            RaiseCommandStates();
        }
        catch (Exception exception)
        {
            ErrorText = $"启动捕获失败：{exception.Message}";
            StatusText = "捕获未启动";
            Log.Error(exception, "Failed to start loopback capture");
            captureRequested = false;
            await StopCaptureAsync();
        }
    }

    private async Task StopCaptureAsync()
    {
        long stopStarted = Stopwatch.GetTimestamp();
        statisticsTimer.Stop();
        var source = captureSource;
        captureSource = null;
        applicationCaptureRunning = false;
        var lifetime = captureLifetime;
        captureLifetime = null;
        lifetime?.Cancel();
        await applicationPipelineGate.WaitAsync();
        ApplicationCapturePipeline[] pipelines;
        try
        {
            pipelines = applicationPipelines.ToArray();
            applicationPipelines.Clear();
            processCaptureSources.Clear();
        }
        finally
        {
            applicationPipelineGate.Release();
        }
        Guid? systemChannel = systemAudioChannelId;
        systemAudioChannelId = null;

        if (source is not null)
        {
            source.CaptureStopped -= OnCaptureStopped;
        }

        stoppingCapture = source is not null || pipelines.Length > 0;
        PeakPercent = 0;
        RmsPercent = 0;
        StatusText = stoppingCapture ? "正在停止捕获…" : "捕获已停止";
        OnPropertyChanged(nameof(IsCapturing));
        OnPropertyChanged(nameof(ToggleCaptureText));
        RaiseCommandStates();

        try
        {
            var shutdownTasks = new List<Task>(pipelines.Length + 1);
            if (source is not null)
            {
                shutdownTasks.Add(StopAndDisposeAsync(source));
            }
            shutdownTasks.AddRange(pipelines.Select(StopApplicationPipelineAsync));

            await Task.WhenAll(shutdownTasks).ConfigureAwait(true);
            if (systemChannel is Guid channelId)
            {
                await Network.CloseApplicationAudioStreamAsync(
                    channelId,
                    CancellationToken.None);
            }
        }
        finally
        {
            lifetime?.Dispose();
            stoppingCapture = false;
            StatusText = "捕获已停止";
            OnPropertyChanged(nameof(ToggleCaptureText));
            RaiseCommandStates();
            Log.Information(
                "Sender capture stopped in {ElapsedMilliseconds:F1} ms",
                Stopwatch.GetElapsedTime(stopStarted).TotalMilliseconds);
        }
    }

    private static Task StopAndDisposeAsync(WasapiLoopbackCaptureSource source) =>
        Task.Run(async () =>
        {
            try
            {
                await source.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        });

    private static Task StopAndDisposeAsync(WasapiProcessLoopbackCaptureSource source) =>
        Task.Run(async () =>
        {
            try
            {
                await source.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        });

    private static async Task IgnoreExpectedCancellationAsync(
        Task task,
        CancellationTokenSource? lifetime)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true)
        {
        }
    }

    private async Task StartSystemCaptureAsync(CancellationToken cancellationToken)
    {
        if (!Network.IsAudioReady)
        {
            throw new InvalidOperationException("请先连接目标 Controller，再开始转发系统声音。");
        }
        Guid channelId = await Network.OpenApplicationAudioStreamAsync(
            $"system-output:{SelectedDevice!.Id}",
            "系统声音",
            cancellationToken,
            "system");
        systemAudioChannelId = channelId;
        captureSource = captureFactory.Create(SelectedDevice!.Id);
        captureSource.CaptureStopped += OnCaptureStopped;
        var sink = new LevelMonitoringSink(
            UpdateLevels,
            (pcm, timestamp, token) => Network.SendApplicationAudioAsync(
                channelId,
                pcm,
                timestamp,
                token));
        await captureSource.StartAsync(sink, cancellationToken);
    }

    private async Task StartApplicationCaptureAsync(CancellationToken cancellationToken)
    {
        SenderApplicationItemViewModel[] selected = Applications
            .Where(item => item.IsSelected)
            .ToArray();
        if (selected.Length == 0 || selected.All(item => item.ProcessIds.Count == 0))
        {
            throw new InvalidOperationException("选中的应用当前没有可捕获进程。");
        }

        if (!Network.IsAudioReady)
        {
            throw new InvalidOperationException("请先连接目标 Controller，再开始转发应用声音。");
        }

        var failures = new List<Exception>();
        foreach (SenderApplicationItemViewModel application in selected)
        {
            try
            {
                applicationPipelines.Add(await StartApplicationPipelineAsync(
                    application,
                    cancellationToken));
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (applicationPipelines.Count == 0)
        {
            throw new AggregateException("所有选中应用均无法启动进程回环捕获。", failures);
        }
        applicationCaptureRunning = true;
        if (failures.Count > 0)
        {
            ErrorText = $"有 {failures.Count} 个应用暂时无法捕获，其余应用已开始发送。";
        }
    }

    private async Task<ApplicationCapturePipeline> StartApplicationPipelineAsync(
        SenderApplicationItemViewModel application,
        CancellationToken cancellationToken)
    {
        Guid channelId = await Network.OpenApplicationAudioStreamAsync(
            application.IdentityKey,
            application.DisplayName,
            cancellationToken);
        var pipelineLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var mixer = new RemotePcmMixer(
            AudioFrameBytes,
            startupFrames: 2,
            maximumFrames: 12);
        var sources = new List<WasapiProcessLoopbackCaptureSource>();
        var failures = new List<Exception>();
        foreach (int processId in application.ProcessIds.Distinct())
        {
            var processStreamId = Guid.NewGuid();
            mixer.RegisterStream(processStreamId);
            WasapiProcessLoopbackCaptureSource source =
                processCaptureFactory.Create(processId);
            try
            {
                await source.StartAsync(
                    new ApplicationMixerSink(processStreamId, mixer),
                    pipelineLifetime.Token);
                sources.Add(source);
                processCaptureSources.Add(source);
            }
            catch (Exception exception)
            {
                mixer.RemoveStream(processStreamId);
                await source.DisposeAsync();
                failures.Add(exception);
            }
        }

        if (sources.Count == 0)
        {
            pipelineLifetime.Dispose();
            await Network.CloseApplicationAudioStreamAsync(channelId, CancellationToken.None);
            throw new AggregateException(
                $"无法捕获应用“{application.DisplayName}”。",
                failures);
        }

        Task mixTask = MixSelectedApplicationAsync(
            channelId,
            mixer,
            pipelineLifetime.Token);
        return new ApplicationCapturePipeline(
            application.IdentityKey,
            channelId,
            application.DisplayName,
            application.ProcessIds.Distinct().Order().ToArray(),
            mixer,
            sources,
            pipelineLifetime,
            mixTask);
    }

    private async Task StopApplicationPipelineAsync(ApplicationCapturePipeline pipeline)
    {
        pipeline.Lifetime.Cancel();
        try
        {
            await Task.WhenAll(pipeline.Sources.Select(StopAndDisposeAsync));
            await IgnoreExpectedCancellationAsync(pipeline.MixTask, pipeline.Lifetime);
        }
        finally
        {
            try
            {
                await Network.CloseApplicationAudioStreamAsync(
                    pipeline.ChannelId,
                    CancellationToken.None);
            }
            finally
            {
                pipeline.Lifetime.Dispose();
            }
        }
    }

    private async Task ReconcileApplicationPipelinesSafelyAsync(bool forceReopen = false)
    {
        try
        {
            await ReconcileApplicationPipelinesAsync(forceReopen);
        }
        catch (OperationCanceledException) when (captureLifetime?.IsCancellationRequested != false)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to update live Sender application channels");
            ErrorText = $"更新应用声道失败：{exception.Message}";
        }
    }

    private async Task ReconcileApplicationPipelinesAsync(bool forceReopen = false)
    {
        CancellationTokenSource? capture = captureLifetime;
        if (capture is null || !IsApplicationMode)
        {
            return;
        }

        await applicationPipelineGate.WaitAsync(capture.Token);
        try
        {
            var desired = Applications
                .Where(item => item.IsSelected)
                .ToDictionary(item => item.IdentityKey, StringComparer.OrdinalIgnoreCase);
            ApplicationCapturePipeline[] removing = applicationPipelines
                .Where(pipeline =>
                    forceReopen ||
                    !desired.TryGetValue(pipeline.IdentityKey, out SenderApplicationItemViewModel? application) ||
                    !pipeline.ProcessIds.SequenceEqual(
                        application.ProcessIds.Distinct().Order()))
                .ToArray();
            foreach (ApplicationCapturePipeline pipeline in removing)
            {
                applicationPipelines.Remove(pipeline);
                foreach (WasapiProcessLoopbackCaptureSource source in pipeline.Sources)
                {
                    processCaptureSources.Remove(source);
                }
                await StopApplicationPipelineAsync(pipeline);
            }

            foreach (SenderApplicationItemViewModel application in desired.Values.Where(
                         application => !applicationPipelines.Any(pipeline =>
                             string.Equals(
                                 pipeline.IdentityKey,
                                 application.IdentityKey,
                                 StringComparison.OrdinalIgnoreCase))))
            {
                applicationPipelines.Add(await StartApplicationPipelineAsync(
                    application,
                    capture.Token));
            }

            applicationCaptureRunning = applicationPipelines.Count > 0;
            StatusText = applicationCaptureRunning
                ? $"正在转发 {applicationPipelines.Count} 个独立应用声道"
                : "应用转发已暂停；勾选应用即可继续。";
            OnPropertyChanged(nameof(IsCapturing));
            OnPropertyChanged(nameof(ToggleCaptureText));
            RaiseCommandStates();
        }
        finally
        {
            applicationPipelineGate.Release();
        }
    }

    private async Task MixSelectedApplicationAsync(
        Guid channelId,
        RemotePcmMixer mixer,
        CancellationToken cancellationToken)
    {
        var mixed = new byte[AudioFrameBytes];
        ulong timestamp = 0;
        var sink = new LevelMonitoringSink(
            UpdateLevels,
            (pcm, frameTimestamp, token) => Network.SendApplicationAudioAsync(
                channelId,
                pcm,
                frameTimestamp,
                token));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (mixer.TryMixNext(mixed))
            {
                await sink.WriteAsync(
                    new AudioFrame(mixed, AudioFormat.Default, 480, timestamp),
                    cancellationToken).ConfigureAwait(false);
            }
            timestamp += 480;
        }
    }

    private async Task PlayTestToneAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        ErrorText = string.Empty;
        StatusText = $"正在向 {SelectedDevice.DisplayName} 播放 1 秒测试音…";
        try
        {
            await testTonePlayer.PlayAsync(SelectedDevice.Id, CancellationToken.None);
            StatusText = "测试音播放完成";
        }
        catch (Exception exception)
        {
            ErrorText = $"测试音播放失败：{exception.Message}";
            StatusText = "测试音未播放";
            Log.Error(exception, "Failed to play test tone");
        }
    }

    private async Task RecordDebugWaveAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存 10 秒调试音频",
            Filter = "WAV 音频 (*.wav)|*.wav",
            DefaultExt = ".wav",
            AddExtension = true,
            FileName = $"ListenSphere-Debug-{DateTime.Now:yyyyMMdd-HHmmss}.wav"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ErrorText = string.Empty;
        StatusText = "正在录制 10 秒调试 WAV；请在此期间播放声音…";
        try
        {
            await debugWaveRecorder.RecordAsync(
                SelectedDevice.Id,
                dialog.FileName,
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            StatusText = $"调试音频已保存：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            ErrorText = $"调试音频录制失败：{exception.Message}";
            StatusText = "调试音频未保存";
            Log.Error(exception, "Failed to record debug WAV");
        }
    }

    private void OnCaptureStopped(object? sender, WasapiCaptureStoppedEventArgs args)
    {
        _ = RecoverAfterUnexpectedStopAsync(args);
    }

    private async Task RecoverAfterUnexpectedStopAsync(WasapiCaptureStoppedEventArgs args)
    {
        try
        {
            Task recovery = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (args.Exception is not null)
                {
                    ErrorText = $"音频设备已停止，正在自动恢复：{args.Exception.Message}";
                }

                return RecoverCaptureAsync("捕获设备异常停止");
            });
            await recovery.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Sender recovery after unexpected capture stop failed");
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                ErrorText = $"捕获设备自动恢复失败：{exception.Message}");
        }
    }

    private void OnDeviceChanged(object? sender, WindowsDeviceChange change)
    {
        deviceChangeDebounce?.Cancel();
        deviceChangeDebounce?.Dispose();
        deviceChangeDebounce = new CancellationTokenSource();
        CancellationToken token = deviceChangeDebounce.Token;
        _ = RecoverAfterDeviceChangeAsync(change, token);
    }

    private async Task RecoverAfterDeviceChangeAsync(
        WindowsDeviceChange change,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
            Task recovery = await Application.Current.Dispatcher.InvokeAsync(
                () => RecoverCaptureAsync($"检测到音频设备{GetDeviceChangeText(change)}"));
            await recovery.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Sender capture device recovery failed");
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                ErrorText = $"捕获设备自动恢复失败：{exception.Message}");
        }
    }

    private async Task RecoverCaptureAsync(string reason)
    {
        if (recoveringCapture || disposed)
        {
            return;
        }

        if (IsApplicationMode)
        {
            await RefreshDevicesAsync();
            return;
        }

        recoveringCapture = true;
        bool shouldRestart = captureRequested;
        string? previousDeviceId = SelectedDevice?.Id;
        try
        {
            StatusText = $"{reason}，正在刷新设备…";
            await StopCaptureAsync();
            await RefreshDevicesAsync();
            if (previousDeviceId is not null)
            {
                SelectedDevice =
                    Devices.FirstOrDefault(device => device.Id == previousDeviceId) ??
                    SelectedDevice;
            }

            if (shouldRestart && SelectedDevice is not null)
            {
                await StartCaptureAsync();
                if (IsCapturing)
                {
                    StatusText = $"捕获已自动恢复：{SelectedDevice.DisplayName}";
                }
            }
        }
        finally
        {
            recoveringCapture = false;
        }
    }

    private static string GetDeviceChangeText(WindowsDeviceChange change) => change switch
    {
        WindowsDeviceChange.Added => "接入",
        WindowsDeviceChange.Removed => "移除",
        WindowsDeviceChange.DefaultChanged => "默认项变化",
        _ => "状态变化"
    };

    private void UpdateLevels(AudioLevel level)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            PeakPercent = Math.Clamp(level.Peak * 100, 0, 100);
            RmsPercent = Math.Clamp(level.Rms * 100, 0, 100);
            if (level.IsClipping)
            {
                ErrorText = "检测到音频削波。";
            }
        }, DispatcherPriority.Background);
    }

    private void UpdateStatistics(object? sender, EventArgs args)
    {
        WasapiCaptureStatistics? statistics = captureSource?.Statistics;
        if (IsApplicationMode && processCaptureSources.Count > 0)
        {
            statistics = new WasapiCaptureStatistics(
                processCaptureSources.Sum(source => source.Statistics.CapturedBuffers),
                0);
        }
        if (statistics is not null)
        {
            StatisticsText =
                $"捕获缓冲：{statistics.CapturedBuffers:N0} · 队列丢弃：{statistics.DroppedBuffers:N0}" +
                FormatNetworkStatistics();
        }
    }

    private string FormatNetworkStatistics()
    {
        UdpAudioSenderStatistics? network = Network.AudioStatistics;
        ControlClientStatistics control = Network.ControlStatistics;
        return network is null
            ? " · UDP 未连接"
            : $" · UDP 帧：{network.FramesSent:N0} · 数据报：{network.DatagramsSent:N0}" +
              $" · 失败：{network.SendFailures:N0}" +
              $" · RTT：{control.LastRoundTripMilliseconds:F1} ms";
    }

    private void OnAudioSessionChanged(object? sender, EventArgs args)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            if (!Network.IsAudioReady)
            {
                networkStreamsNeedReopen = captureRequested;
            }
            StatusText = Network.IsAudioReady
                ? "P4 UDP 音频会话已就绪；开始捕获后将发送到 Controller。"
                : "UDP 音频会话已断开；当前只保留本机捕获。";
            if (!Network.IsAudioReady || !captureRequested || !networkStreamsNeedReopen)
            {
                return;
            }

            networkStreamsNeedReopen = false;
            if (IsApplicationMode)
            {
                await ReconcileApplicationPipelinesSafelyAsync(forceReopen: true);
            }
            else
            {
                await RecoverCaptureAsync("网络连接已恢复");
            }
        });
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        ToggleCaptureCommand.RaiseCanExecuteChanged();
        SelectApplicationsCommand.RaiseCanExecuteChanged();
        SelectSystemCommand.RaiseCanExecuteChanged();
        TestToneCommand.RaiseCanExecuteChanged();
        RecordDebugWaveCommand.RaiseCanExecuteChanged();
    }

    private void QueuePreferencesSave()
    {
        if (!initialized || loadingPreferences || disposed)
        {
            return;
        }

        preferencesSaveDebounce?.Cancel();
        preferencesSaveDebounce?.Dispose();
        preferencesSaveDebounce = new CancellationTokenSource();
        _ = SavePreferencesAfterDelayAsync(preferencesSaveDebounce.Token);
    }

    private async Task SavePreferencesAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken);
            await SavePreferencesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to save Sender preferences");
        }
    }

    private async Task SavePreferencesAsync(CancellationToken cancellationToken)
    {
        senderSettings = senderSettings with
        {
            SenderSelectedApplicationKeys = selectedApplicationKeys
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SenderCaptureMode = IsApplicationMode ? "applications" : "system",
            SenderCaptureDeviceId = SelectedDevice?.Id
        };
        await settingsStore.SaveAsync(senderSettings, cancellationToken);
    }

    private bool CanStartOrStopCapture() =>
        IsCapturing ||
        (IsApplicationMode ? SelectedApplicationCount > 0 : SelectedDevice is not null);

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

    private sealed class LevelMonitoringSink(
        Action<AudioLevel> publish,
        Func<ReadOnlyMemory<byte>, ulong, CancellationToken, ValueTask> send) : IAudioFrameSink
    {
        private long lastPublished = Environment.TickCount64;

        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = MemoryMarshal.Cast<byte, float>(frame.Data.Span);
            var level = AudioLevelCalculator.Calculate(samples);
            var now = Environment.TickCount64;
            if (now - lastPublished >= 50)
            {
                lastPublished = now;
                publish(level);
            }

            return send(frame.Data, frame.Timestamp, cancellationToken);
        }
    }

    private sealed class ApplicationMixerSink(
        Guid streamId,
        RemotePcmMixer mixer) : IAudioFrameSink
    {
        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mixer.Enqueue(streamId, frame.Data.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ApplicationCapturePipeline(
        string IdentityKey,
        Guid ChannelId,
        string DisplayName,
        IReadOnlyList<int> ProcessIds,
        RemotePcmMixer Mixer,
        IReadOnlyList<WasapiProcessLoopbackCaptureSource> Sources,
        CancellationTokenSource Lifetime,
        Task MixTask);
}

public sealed class SenderApplicationItemViewModel : INotifyPropertyChanged
{
    private readonly Action<SenderApplicationItemViewModel> selectionChanged;
    private string displayName;
    private string? processPath;
    private IReadOnlyList<int> processIds;
    private bool isActive;
    private double peakPercent;
    private bool isSelected;

    public SenderApplicationItemViewModel(
        string identityKey,
        string displayName,
        string? processPath,
        IReadOnlyList<int> processIds,
        bool isActive,
        double peakPercent,
        bool isSelected,
        Action<SenderApplicationItemViewModel> selectionChanged)
    {
        IdentityKey = identityKey;
        this.displayName = displayName;
        this.processPath = processPath;
        this.processIds = processIds;
        this.isActive = isActive;
        this.peakPercent = peakPercent;
        this.isSelected = isSelected;
        this.selectionChanged = selectionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string IdentityKey { get; }
    public string DisplayName => displayName;
    public string ProcessText => processIds.Count == 1
        ? $"PID {processIds[0]}"
        : $"{processIds.Count} 个进程";
    public string StatusText => IsActive ? "正在发声" : "等待声音";
    public string? ProcessPath => processPath;
    public IReadOnlyList<int> ProcessIds => processIds;
    public bool IsActive => isActive;
    public double PeakPercent => peakPercent;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            selectionChanged(this);
        }
    }

    public void Update(
        string name,
        string? path,
        IReadOnlyList<int> processes,
        bool active,
        double peak)
    {
        displayName = name;
        processPath = path;
        processIds = processes;
        isActive = active;
        peakPercent = Math.Clamp(peak, 0, 100);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProcessPath)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProcessIds)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProcessText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PeakPercent)));
    }
}
