using ListenSphere.Network;
using ListenSphere.Windows.Bluetooth;
using ListenSphere.Windows.Usb;

namespace ListenSphere.Controller.Coordinators;

internal sealed record RemoteDeviceSnapshot(
    string PairingCode,
    string PairingHint,
    string PairingCodeActionText,
    DateTimeOffset? PairingCodeExpiresAt,
    IReadOnlyList<TrustedDevice> TrustedDevices,
    bool IsDeleteConfirmationVisible,
    string DeleteConfirmationDeviceName)
{
    public static RemoteDeviceSnapshot Empty { get; } = new(
        "------",
        "点击“生成验证码”以允许新设备配对。",
        "生成配对码",
        null,
        Array.Empty<TrustedDevice>(),
        false,
        string.Empty);
}

internal sealed record RemoteDeviceMutation(Guid DeviceId, string DisplayName);

internal interface IRemoteDeviceRuntime
{
    ValueTask<IReadOnlyList<TrustedDevice>> GetTrustedDevicesAsync(
        CancellationToken cancellationToken);

    ValueTask DisconnectBluetoothAsync(Guid deviceId);

    ValueTask DisconnectUsbAsync(Guid deviceId);

    ValueTask DisconnectControlAsync(Guid deviceId, CancellationToken cancellationToken);

    ValueTask RevokeControlAsync(Guid deviceId, CancellationToken cancellationToken);
}

internal sealed class RemoteDeviceRuntime(
    ITrustedDeviceStore trustStore,
    BluetoothRfcommProbeHost bluetoothHost,
    UsbAccessoryHost usbHost,
    ListenSphereControlServer server) : IRemoteDeviceRuntime
{
    public ValueTask<IReadOnlyList<TrustedDevice>> GetTrustedDevicesAsync(
        CancellationToken cancellationToken) =>
        trustStore.GetAllAsync(cancellationToken);

    public ValueTask DisconnectBluetoothAsync(Guid deviceId) =>
        bluetoothHost.DisconnectDeviceAsync(deviceId);

    public ValueTask DisconnectUsbAsync(Guid deviceId) =>
        usbHost.DisconnectDeviceAsync(deviceId);

    public ValueTask DisconnectControlAsync(
        Guid deviceId,
        CancellationToken cancellationToken) =>
        server.DisconnectDeviceAsync(deviceId, cancellationToken);

    public ValueTask RevokeControlAsync(
        Guid deviceId,
        CancellationToken cancellationToken) =>
        server.RevokeAsync(deviceId, cancellationToken);
}

internal sealed class RemoteDeviceCoordinator : IAsyncDisposable
{
    private readonly PairingCodeService pairingCodes;
    private readonly IRemoteDeviceRuntime runtime;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private RemoteDeviceSnapshot snapshot = RemoteDeviceSnapshot.Empty;
    private TaskCompletionSource<bool>? deleteConfirmation;
    private Task? pairingCodeCountdownLoop;
    private int initialized;
    private int disposed;

