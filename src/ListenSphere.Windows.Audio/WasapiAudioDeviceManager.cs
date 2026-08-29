using ListenSphere.Audio.Abstractions;
using NAudio.CoreAudioApi;

namespace ListenSphere.Windows.Audio;

/// <summary>Enumerates active Windows render endpoints using stable MMDevice IDs.</summary>
public sealed class WasapiAudioDeviceManager : IAudioDeviceManager
{
    public ValueTask<IReadOnlyList<IAudioDevice>> GetPlaybackDevicesAsync(
        CancellationToken cancellationToken) =>
        GetDevicesAsync(DataFlow.Render, cancellationToken);

    public ValueTask<IReadOnlyList<IAudioDevice>> GetRecordingDevicesAsync(
        CancellationToken cancellationToken) =>
        GetDevicesAsync(DataFlow.Capture, cancellationToken);

    private static ValueTask<IReadOnlyList<IAudioDevice>> GetDevicesAsync(
        DataFlow dataFlow,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(dataFlow, Role.Console))
        {
            using var defaultEndpoint =
                enumerator.GetDefaultAudioEndpoint(dataFlow, Role.Console);
            defaultId = defaultEndpoint.ID;
        }

        var endpoints = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
        var devices = new List<IAudioDevice>(endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
            {
                cancellationToken.ThrowIfCancellationRequested();
                devices.Add(new WindowsAudioDevice(
                    endpoint.ID,
                    string.IsNullOrWhiteSpace(endpoint.FriendlyName)
                        ? endpoint.DeviceFriendlyName
                        : endpoint.FriendlyName,
                    string.Equals(endpoint.ID, defaultId, StringComparison.Ordinal),
                    WindowsAudioEndpointClassifier.IsBluetooth(endpoint)));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<IAudioDevice>>(devices);
    }
}

public static class WindowsAudioEndpointClassifier
{
    public static bool IsBluetooth(MMDevice endpoint)
    {
        string? instanceId = null;
        string? controllerId = null;
        endpoint.Properties.TryGetValue(
            PropertyKeys.PKEY_Device_InstanceId,
            out instanceId);
        endpoint.Properties.TryGetValue(
            PropertyKeys.PKEY_Device_ControllerDeviceId,
            out controllerId);
        return IsBluetooth(instanceId, controllerId);
    }

    public static bool IsBluetooth(string? instanceId, string? controllerId) =>
        IsBluetoothIdentifier(instanceId) || IsBluetoothIdentifier(controllerId);

    private static bool IsBluetoothIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("BTHA2DP", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("BTHLEDEVICE", StringComparison.OrdinalIgnoreCase));
}
