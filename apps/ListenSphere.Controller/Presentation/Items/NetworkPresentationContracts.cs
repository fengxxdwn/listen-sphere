using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListenSphere.Audio.Engine;
using ListenSphere.Device;
using ListenSphere.Network;
using ListenSphere.Protocol;
using ListenSphere.Windows.Bluetooth;

namespace ListenSphere.Controller;

public sealed record EqualizerPresetOption(string Name, float[]? Gains);

public enum RemoteTransportMode
{
    Wireless,
    Bluetooth,
    Wired
}
