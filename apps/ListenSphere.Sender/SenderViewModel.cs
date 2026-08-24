using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using ListenSphere.Network;
using ListenSphere.Windows.Audio;
using ListenSphere.Windows.Devices;
using Microsoft.Win32;
using Serilog;

namespace ListenSphere.Sender;

public sealed class SenderViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IAudioDeviceManager deviceManager;
    private readonly IWasapiCaptureSourceFactory captureFactory;
    private readonly ITestTonePlayer testTonePlayer;
    private readonly IDebugWaveRecorder debugWaveRecorder;
    private readonly IWindowsDeviceNotificationSource deviceNotifications;
    private readonly DispatcherTimer statisticsTimer;
    private WindowsAudioDevice? selectedDevice;
    private WasapiLoopbackCaptureSource? captureSource;
    private CancellationTokenSource? captureLifetime;
    private CancellationTokenSource? deviceChangeDebounce;
    private string statusText = "正在读取 Windows 输出设备…";
    private string statisticsText = "尚未开始捕获";
    private string errorText = string.Empty;
    private double peakPercent;
    private double rmsPercent;
    private bool initialized;
    private bool captureRequested;
    private bool recoveringCapture;
    private bool disposed;

    public SenderViewModel(
        IAudioDeviceManager deviceManager,
        IWasapiCaptureSourceFactory captureFactory,
        ITestTonePlayer testTonePlayer,
        IDebugWaveRecorder debugWaveRecorder,
        IWindowsDeviceNotificationSource deviceNotifications,
        SenderNetworkViewModel network)
    {
        this.deviceManager = deviceManager;
        this.captureFactory = captureFactory;
        this.testTonePlayer = testTonePlayer;
        this.debugWaveRecorder = debugWaveRecorder;
        this.deviceNotifications = deviceNotifications;
        Network = network;
        Network.AudioSessionChanged += OnAudioSessionChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshDevicesAsync, () => !IsCapturing);
        ToggleCaptureCommand = new AsyncRelayCommand(
            ToggleCaptureAsync,
            () => SelectedDevice is not null);
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
    public SenderNetworkViewModel Network { get; }

    public AsyncRelayCommand RefreshCommand { get; }
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
            }
        }
    }

    public bool IsCapturing => captureSource?.IsRunning == true;
    public string ToggleCaptureText => IsCapturing ? "停止捕获" : "开始捕获";

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
        deviceNotifications.Changed += OnDeviceChanged;
        await Network.InitializeAsync();
        await RefreshDevicesAsync();
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
        deviceChangeDebounce?.Cancel();
        deviceChangeDebounce?.Dispose();
        captureRequested = false;
        await StopCaptureAsync();
        Network.AudioSessionChanged -= OnAudioSessionChanged;
        await Network.DisposeAsync();
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
        if (SelectedDevice is null)
        {
            return;
        }

        ErrorText = string.Empty;
        try
        {
            captureLifetime = new CancellationTokenSource();
            captureSource = captureFactory.Create(SelectedDevice.Id);
            captureSource.CaptureStopped += OnCaptureStopped;
            var sink = new LevelMonitoringSink(UpdateLevels, Network.SendAudioAsync);
            await captureSource.StartAsync(sink, captureLifetime.Token);
            statisticsTimer.Start();
            captureRequested = true;
            StatusText = $"正在捕获：{SelectedDevice.DisplayName}";
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
        statisticsTimer.Stop();
        var source = captureSource;
        captureSource = null;
        if (source is not null)
        {
            source.CaptureStopped -= OnCaptureStopped;
            try
            {
                await source.StopAsync(CancellationToken.None);
            }
            finally
            {
                await source.DisposeAsync();
            }
        }

        captureLifetime?.Cancel();
        captureLifetime?.Dispose();
        captureLifetime = null;
        PeakPercent = 0;
        RmsPercent = 0;
        StatusText = "捕获已停止";
        OnPropertyChanged(nameof(IsCapturing));
        OnPropertyChanged(nameof(ToggleCaptureText));
        RaiseCommandStates();
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
        var statistics = captureSource?.Statistics;
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
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            StatusText = Network.IsAudioReady
                ? "P4 UDP 音频会话已就绪；开始捕获后将发送到 Controller。"
                : "UDP 音频会话已断开；当前只保留本机捕获。";
        });
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        ToggleCaptureCommand.RaiseCanExecuteChanged();
        TestToneCommand.RaiseCanExecuteChanged();
        RecordDebugWaveCommand.RaiseCanExecuteChanged();
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
}
