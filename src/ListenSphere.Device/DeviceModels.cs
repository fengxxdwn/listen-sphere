namespace ListenSphere.Device;

[Flags]
public enum DeviceCapabilities : ulong
{
    None = 0,
    Controller = 1UL << 0,
    AudioSend = 1UL << 1,
    AudioReceive = 1UL << 2,
    RemoteControl = 1UL << 3
}

public enum DevicePlatform
{
    Unknown = 0,
    Windows = 1,
    Android = 2,
    IOS = 3,
    MacOS = 4
}

public enum DeviceConnectionState
{
    Offline = 0,
    Discovered = 1,
    Pairing = 2,
    Connecting = 3,
    Connected = 4,
    Streaming = 5,
    Faulted = 6
}

public sealed record DeviceDescriptor(
    Guid DeviceId,
    string DisplayName,
    DevicePlatform Platform,
    DeviceCapabilities Capabilities,
    ushort ProtocolMajor,
    ushort ProtocolMinor);

public sealed record DeviceStatus(
    DeviceDescriptor Device,
    DeviceConnectionState ConnectionState,
    bool IsPaired,
    DateTimeOffset LastSeen);

