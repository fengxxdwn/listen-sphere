using ListenSphere.Protocol.V1;

namespace ListenSphere.Protocol;

public static class ProtocolConstants
{
    public const ushort MajorVersion = 1;
    public const ushort MinorVersion = 1;

    public static ProtocolVersion CurrentVersion =>
        new() { Major = MajorVersion, Minor = MinorVersion };

    public static Envelope CreateEnvelope(ulong requestId) =>
        new()
        {
            Version = CurrentVersion,
            RequestId = requestId
        };

    public static bool IsCompatible(ProtocolVersion? version) =>
        version is not null && version.Major == MajorVersion;
}
