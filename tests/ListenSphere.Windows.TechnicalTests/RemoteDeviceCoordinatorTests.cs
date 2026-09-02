using ListenSphere.Controller.Coordinators;
using ListenSphere.Network;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class RemoteDeviceCoordinatorTests
{
    [Fact]
    public async Task InitializeAsync_ProjectsTrustedDevicesInDisplayNameOrder()
    {
        var runtime = new FakeRemoteDeviceRuntime(
            CreateDevice("Zulu"),
            CreateDevice("Alpha"));
        await using var coordinator = new RemoteDeviceCoordinator(
            new PairingCodeService(),
            runtime);

        await coordinator.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Alpha", "Zulu"], coordinator.Snapshot.TrustedDevices
            .Select(device => device.DisplayName));
    }

    [Fact]
    public async Task GenerateCodeAsync_PublishesFormattedSingleUseCode()
    {
        await using var coordinator = new RemoteDeviceCoordinator(
            new PairingCodeService(),
            new FakeRemoteDeviceRuntime());

        await coordinator.GenerateCodeAsync();

        Assert.Matches("^[0-9]{3} [0-9]{3}$", coordinator.Snapshot.PairingCode);
        Assert.Equal("重新生成", coordinator.Snapshot.PairingCodeActionText);
        Assert.Contains("单次使用", coordinator.Snapshot.PairingHint);
        Assert.NotNull(coordinator.Snapshot.PairingCodeExpiresAt);
    }

    [Fact]
    public async Task ConsumedPairingCode_IsClearedAfterTrustIsEstablished()
    {
        var pairingCodes = new PairingCodeService();
        await using var coordinator = new RemoteDeviceCoordinator(
            pairingCodes,
            new FakeRemoteDeviceRuntime());
        await coordinator.GenerateCodeAsync();
        string rawCode = coordinator.Snapshot.PairingCode.Replace(" ", string.Empty);

        Assert.Equal(
            PairingCodeValidation.Accepted,
            pairingCodes.ValidateAndConsume(rawCode));
        coordinator.CompletePairingCodeIfConsumed("设备已建立身份信任。");

        Assert.Equal("------", coordinator.Snapshot.PairingCode);
        Assert.Equal("生成配对码", coordinator.Snapshot.PairingCodeActionText);
        Assert.Equal("设备已建立身份信任。", coordinator.Snapshot.PairingHint);
        Assert.Null(coordinator.Snapshot.PairingCodeExpiresAt);
    }

    [Fact]
    public async Task DisconnectAsync_ClosesAllThreeTransports()
    {
        var runtime = new FakeRemoteDeviceRuntime();
        await using var coordinator = new RemoteDeviceCoordinator(
            new PairingCodeService(),
            runtime);
        Guid deviceId = Guid.NewGuid();

        await coordinator.DisconnectAsync(
            deviceId,
            TestContext.Current.CancellationToken);

        Assert.Equal([deviceId], runtime.BluetoothDisconnects);
        Assert.Equal([deviceId], runtime.UsbDisconnects);
        Assert.Equal([deviceId], runtime.ControlDisconnects);
    }

    [Fact]
    public async Task RevokeAsync_WaitsForConfirmationThenRevokesAndRefreshesTrust()
    {
        TrustedDevice device = CreateDevice("Tablet");
        var runtime = new FakeRemoteDeviceRuntime(device);
        await using var coordinator = new RemoteDeviceCoordinator(
            new PairingCodeService(),
            runtime);
        await coordinator.InitializeAsync(TestContext.Current.CancellationToken);

        Task<RemoteDeviceMutation?> revoke = coordinator.RevokeAsync(
            device.DeviceId,
            null,
            TestContext.Current.CancellationToken);

        Assert.True(coordinator.Snapshot.IsDeleteConfirmationVisible);
        Assert.Equal("Tablet", coordinator.Snapshot.DeleteConfirmationDeviceName);
        await coordinator.CompleteDeleteConfirmationAsync(true);
        RemoteDeviceMutation? result = await revoke;

        Assert.NotNull(result);
        Assert.Equal(device.DeviceId, result.DeviceId);
        Assert.Empty(coordinator.Snapshot.TrustedDevices);
        Assert.Equal([device.DeviceId], runtime.BluetoothDisconnects);
        Assert.Equal([device.DeviceId], runtime.UsbDisconnects);
        Assert.Equal([device.DeviceId], runtime.ControlRevocations);
    }

    [Fact]
    public async Task RevokeAsync_WhenCancelledLeavesTrustAndConnectionsUnchanged()
    {
        TrustedDevice device = CreateDevice("Phone");
        var runtime = new FakeRemoteDeviceRuntime(device);
        await using var coordinator = new RemoteDeviceCoordinator(
            new PairingCodeService(),
            runtime);
        await coordinator.InitializeAsync(TestContext.Current.CancellationToken);

        Task<RemoteDeviceMutation?> revoke = coordinator.RevokeAsync(
            device.DeviceId,
            null,
            TestContext.Current.CancellationToken);
        await coordinator.CompleteDeleteConfirmationAsync(false);

        Assert.Null(await revoke);
        Assert.Single(coordinator.Snapshot.TrustedDevices);
        Assert.Empty(runtime.BluetoothDisconnects);
        Assert.Empty(runtime.UsbDisconnects);
        Assert.Empty(runtime.ControlRevocations);
    }

    private static TrustedDevice CreateDevice(string displayName) => new(
        Guid.NewGuid(),
        displayName,
        "Android",
        0,
        "fingerprint",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private sealed class FakeRemoteDeviceRuntime(params TrustedDevice[] devices) :
        IRemoteDeviceRuntime
    {
        private readonly List<TrustedDevice> trustedDevices = [.. devices];

        public List<Guid> BluetoothDisconnects { get; } = [];
        public List<Guid> UsbDisconnects { get; } = [];
        public List<Guid> ControlDisconnects { get; } = [];
        public List<Guid> ControlRevocations { get; } = [];

        public ValueTask<IReadOnlyList<TrustedDevice>> GetTrustedDevicesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<TrustedDevice>>(
                trustedDevices.ToArray());
        }

        public ValueTask DisconnectBluetoothAsync(Guid deviceId)
        {
            BluetoothDisconnects.Add(deviceId);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectUsbAsync(Guid deviceId)
        {
            UsbDisconnects.Add(deviceId);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectControlAsync(
            Guid deviceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ControlDisconnects.Add(deviceId);
            return ValueTask.CompletedTask;
        }

        public ValueTask RevokeControlAsync(
            Guid deviceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ControlRevocations.Add(deviceId);
            trustedDevices.RemoveAll(device => device.DeviceId == deviceId);
            return ValueTask.CompletedTask;
        }
    }
}
