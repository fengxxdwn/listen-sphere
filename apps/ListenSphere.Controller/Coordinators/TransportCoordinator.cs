using System.Net;
using System.Net.NetworkInformation;
using ListenSphere.Device;
using ListenSphere.Network;
using ListenSphere.Windows.Bluetooth;
using ListenSphere.Windows.Usb;
using Serilog;

namespace ListenSphere.Controller.Coordinators;

internal sealed record TransportSnapshot(
    RemoteTransportMode SelectedTransport,
    string NetworkStatus,
    string WirelessIpAddressText,
    string WirelessPortText,
    string BluetoothStatus,
    string UsbStatus);

internal interface ITransportRuntime : IAsyncDisposable
{
    event EventHandler? NetworkAddressChanged;

    int Port { get; }

    Task StartControlServerAsync(CancellationToken cancellationToken);

    void RestartDiscoveryPublisher();

    IReadOnlyList<IPAddress> GetManualConnectAddresses();

    Task StartBluetoothAsync(CancellationToken cancellationToken);

    Task StartUsbAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Owns transport discovery and availability state. It deliberately does not own
/// remote-device sessions or audio-frame consumption; those responsibilities are
/// extracted by later R2 coordinators.
/// </summary>
internal sealed class TransportCoordinator : IAsyncDisposable
{
    private readonly ITransportRuntime runtime;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private CancellationTokenSource? networkChangeDebounce;
    private Task? bluetoothRecoveryLoop;
    private TransportSnapshot snapshot = new(
        RemoteTransportMode.Wireless,
        "控制服务尚未启动",
        "IP 地址：正在检测…",
        "端口：--",
        "正在初始化",
        "正在初始化");
    private bool initialized;
    private bool disposed;

    public TransportCoordinator(
        ListenSphereControlServer server,
        BluetoothRfcommProbeHost bluetoothHost,
        UsbAccessoryHost usbHost,
        LocalDeviceIdentity identity)
        : this(new ControllerTransportRuntime(server, bluetoothHost, usbHost, identity))
    {
    }

    internal TransportCoordinator(ITransportRuntime runtime)
    {
        this.runtime = runtime;
        runtime.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    public event EventHandler<TransportSnapshot>? SnapshotChanged;

    public TransportSnapshot Snapshot => Volatile.Read(ref snapshot);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            await runtime.StartControlServerAsync(cancellationToken).ConfigureAwait(false);
            RestartDiscoveryPublisher();
            await EnsureBluetoothListeningAsync(cancellationToken).ConfigureAwait(false);
            await runtime.StartUsbAsync(cancellationToken).ConfigureAwait(false);
            Update(current => current with { UsbStatus = "正在等待原生 USB 设备" });
            bluetoothRecoveryLoop = RecoverBluetoothAvailabilityAsync(lifetime.Token);
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RestartDiscoveryPublisher();
        await EnsureBluetoothListeningAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SelectTransportAsync(
        RemoteTransportMode transport,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Update(current => current with { SelectedTransport = transport });
        if (transport == RemoteTransportMode.Bluetooth)
        {
            await EnsureBluetoothListeningAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void ReportNetworkStatus(string value) =>
        Update(current => current with { NetworkStatus = value });

    public void ReportBluetoothStatus(string value) =>
        Update(current => current with { BluetoothStatus = value });

    public void ReportUsbStatus(string value) =>
        Update(current => current with { UsbStatus = value });

    private void OnNetworkAddressChanged(object? sender, EventArgs eventArgs)
    {
        RefreshManualEndpoint();
        CancellationTokenSource replacement =
            CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref networkChangeDebounce,
            replacement);
        previous?.Cancel();
        previous?.Dispose();
        _ = RestartDiscoveryPublisherAfterDelayAsync(replacement.Token);
    }

    private async Task RestartDiscoveryPublisherAfterDelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            RestartDiscoveryPublisher();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to republish mDNS after network change");
        }
    }

    private void RestartDiscoveryPublisher()
    {
        runtime.RestartDiscoveryPublisher();
        RefreshManualEndpoint();
        Update(current => current with
        {
            NetworkStatus =
                $"正在发布 {ListenSphereDiscovery.QualifiedServiceName} · TCP {runtime.Port}"
        });
    }

