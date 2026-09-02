using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListenSphere.Audio.Engine;
using ListenSphere.Device;
using ListenSphere.Network;
using ListenSphere.Protocol;
using ListenSphere.Windows.Bluetooth;

namespace ListenSphere.Controller;

public sealed class AdditionalOutputRouteItemViewModel
{
    public AdditionalOutputRouteItemViewModel(
        Guid channelId,
        string deviceId,
        string sourceName,
        bool isActive,
        Func<Guid, string, Task> remove)
    {
        ChannelId = channelId;
        DeviceId = deviceId;
        SourceName = sourceName;
        IsActive = isActive;
        RemoveCommand = new AsyncRelayCommand(() => remove(ChannelId, DeviceId));
    }

    public Guid ChannelId { get; }
    public string DeviceId { get; }
    public string SourceName { get; }
    public bool IsActive { get; }
    public string StatusText => IsActive ? "正在输出" : "等待设备恢复";
    public AsyncRelayCommand RemoveCommand { get; }
}
