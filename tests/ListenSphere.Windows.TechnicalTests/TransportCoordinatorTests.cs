using System.IO;
using System.Net;
using ListenSphere.Controller;
using ListenSphere.Controller.Coordinators;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class TransportCoordinatorTests
{
    [Fact]
    public async Task InitializeAsync_IsIdempotentAndPublishesAvailableTransports()
    {
        var runtime = new FakeTransportRuntime();
        await using var coordinator = new TransportCoordinator(runtime);

        await coordinator.InitializeAsync(TestContext.Current.CancellationToken);
        await coordinator.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, runtime.ControlServerStarts);
        Assert.Equal(1, runtime.DiscoveryRestarts);
        Assert.Equal(1, runtime.UsbStarts);
        Assert.True(runtime.BluetoothStarts >= 1);
        Assert.Equal("端口：56974", coordinator.Snapshot.WirelessPortText);
        Assert.Equal("RFCOMM 音频服务已开启", coordinator.Snapshot.BluetoothStatus);
        Assert.Equal("正在等待原生 USB 设备", coordinator.Snapshot.UsbStatus);
    }

    [Fact]
    public async Task SelectTransportAsync_UpdatesSnapshotAndProbesBluetooth()
    {
        var runtime = new FakeTransportRuntime();
        await using var coordinator = new TransportCoordinator(runtime);
        int before = runtime.BluetoothStarts;

        await coordinator.SelectTransportAsync(
            RemoteTransportMode.Bluetooth,
            TestContext.Current.CancellationToken);

        Assert.Equal(RemoteTransportMode.Bluetooth, coordinator.Snapshot.SelectedTransport);
        Assert.True(runtime.BluetoothStarts > before);
    }

    [Fact]
    public async Task NetworkAddressChange_RefreshesEndpointAndRepublishesAfterDebounce()
    {
        var runtime = new FakeTransportRuntime();
        await using var coordinator = new TransportCoordinator(runtime);
        await coordinator.InitializeAsync(TestContext.Current.CancellationToken);
        int before = runtime.DiscoveryRestarts;
        runtime.Addresses = [IPAddress.Parse("192.168.137.1")];

        runtime.RaiseNetworkAddressChanged();

        Assert.Equal("IP 地址：192.168.137.1", coordinator.Snapshot.WirelessIpAddressText);
        await Task.Delay(
            TimeSpan.FromMilliseconds(1_200),
            TestContext.Current.CancellationToken);
        Assert.Equal(before + 1, runtime.DiscoveryRestarts);
    }

    [Fact]
    public async Task BluetoothFailure_IsReportedWithoutFailingInitialization()
    {
        var runtime = new FakeTransportRuntime { BluetoothFailure = new IOException("off") };
        await using var coordinator = new TransportCoordinator(runtime);

        await coordinator.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "蓝牙不可用 · 开启系统蓝牙后将自动重试",
            coordinator.Snapshot.BluetoothStatus);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndReleasesRuntime()
    {
        var runtime = new FakeTransportRuntime();
        var coordinator = new TransportCoordinator(runtime);
        await coordinator.InitializeAsync(TestContext.Current.CancellationToken);

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        Assert.Equal(1, runtime.DisposeCount);
    }

    private sealed class FakeTransportRuntime : ITransportRuntime
    {
        public event EventHandler? NetworkAddressChanged;

        public int Port { get; set; } = 56_974;
        public IReadOnlyList<IPAddress> Addresses { get; set; } =
            [IPAddress.Parse("10.0.0.2")];
        public Exception? BluetoothFailure { get; set; }
        public int ControlServerStarts { get; private set; }
        public int DiscoveryRestarts { get; private set; }
        public int BluetoothStarts { get; private set; }
        public int UsbStarts { get; private set; }
        public int DisposeCount { get; private set; }

        public Task StartControlServerAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ControlServerStarts++;
            return Task.CompletedTask;
        }

        public void RestartDiscoveryPublisher() => DiscoveryRestarts++;

        public IReadOnlyList<IPAddress> GetManualConnectAddresses() => Addresses;

        public Task StartBluetoothAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BluetoothStarts++;
            return BluetoothFailure is null
                ? Task.CompletedTask
                : Task.FromException(BluetoothFailure);
        }

        public Task StartUsbAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UsbStarts++;
            return Task.CompletedTask;
        }

        public void RaiseNetworkAddressChanged() =>
            NetworkAddressChanged?.Invoke(this, EventArgs.Empty);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