    private void RefreshManualEndpoint()
    {
        IReadOnlyList<IPAddress> addresses = runtime.GetManualConnectAddresses();
        Update(current => current with
        {
            WirelessIpAddressText = addresses.Count == 0
                ? "IP 地址：等待网络"
                : $"IP 地址：{string.Join(" / ", addresses)}",
            WirelessPortText = $"端口：{runtime.Port}"
        });
    }

    private async Task RecoverBluetoothAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await EnsureBluetoothListeningAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task EnsureBluetoothListeningAsync(CancellationToken cancellationToken)
    {
        try
        {
            await runtime.StartBluetoothAsync(cancellationToken).ConfigureAwait(false);
            ReportBluetoothStatus("RFCOMM 音频服务已开启");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            const string unavailable = "蓝牙不可用 · 开启系统蓝牙后将自动重试";
            bool changed = !string.Equals(
                Snapshot.BluetoothStatus,
                unavailable,
                StringComparison.Ordinal);
            ReportBluetoothStatus(unavailable);
            if (changed)
            {
                Log.Warning(exception, "Bluetooth unavailable; automatic retry is active");
            }
        }
    }

    private void Update(Func<TransportSnapshot, TransportSnapshot> update)
    {
        TransportSnapshot next;
        TransportSnapshot previous;
        do
        {
            previous = Snapshot;
            next = update(previous);
            if (next == previous)
            {
                return;
            }
        }
        while (!ReferenceEquals(
            Interlocked.CompareExchange(ref snapshot, next, previous),
            previous));

        SnapshotChanged?.Invoke(this, next);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        runtime.NetworkAddressChanged -= OnNetworkAddressChanged;
        CancellationTokenSource? debounce = Interlocked.Exchange(
            ref networkChangeDebounce,
            null);
        debounce?.Cancel();
        debounce?.Dispose();
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (bluetoothRecoveryLoop is not null)
        {
            await bluetoothRecoveryLoop.ConfigureAwait(false);
        }

        await runtime.DisposeAsync().ConfigureAwait(false);
        initializationGate.Dispose();
        lifetime.Dispose();
    }
}

internal sealed class ControllerTransportRuntime : ITransportRuntime
{
    private readonly ListenSphereControlServer server;
    private readonly BluetoothRfcommProbeHost bluetoothHost;
    private readonly UsbAccessoryHost usbHost;
    private readonly LocalDeviceIdentity identity;
    private MdnsControllerPublisher? publisher;
    private bool networkEventsSubscribed;
    private bool disposed;

    public ControllerTransportRuntime(
        ListenSphereControlServer server,
        BluetoothRfcommProbeHost bluetoothHost,
        UsbAccessoryHost usbHost,
        LocalDeviceIdentity identity)
    {
        this.server = server;
        this.bluetoothHost = bluetoothHost;
        this.usbHost = usbHost;
        this.identity = identity;
    }

    public event EventHandler? NetworkAddressChanged;

    public int Port => server.Port;

    public async Task StartControlServerAsync(CancellationToken cancellationToken)
    {
        await server.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!networkEventsSubscribed)
        {
            NetworkChange.NetworkAddressChanged += ForwardNetworkAddressChanged;
            networkEventsSubscribed = true;
        }
    }

    public void RestartDiscoveryPublisher()
    {
        var replacement = new MdnsControllerPublisher(identity, server.Port);
        MdnsControllerPublisher? previous = publisher;
        publisher = replacement;
        previous?.Dispose();
    }

    public IReadOnlyList<IPAddress> GetManualConnectAddresses() =>
        ListenSphereDiscovery.GetManualConnectAddresses();

    public Task StartBluetoothAsync(CancellationToken cancellationToken) =>
        bluetoothHost.StartAsync(cancellationToken);

    public Task StartUsbAsync(CancellationToken cancellationToken) =>
        usbHost.StartAsync(cancellationToken);

    private void ForwardNetworkAddressChanged(object? sender, EventArgs eventArgs) =>
        NetworkAddressChanged?.Invoke(this, eventArgs);

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        if (networkEventsSubscribed)
        {
            NetworkChange.NetworkAddressChanged -= ForwardNetworkAddressChanged;
            networkEventsSubscribed = false;
        }
        publisher?.Dispose();
        publisher = null;
        return ValueTask.CompletedTask;
    }
}
