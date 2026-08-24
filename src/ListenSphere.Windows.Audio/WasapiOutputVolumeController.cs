using ListenSphere.Audio.Abstractions;
using NAudio.CoreAudioApi;

namespace ListenSphere.Windows.Audio;

/// <summary>Controls the Windows endpoint master volume for the selected render device.</summary>
public sealed class WasapiOutputVolumeController : IAudioOutputVolumeController
{
    public ValueTask<float> GetVolumeAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        return ValueTask.FromResult(
            Math.Clamp(device.AudioEndpointVolume.MasterVolumeLevelScalar, 0f, 1f));
    }

    public ValueTask SetVolumeAsync(
        string deviceId,
        float volume,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
        return ValueTask.CompletedTask;
    }
}