    public RemoteDeviceCoordinator(
        PairingCodeService pairingCodes,
        IRemoteDeviceRuntime runtime,
        TimeProvider? timeProvider = null)
    {
        this.pairingCodes = pairingCodes;
        this.runtime = runtime;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<RemoteDeviceSnapshot>? SnapshotChanged;

    public RemoteDeviceSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return snapshot;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref initialized, 1) == 0)
        {
            pairingCodeCountdownLoop = MonitorPairingCodeAsync(lifetime.Token);
        }

        await RefreshTrustedDevicesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task GenerateCodeAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        PairingCode code = pairingCodes.Generate();
        UpdateSnapshot(current => current with
        {
            PairingCode = $"{code.Value[..3]} {code.Value[3..]}",
            PairingCodeExpiresAt = code.ExpiresAt,
            PairingCodeActionText = "重新生成"
        });
        UpdatePairingCodeCountdown();
        return Task.CompletedTask;
    }

    public async Task RefreshTrustedDevicesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TrustedDevice> devices = await runtime
            .GetTrustedDevicesAsync(cancellationToken)
            .ConfigureAwait(false);
        TrustedDevice[] ordered = devices
            .OrderBy(device => device.DisplayName, StringComparer.CurrentCulture)
            .ToArray();
        UpdateSnapshot(current => current with { TrustedDevices = ordered });
    }

    public void CompletePairingCodeIfConsumed(string successMessage)
    {
        RemoteDeviceSnapshot current = Snapshot;
        if (current.PairingCodeExpiresAt is not { } expiresAt || pairingCodes.HasActiveCode)
        {
            return;
        }

        ClearPairingCode(
            timeProvider.GetUtcNow() >= expiresAt
                ? "配对码已过期，请重新生成。"
                : successMessage);
    }

    public async Task DisconnectAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        await runtime.DisconnectBluetoothAsync(deviceId).ConfigureAwait(false);
        await runtime.DisconnectUsbAsync(deviceId).ConfigureAwait(false);
        await runtime.DisconnectControlAsync(deviceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteDeviceMutation?> RevokeAsync(
        Guid deviceId,
        string? fallbackDisplayName,
        CancellationToken cancellationToken = default)
    {
        TrustedDevice? trusted = Snapshot.TrustedDevices.FirstOrDefault(
            device => device.DeviceId == deviceId);
        string? displayName = trusted?.DisplayName ?? fallbackDisplayName;
        if (string.IsNullOrWhiteSpace(displayName) ||
            !await RequestDeleteConfirmationAsync(displayName, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        await runtime.DisconnectBluetoothAsync(deviceId).ConfigureAwait(false);
        await runtime.DisconnectUsbAsync(deviceId).ConfigureAwait(false);
        await runtime.RevokeControlAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await RefreshTrustedDevicesAsync(cancellationToken).ConfigureAwait(false);
        return new RemoteDeviceMutation(deviceId, displayName);
    }

    public Task CompleteDeleteConfirmationAsync(bool confirmed)
    {
        TaskCompletionSource<bool>? completion;
        lock (sync)
        {
            completion = deleteConfirmation;
            deleteConfirmation = null;
            snapshot = snapshot with
            {
                IsDeleteConfirmationVisible = false,
                DeleteConfirmationDeviceName = string.Empty
            };
        }

        PublishSnapshot();
        completion?.TrySetResult(confirmed);
        return Task.CompletedTask;
    }

    private async Task<bool> RequestDeleteConfirmationAsync(
        string deviceName,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion;
        lock (sync)
        {
            if (deleteConfirmation is not null)
            {
                return false;
            }

            completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            deleteConfirmation = completion;
            snapshot = snapshot with
            {
                IsDeleteConfirmationVisible = true,
                DeleteConfirmationDeviceName = deviceName
            };
        }

        PublishSnapshot();
        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TaskCompletionSource<bool>? cancelled = null;
            lock (sync)
            {
                if (ReferenceEquals(deleteConfirmation, completion))
                {
                    cancelled = deleteConfirmation;
                    deleteConfirmation = null;
                    snapshot = snapshot with
                    {
                        IsDeleteConfirmationVisible = false,
                        DeleteConfirmationDeviceName = string.Empty
                    };
                }
            }

            if (cancelled is not null)
            {
                PublishSnapshot();
                cancelled.TrySetCanceled(cancellationToken);
            }
            throw;
        }
    }

    private async Task MonitorPairingCodeAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                UpdatePairingCodeCountdown();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal coordinator shutdown.
        }
    }

    private void UpdatePairingCodeCountdown()
    {
        RemoteDeviceSnapshot current = Snapshot;
        if (current.PairingCodeExpiresAt is not { } expiresAt)
        {
            return;
        }

        TimeSpan remaining = expiresAt - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            ClearPairingCode("配对码已过期，请重新生成。");
            return;
        }

        if (!pairingCodes.HasActiveCode)
        {
            UpdateSnapshot(value => value with
            {
                PairingHint = "配对码已验证，正在建立设备信任…"
            });
            return;
        }

        int remainingSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        UpdateSnapshot(value => value with
        {
            PairingHint =
                $"剩余 {remainingSeconds / 60:00}:{remainingSeconds % 60:00} · 单次使用，仅首次配对需要。"
        });
    }

    private void ClearPairingCode(string hint)
    {
        pairingCodes.Cancel();
        UpdateSnapshot(current => current with
        {
            PairingCode = "------",
            PairingHint = hint,
            PairingCodeActionText = "生成配对码",
            PairingCodeExpiresAt = null
        });
    }

    private void UpdateSnapshot(Func<RemoteDeviceSnapshot, RemoteDeviceSnapshot> update)
    {
        lock (sync)
        {
            snapshot = update(snapshot);
        }

        PublishSnapshot();
    }

    private void PublishSnapshot() => SnapshotChanged?.Invoke(this, Snapshot);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await CompleteDeleteConfirmationAsync(false).ConfigureAwait(false);
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (pairingCodeCountdownLoop is not null)
        {
            await pairingCodeCountdownLoop.ConfigureAwait(false);
        }
        lifetime.Dispose();
    }
}
