using System.Buffers.Binary;

namespace ListenSphere.Protocol;

[Flags]
public enum AudioPacketFlags : ushort
{
    None = 0,
    Encrypted = 1 << 0
}

public enum AudioCodec : byte
{
    PcmFloat32 = 1,
    Opus = 2,
    PcmInt16 = 3,
    ImaAdpcm = 4,
    AacLc = 5,
    Sbc = 6,
    Ldac = 7
}

public enum AudioPacketSampleFormat : byte
{
    Float32LittleEndian = 1,
    Int16LittleEndian = 2
}

public enum AudioPacketReadError
{
    None = 0,
    DatagramTooShort,
    InvalidMagic,
    UnsupportedMajorVersion,
    InvalidHeaderLength,
    InvalidFormat,
    InvalidFragment,
    PayloadLengthMismatch
}

/// <summary>The fixed 60-byte ListenSphere Protocol v1 audio datagram header.</summary>
public readonly record struct AudioPacketHeader
{
    public const int Size = 60;
    public const int MaximumDatagramSize = 1_200;
    public const byte CurrentMajorVersion = 1;
    public const byte CurrentMinorVersion = 0;

    private static ReadOnlySpan<byte> Magic => "LSPA"u8;

    public byte MajorVersion { get; init; }
    public byte MinorVersion { get; init; }
    public AudioPacketFlags Flags { get; init; }
    public AudioCodec Codec { get; init; }
    public AudioPacketSampleFormat SampleFormat { get; init; }
    public byte ChannelCount { get; init; }
    public uint SampleRate { get; init; }
    public ushort FrameSamples { get; init; }
    public byte FragmentIndex { get; init; }
    public byte FragmentCount { get; init; }
    public Guid SessionId { get; init; }
    public uint StreamId { get; init; }
    public uint PacketSequence { get; init; }
    public uint FrameSequence { get; init; }
    public ulong Timestamp { get; init; }
    public ushort PayloadLength { get; init; }

    public static AudioPacketHeader CreateDefault(Guid sessionId, uint streamId) => new()
    {
        MajorVersion = CurrentMajorVersion,
        MinorVersion = CurrentMinorVersion,
        Codec = AudioCodec.PcmFloat32,
        SampleFormat = AudioPacketSampleFormat.Float32LittleEndian,
        ChannelCount = 2,
        SampleRate = 48_000,
        FrameSamples = 480,
        FragmentCount = 1,
        SessionId = sessionId,
        StreamId = streamId
    };

    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"Destination must contain at least {Size} bytes.", nameof(destination));
        }

        Validate();
        destination[..Size].Clear();
        Magic.CopyTo(destination);
        destination[4] = MajorVersion;
        destination[5] = MinorVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], Size);
        destination[10] = (byte)Codec;
        destination[11] = (byte)SampleFormat;
        destination[12] = ChannelCount;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[14..], SampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[18..], FrameSamples);
        destination[20] = FragmentIndex;
        destination[21] = FragmentCount;
        if (!SessionId.TryWriteBytes(destination[22..38], bigEndian: true, out var bytesWritten) ||
            bytesWritten != 16)
        {
            throw new InvalidOperationException("Session ID could not be serialized.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[38..], StreamId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[42..], PacketSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[46..], FrameSequence);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[50..], Timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[58..], PayloadLength);
    }

    public static bool TryReadDatagram(
        ReadOnlySpan<byte> datagram,
        out AudioPacketHeader header,
        out ReadOnlySpan<byte> payload,
        out AudioPacketReadError error)
    {
        header = default;
        payload = default;
        if (datagram.Length < Size)
        {
            error = AudioPacketReadError.DatagramTooShort;
            return false;
        }

        if (!datagram[..4].SequenceEqual(Magic))
        {
            error = AudioPacketReadError.InvalidMagic;
            return false;
        }

        if (datagram[4] != CurrentMajorVersion)
        {
            error = AudioPacketReadError.UnsupportedMajorVersion;
            return false;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(datagram[8..]) != Size)
        {
            error = AudioPacketReadError.InvalidHeaderLength;
            return false;
        }

        header = new AudioPacketHeader
        {
            MajorVersion = datagram[4],
            MinorVersion = datagram[5],
            Flags = (AudioPacketFlags)BinaryPrimitives.ReadUInt16LittleEndian(datagram[6..]),
            Codec = (AudioCodec)datagram[10],
            SampleFormat = (AudioPacketSampleFormat)datagram[11],
            ChannelCount = datagram[12],
            SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(datagram[14..]),
            FrameSamples = BinaryPrimitives.ReadUInt16LittleEndian(datagram[18..]),
            FragmentIndex = datagram[20],
            FragmentCount = datagram[21],
            SessionId = new Guid(datagram[22..38], bigEndian: true),
            StreamId = BinaryPrimitives.ReadUInt32LittleEndian(datagram[38..]),
            PacketSequence = BinaryPrimitives.ReadUInt32LittleEndian(datagram[42..]),
            FrameSequence = BinaryPrimitives.ReadUInt32LittleEndian(datagram[46..]),
            Timestamp = BinaryPrimitives.ReadUInt64LittleEndian(datagram[50..]),
            PayloadLength = BinaryPrimitives.ReadUInt16LittleEndian(datagram[58..])
        };

        if (header.ChannelCount is 0 or > 8 ||
            header.SampleRate == 0 ||
            header.FrameSamples == 0 ||
            !Enum.IsDefined(header.Codec) ||
            !Enum.IsDefined(header.SampleFormat))
        {
            header = default;
            error = AudioPacketReadError.InvalidFormat;
            return false;
        }

        if (header.FragmentCount == 0 || header.FragmentIndex >= header.FragmentCount)
        {
            header = default;
            error = AudioPacketReadError.InvalidFragment;
            return false;
        }

        if (datagram.Length != Size + header.PayloadLength)
        {
            header = default;
            error = AudioPacketReadError.PayloadLengthMismatch;
            return false;
        }

        payload = datagram[Size..];
        error = AudioPacketReadError.None;
        return true;
    }

    private void Validate()
    {
        if (MajorVersion != CurrentMajorVersion)
        {
            throw new InvalidOperationException("Only ListenSphere Protocol major version 1 can be written.");
        }

        if (ChannelCount is 0 or > 8 || SampleRate == 0 || FrameSamples == 0)
        {
            throw new InvalidOperationException("The audio format is invalid.");
        }

        if (FragmentCount == 0 || FragmentIndex >= FragmentCount)
        {
            throw new InvalidOperationException("The fragment metadata is invalid.");
        }
    }
}
