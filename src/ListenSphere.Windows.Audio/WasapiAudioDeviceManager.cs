using ListenSphere.Audio.Abstractions;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

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

    public ValueTask SetDefaultRecordingDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsDefaultAudioEndpoint.SetDefault(deviceId);
        return ValueTask.CompletedTask;
    }

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

internal static class WindowsDefaultAudioEndpoint
{
    public static void SetDefault(string deviceId)
    {
        Type policyConfigType = Type.GetTypeFromCLSID(
            new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"),
            throwOnError: true)!;
        var policyConfig = (IPolicyConfig)Activator.CreateInstance(policyConfigType)!;
        try
        {
            SetDefaultForRole(policyConfig, deviceId, PolicyRole.Console);
            SetDefaultForRole(policyConfig, deviceId, PolicyRole.Multimedia);
            SetDefaultForRole(policyConfig, deviceId, PolicyRole.Communications);
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(policyConfig);
        }
    }

    private static void SetDefaultForRole(
        IPolicyConfig policyConfig,
        string deviceId,
        PolicyRole role)
    {
        int result = policyConfig.SetDefaultEndpoint(deviceId, role);
        Marshal.ThrowExceptionForHR(result);
    }

    private enum PolicyRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();

        [PreserveSig]
        int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            PolicyRole role);

        void SetEndpointVisibility();
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
