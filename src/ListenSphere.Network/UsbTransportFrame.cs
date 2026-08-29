using System.Buffers.Binary;

namespace ListenSphere.Network;

/// <summary>
/// Multiplexes ListenSphere control and audio payloads over a reliable USB bulk stream.
/// This framing is transport-only; device identity and pairing remain ListenSphere Protocol concerns.
/// </summary>
public static class UsbTransportFrame
{
    public const int HeaderLength = 24;
    public const byte ProtocolVersion = 1;
    public const int MaximumPayloadLength = 64 * 1024;

    private static ReadOnlySpan<byte> Magic => "LSUB"u8;

    public static byte[] Encode(
        UsbTransportFrameKind kind,
        ReadOnlySpan<byte> payload,
        uint sequence,
        ulong timestamp = 0,
        ushort flags = 0)
    {
        if (!Enum.IsDefined(kind) || kind == UsbTransportFrameKind.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (payload.Length > MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        byte[] frame = new byte[HeaderLength + payload.Length];
        Magic.CopyTo(frame);
        frame[4] = ProtocolVersion;
        frame[5] = (byte)kind;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12), sequence);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(16), timestamp);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static bool TryReadHeader(
        ReadOnlySpan<byte> header,
        out UsbTransportFrameHeader value)
    {
        value = default;
        if (header.Length < HeaderLength ||
            !header[..4].SequenceEqual(Magic) ||
            header[4] != ProtocolVersion ||
            !Enum.IsDefined(typeof(UsbTransportFrameKind), header[5]) ||
            header[5] == (byte)UsbTransportFrameKind.Unspecified)
        {
            return false;
        }

        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (payloadLength > MaximumPayloadLength)
        {
            return false;
        }

        value = new UsbTransportFrameHeader(
            (UsbTransportFrameKind)header[5],
            BinaryPrimitives.ReadUInt16BigEndian(header[6..]),
            checked((int)payloadLength),
            BinaryPrimitives.ReadUInt32BigEndian(header[12..]),
            BinaryPrimitives.ReadUInt64BigEndian(header[16..]));
        return true;
    }
}

public enum UsbTransportFrameKind : byte
{
    Unspecified = 0,
    Control = 1,
    Audio = 2,
    Status = 3,
    KeepAlive = 4
}

public readonly record struct UsbTransportFrameHeader(
    UsbTransportFrameKind Kind,
    ushort Flags,
    int PayloadLength,
    uint Sequence,
    ulong Timestamp);
