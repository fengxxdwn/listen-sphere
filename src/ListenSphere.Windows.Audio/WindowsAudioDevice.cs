using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Windows.Audio;

public sealed record WindowsAudioDevice(
    string Id,
    string DisplayName,
    bool IsDefault) : IAudioDevice
{
    public string DisplayLabel => IsDefault ? $"{DisplayName}（默认）" : DisplayName;
}
