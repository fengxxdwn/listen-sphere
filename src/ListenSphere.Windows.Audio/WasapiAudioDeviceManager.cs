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

    public async ValueTask SetDefaultPlaybackDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        cancellationToken.ThrowIfCancellationRequested();
        await WindowsDefaultAudioEndpoint.SetDefaultAndVerifyAsync(
            deviceId,
            DataFlow.Render,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetDefaultRecordingDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        cancellationToken.ThrowIfCancellationRequested();
        await WindowsDefaultAudioEndpoint.SetDefaultAndVerifyAsync(
            deviceId,
            DataFlow.Capture,
            cancellationToken).ConfigureAwait(false);
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
    private static readonly PolicyRole[] Roles =
    [
        PolicyRole.Console,
        PolicyRole.Multimedia,
        PolicyRole.Communications
    ];

    public static async Task SetDefaultAndVerifyAsync(
        string deviceId,
        DataFlow dataFlow,
        CancellationToken cancellationToken)
    {
        Exception? primaryFailure = null;
        try
        {
            SetDefaultWithCurrentPolicy(deviceId);
            if (await WaitForDefaultAsync(deviceId, dataFlow, cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            primaryFailure = exception;
        }

        try
        {
            SetDefaultWithVistaPolicy(deviceId);
            if (await WaitForDefaultAsync(deviceId, dataFlow, cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Windows 拒绝了默认音频设备切换。",
                primaryFailure is null
                    ? exception
                    : new AggregateException(primaryFailure, exception));
        }

        throw new InvalidOperationException(
            $"Windows 未将请求的设备设为默认设备：{deviceId}",
            primaryFailure);
    }

    private static void SetDefaultWithCurrentPolicy(string deviceId)
    {
        Type policyConfigType = Type.GetTypeFromCLSID(
            new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"),
            throwOnError: true)!;
        var policyConfig = (IPolicyConfig)Activator.CreateInstance(policyConfigType)!;
        try
        {
            foreach (PolicyRole role in Roles)
            {
                Marshal.ThrowExceptionForHR(
                    policyConfig.SetDefaultEndpoint(deviceId, role));
            }
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(policyConfig);
        }
    }

    private static void SetDefaultWithVistaPolicy(string deviceId)
    {
        Type policyConfigType = Type.GetTypeFromCLSID(
            new Guid("294935CE-F637-4E7C-A41B-AB255460B862"),
            throwOnError: true)!;
        var policyConfig = (IPolicyConfigVista)Activator.CreateInstance(policyConfigType)!;
        try
        {
            foreach (PolicyRole role in Roles)
            {
                Marshal.ThrowExceptionForHR(
                    policyConfig.SetDefaultEndpoint(deviceId, role));
            }
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(policyConfig);
        }
    }

    private static async Task<bool> WaitForDefaultAsync(
        string deviceId,
        DataFlow dataFlow,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDefaultForAllRoles(deviceId, dataFlow))
            {
                return true;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static bool IsDefaultForAllRoles(string deviceId, DataFlow dataFlow)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (PolicyRole role in Roles)
        {
            var naudioRole = (Role)(int)role;
            if (!enumerator.HasDefaultAudioEndpoint(dataFlow, naudioRole))
            {
                return false;
            }
            using MMDevice endpoint = enumerator.GetDefaultAudioEndpoint(dataFlow, naudioRole);
            if (!string.Equals(endpoint.ID, deviceId, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
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

    [ComImport]
    [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
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
