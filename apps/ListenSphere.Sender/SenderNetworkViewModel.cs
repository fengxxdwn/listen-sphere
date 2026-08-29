using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using ListenSphere.Device;
using ListenSphere.Network;
using Serilog;

namespace ListenSphere.Sender;

public sealed class SenderNetworkViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IDiscoveryService discovery;
    private readonly ListenSphereControlClient client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ReconnectBackoffTracker reconnectBackoff = new();
    private ControllerItemViewModel? selectedController;
    private string pairingCode = string.Empty;
    private string statusText = "正在搜索局域网内的 ListenSphere Controller…";
    private Task? discoveryLoop;
    private bool initialized;
    private bool connecting;
    private Guid? connectedControllerId;
    private Guid? pairingRequiredControllerId;
    private bool isPairingPromptVisible;
    private UdpAudioSender? audioSender;
    private readonly ConcurrentDictionary<Guid, ApplicationAudioSender> applicationAudioSenders = [];
    private bool disposed;

    public SenderNetworkViewModel(
        IDiscoveryService discovery,
        ListenSphereControlClient client)
    {
        this.discovery = discovery;
        this.client = client;
        ConnectCommand = new AsyncRelayCommand(
            ConnectAsync,
            () => SelectedController is not null && !connecting);
        SubmitPairingCommand = new AsyncRelayCommand(
            ConnectAsync,
            () => SelectedController is not null &&
                IsPairingPromptVisible &&
                PairingCode.Length == 6 &&
                !connecting);
        CancelPairingCommand = new AsyncRelayCommand(CancelPairingAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? AudioSessionChanged;

    public ObservableCollection<ControllerItemViewModel> Controllers { get; } = [];
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand SubmitPairingCommand { get; }
    public AsyncRelayCommand CancelPairingCommand { get; }

    public bool IsAudioReady => audioSender is not null;

    public UdpAudioSenderStatistics? AudioStatistics
    {
        get
        {
            UdpAudioSenderStatistics[] statistics =
                applicationAudioSenders.Values.Select(item => item.Sender.Statistics).ToArray();
            if (audioSender is not null)
            {
                statistics = [audioSender.Statistics, .. statistics];
            }
            return statistics.Length == 0 ? null : new UdpAudioSenderStatistics(
                statistics.Sum(item => item.FramesSent),
                statistics.Sum(item => item.DatagramsSent),
                statistics.Sum(item => item.BytesSent),
                statistics.Sum(item => item.SendFailures));
        }
    }
    public ControlClientStatistics ControlStatistics => client.Statistics;

    public ControllerItemViewModel? SelectedController
    {
        get => selectedController;
        set
        {
            if (SetField(ref selectedController, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
                SubmitPairingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PairingCode
    {
        get => pairingCode;
        set
        {
            string normalized = new((value ?? string.Empty)
                .Where(char.IsAsciiDigit)
                .Take(6)
                .ToArray());
            if (SetField(ref pairingCode, normalized))
            {
                SubmitPairingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPairingPromptVisible
    {
        get => isPairingPromptVisible;
        private set
        {
            if (SetField(ref isPairingPromptVisible, value))
            {
                SubmitPairingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public Task InitializeAsync()
    {
        if (initialized)
        {
            return Task.CompletedTask;
        }

        initialized = true;
        client.StateChanged += OnStateChanged;
        discoveryLoop = DiscoverAsync(lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (DiscoveredController controller in
                    discovery.DiscoverAsync(cancellationToken))
                {
                    if (ListenSphereDiscovery.IsLocalMachineAddress(
                        controller.ControlEndpoint.Address))
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            ControllerItemViewModel? local = Controllers.FirstOrDefault(
                                item => item.Controller.Device.DeviceId ==
                                    controller.Device.DeviceId);
                            if (local is not null)
                            {
                                Controllers.Remove(local);
                                if (ReferenceEquals(SelectedController, local))
                                {
                                    SelectedController = Controllers.FirstOrDefault();
                                }
                            }

                            StatusText = Controllers.Count == 0
                                ? "未发现可连接的其他电脑；本机 Controller 已自动忽略。"
                                : $"已发现 {Controllers.Count} 个可连接的 Controller。";
                        });
                        continue;
                    }

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ControllerItemViewModel? existing = Controllers.FirstOrDefault(
                            item => item.Controller.Device.DeviceId ==
                                controller.Device.DeviceId);
                        if (existing is null)
                        {
                            existing = new ControllerItemViewModel(controller);
                            Controllers.Add(existing);
                        }
                        else
                        {
                            existing.Update(controller);
                        }

                        SelectedController ??= existing;
                        StatusText =
                            $"已发现 {Controllers.Count} 个 Controller；未配对设备需要六位验证码。";
                    });

                    if (!connecting)
                    {
                        await TryAutomaticReconnectAsync(controller, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StatusText = "局域网发现已停止。";
                return;
            }
            catch (Exception exception)
            {
                StatusText = $"局域网发现暂时中断，2 秒后重试：{exception.Message}";
                Log.Warning(exception, "ListenSphere mDNS discovery interrupted; retrying");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StatusText = "局域网发现已停止。";
                return;
            }
        }
    }

    private async Task TryAutomaticReconnectAsync(
        DiscoveredController controller,
        CancellationToken cancellationToken)
    {
        if (connectedControllerId == controller.Device.DeviceId ||
            pairingRequiredControllerId == controller.Device.DeviceId)
        {
            return;
        }

        ReconnectBackoffDecision backoff = reconnectBackoff.Check(
            controller.Device.DeviceId,
            DateTimeOffset.UtcNow);
        if (!backoff.CanAttempt)
        {
            return;
        }

        connecting = true;
        ConnectCommand.RaiseCanExecuteChanged();
        try
        {
            ControlClientResult result = await client.ConnectAsync(
                controller,
                null,
                cancellationToken);
            if (result.Outcome == ControlClientOutcome.Connected)
            {
                await ReplaceAudioSenderAsync(result.AudioSession).ConfigureAwait(false);
                connectedControllerId = controller.Device.DeviceId;
                pairingRequiredControllerId = null;
                reconnectBackoff.Reset(controller.Device.DeviceId);
                StatusText = $"{result.Controller.DisplayName} · 已通过证书固定自动重连";
            }
            else if (result.Outcome == ControlClientOutcome.PairingRequired)
            {
                pairingRequiredControllerId = controller.Device.DeviceId;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusText =
                        $"{controller.Device.DisplayName} 尚未配对；选定目标并点击连接后输入验证码。";
                });
            }
        }
        catch (Exception exception) when (
            exception is IOException or
                System.Net.Sockets.SocketException or
                System.Security.Authentication.AuthenticationException)
        {
            ReconnectBackoffDecision retry = reconnectBackoff.RecordFailure(
                controller.Device.DeviceId,
                DateTimeOffset.UtcNow);
            StatusText =
                $"自动连接暂不可用；{Math.Ceiling(retry.RetryAfter.TotalSeconds):F0} 秒后进行第 {retry.FailureCount + 1} 次尝试。";
            Log.Debug(exception, "Automatic reconnect attempt {Attempt} failed", retry.FailureCount);
        }
        finally
        {
            connecting = false;
            ConnectCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ConnectAsync()
    {
        if (SelectedController is null)
        {
            return;
        }

        if (ListenSphereDiscovery.IsLocalMachineAddress(
            SelectedController.Controller.ControlEndpoint.Address))
        {
            StatusText = "不能连接同一台电脑上的 ListenSphere Controller。";
            Controllers.Remove(SelectedController);
            SelectedController = Controllers.FirstOrDefault();
            return;
        }

        if (string.IsNullOrWhiteSpace(PairingCode) &&
            pairingRequiredControllerId == SelectedController.Controller.Device.DeviceId)
        {
            PairingCode = string.Empty;
            IsPairingPromptVisible = true;
            StatusText = "请输入主控端显示的六位验证码。";
            return;
        }

        connecting = true;
        reconnectBackoff.Reset(SelectedController.Controller.Device.DeviceId);
        ConnectCommand.RaiseCanExecuteChanged();
        SubmitPairingCommand.RaiseCanExecuteChanged();
        StatusText = "正在建立 TLS 控制通道…";
        try
        {
            pairingRequiredControllerId = null;
            ControlClientResult result = await client.ConnectAsync(
                SelectedController.Controller,
                string.IsNullOrWhiteSpace(PairingCode) ? null : PairingCode.Trim(),
                lifetime.Token);
            StatusText = result.Message;
            if (result.Outcome == ControlClientOutcome.Connected)
            {
                await ReplaceAudioSenderAsync(result.AudioSession);
                connectedControllerId = result.Controller.DeviceId;
                reconnectBackoff.Reset(result.Controller.DeviceId);
                PairingCode = string.Empty;
                IsPairingPromptVisible = false;
            }
            else if (result.Outcome is ControlClientOutcome.PairingRequired or
                ControlClientOutcome.PairingRejected)
            {
                pairingRequiredControllerId = SelectedController.Controller.Device.DeviceId;
                IsPairingPromptVisible = true;
            }
        }
        catch (Exception exception)
        {
            StatusText = $"连接失败：{exception.Message}";
            Log.Error(exception, "ListenSphere control connection failed");
        }
        finally
        {
            connecting = false;
            ConnectCommand.RaiseCanExecuteChanged();
            SubmitPairingCommand.RaiseCanExecuteChanged();
        }
    }

    private Task CancelPairingAsync()
    {
        PairingCode = string.Empty;
        IsPairingPromptVisible = false;
        StatusText = "已取消首次配对。";
        return Task.CompletedTask;
    }

    private void OnStateChanged(object? sender, ControlPeerEvent args)
    {
        if (args.State is DeviceConnectionState.Offline or DeviceConnectionState.Faulted)
        {
            connectedControllerId = null;
            _ = ClearAudioSenderSafelyAsync();
        }

        _ = Application.Current.Dispatcher.BeginInvoke(() =>
            StatusText = args.Device is null
                ? args.Message
                : $"{args.Device.DisplayName} · {args.State} · {args.Message}");
    }

    public ValueTask SendAudioAsync(
        ReadOnlyMemory<byte> pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {
        UdpAudioSender? sender = audioSender;
        return sender is null
            ? ValueTask.CompletedTask
            : sender.SendFrameAsync(pcm, timestamp, cancellationToken);
    }

    public async ValueTask<Guid> OpenApplicationAudioStreamAsync(
        string sourceId,
        string displayName,
        CancellationToken cancellationToken,
        string sourceKind = "application")
    {
        OpenedAudioStream opened = await client.OpenAudioStreamAsync(
            sourceId,
            displayName,
            sourceKind,
            cancellationToken).ConfigureAwait(false);
        var sender = new UdpAudioSender(opened.Session);
        if (!applicationAudioSenders.TryAdd(
            opened.ChannelId,
            new ApplicationAudioSender(opened.Session.StreamId, sender)))
        {
            await sender.DisposeAsync().ConfigureAwait(false);
            await client.CloseAudioStreamAsync(
                opened.Session.StreamId,
                "重复应用声道",
                cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"应用声道已存在：{displayName}");
        }
        AudioSessionChanged?.Invoke(this, EventArgs.Empty);
        return opened.ChannelId;
    }

    public ValueTask SendApplicationAudioAsync(
        Guid channelId,
        ReadOnlyMemory<byte> pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {
        return applicationAudioSenders.TryGetValue(channelId, out ApplicationAudioSender? sender)
            ? sender.Sender.SendFrameAsync(pcm, timestamp, cancellationToken)
            : ValueTask.CompletedTask;
    }

    public async ValueTask CloseApplicationAudioStreamAsync(
        Guid channelId,
        CancellationToken cancellationToken = default)
    {
        if (!applicationAudioSenders.TryRemove(channelId, out ApplicationAudioSender? sender))
        {
            return;
        }
        await sender.Sender.DisposeAsync().ConfigureAwait(false);
        try
        {
            await client.CloseAudioStreamAsync(
                sender.StreamId,
                "Sender 已停止应用捕获",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Log.Debug(exception, "Application stream close notification was not delivered");
        }
        AudioSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReplaceAudioSenderAsync(AudioSessionParameters? session)
    {
        await ClearAudioSenderAsync().ConfigureAwait(false);
        if (session is null)
        {
            throw new InvalidDataException("Controller did not provide a P4 audio session.");
        }

        audioSender = new UdpAudioSender(session);
        AudioSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ClearAudioSenderAsync()
    {
        ApplicationAudioSender[] applications = applicationAudioSenders.Values.ToArray();
        applicationAudioSenders.Clear();
        foreach (ApplicationAudioSender application in applications)
        {
            await application.Sender.DisposeAsync().ConfigureAwait(false);
        }
        UdpAudioSender? previous = Interlocked.Exchange(ref audioSender, null);
        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
            AudioSessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ClearAudioSenderSafelyAsync()
    {
        try
        {
            await ClearAudioSenderAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to clear Sender UDP audio session");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        client.StateChanged -= OnStateChanged;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await ClearAudioSenderAsync().ConfigureAwait(false);
        if (discoveryLoop is not null)
        {
            await discoveryLoop.ConfigureAwait(false);
        }

        await client.DisposeAsync();
        await discovery.DisposeAsync();
        lifetime.Dispose();
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

internal sealed record ApplicationAudioSender(uint StreamId, UdpAudioSender Sender);

public sealed class ControllerItemViewModel : INotifyPropertyChanged
{
    public ControllerItemViewModel(DiscoveredController controller)
    {
        Controller = controller;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DiscoveredController Controller { get; private set; }

    public string DisplayLabel =>
        $"{Controller.Device.DisplayName} · {Controller.ControlEndpoint.Address}";

    public void Update(DiscoveredController controller)
    {
        Controller = controller;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
    }
}
