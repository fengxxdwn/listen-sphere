using System.Collections.ObjectModel;
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
    private ControllerItemViewModel? selectedController;
    private string pairingCode = string.Empty;
    private string statusText = "正在搜索局域网内的 ListenSphere Controller…";
    private Task? discoveryLoop;
    private bool initialized;
    private bool connecting;
    private Guid? connectedControllerId;
    private Guid? pairingRequiredControllerId;
    private UdpAudioSender? audioSender;
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
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? AudioSessionChanged;

    public ObservableCollection<ControllerItemViewModel> Controllers { get; } = [];
    public AsyncRelayCommand ConnectCommand { get; }

    public bool IsAudioReady => audioSender is not null;

    public UdpAudioSenderStatistics? AudioStatistics => audioSender?.Statistics;
    public ControlClientStatistics ControlStatistics => client.Statistics;

    public ControllerItemViewModel? SelectedController
    {
        get => selectedController;
        set
        {
            if (SetField(ref selectedController, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PairingCode
    {
        get => pairingCode;
        set => SetField(ref pairingCode, value);
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
                StatusText = $"{result.Controller.DisplayName} · 已通过证书固定自动重连";
            }
            else if (result.Outcome == ControlClientOutcome.PairingRequired)
            {
                pairingRequiredControllerId = controller.Device.DeviceId;
            }
        }
        catch (Exception exception) when (
            exception is IOException or
                System.Net.Sockets.SocketException or
                System.Security.Authentication.AuthenticationException)
        {
            StatusText = $"自动连接暂不可用：{exception.Message}";
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

        connecting = true;
        ConnectCommand.RaiseCanExecuteChanged();
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
                PairingCode = string.Empty;
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
        }
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
