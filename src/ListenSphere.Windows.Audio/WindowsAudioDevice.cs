using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Windows.Audio;

public sealed record WindowsAudioDevice(
    string Id,
    string DisplayName,
    bool IsDefault,
    bool IsBluetooth = false) : IAudioDevice
{
    public string DisplayLabel => IsDefault ? $"{DisplayName}（默认）" : DisplayName;
}
