using ListenSphere.Audio.Abstractions;
using NAudio.CoreAudioApi;

namespace ListenSphere.Windows.Audio;

/// <summary>Enumerates active Windows render endpoints using stable MMDevice IDs.</summary>
public sealed class WasapiAudioDeviceManager : IAudioDeviceManager
{
    public ValueTask<IReadOnlyList<IAudioDevice>> GetPlaybackDevicesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
        {
            using var defaultEndpoint =
                enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            defaultId = defaultEndpoint.ID;
        }

        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
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
                    string.Equals(endpoint.ID, defaultId, StringComparison.Ordinal)));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<IAudioDevice>>(devices);
    }
}
