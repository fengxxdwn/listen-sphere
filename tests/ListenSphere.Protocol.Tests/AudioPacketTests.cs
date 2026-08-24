using System.Security.Cryptography;
using Google.Protobuf;
using ListenSphere.Protocol.V1;
using Xunit;

namespace ListenSphere.Protocol.Tests;

public sealed class AudioPacketTests
{
    private static readonly Guid SessionId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    [Fact]
    public void Header_WritesThePortableGoldenVector()
    {
        var header = CreateHeader();
        Span<byte> bytes = stackalloc byte[AudioPacketHeader.Size];
        header.Write(bytes);
        var expectedHex = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "vectors", "audio-header-v1.hex")).Trim();

        Assert.Equal(AudioPacketHeader.Size, bytes.Length);
        Assert.Equal(expectedHex, Convert.ToHexStringLower(bytes));
    }

    [Fact]
    public void Datagram_RoundTripsAllFields()
    {
        var header = CreateHeader() with { PayloadLength = 3 };
        var datagram = new byte[AudioPacketHeader.Size + 3];
        header.Write(datagram);
        new byte[] { 1, 2, 3 }.CopyTo(datagram, AudioPacketHeader.Size);

        var success = AudioPacketHeader.TryReadDatagram(
            datagram, out var actual, out var payload, out var error);

        Assert.True(success);
        Assert.Equal(AudioPacketReadError.None, error);
        Assert.Equal(header, actual);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload.ToArray());
    }

    [Fact]
    public void Datagram_RejectsUnknownMajorVersionAndBadLength()
    {
        var bytes = new byte[AudioPacketHeader.Size];
        CreateHeader().Write(bytes);
        bytes[4] = 2;

        Assert.False(AudioPacketHeader.TryReadDatagram(bytes, out _, out _, out var versionError));
        Assert.Equal(AudioPacketReadError.UnsupportedMajorVersion, versionError);

        CreateHeader().Write(bytes);
        bytes[58] = 1;
        Assert.False(AudioPacketHeader.TryReadDatagram(bytes, out _, out _, out var lengthError));
        Assert.Equal(AudioPacketReadError.PayloadLengthMismatch, lengthError);
    }

    [Fact]
    public void Fragmenter_StaysUnderMtuAndReassemblesPcmFrame()
    {
        var frame = Enumerable.Range(0, 3_840).Select(index => (byte)(index % 251)).ToArray();
        var fragments = AudioPacketFragmenter.Fragment(frame, CreateHeader());
        using var reassembled = new MemoryStream();

        Assert.Equal(4, fragments.Count);
        foreach (var datagram in fragments)
        {
            Assert.InRange(
                datagram.Length + AudioPayloadProtector.TagSize,
                AudioPacketHeader.Size + AudioPayloadProtector.TagSize,
                AudioPacketHeader.MaximumDatagramSize);
            Assert.True(AudioPacketHeader.TryReadDatagram(
                datagram, out var header, out var payload, out _));
            Assert.Equal((byte)fragments.Count, header.FragmentCount);
            reassembled.Write(payload);
        }

        Assert.Equal(frame, reassembled.ToArray());
    }

    [Fact]
    public void SequenceNumber_PreservesUnsignedWrapBoundary()
    {
        var header = CreateHeader() with { PacketSequence = uint.MaxValue };
        var bytes = new byte[AudioPacketHeader.Size];
        header.Write(bytes);

        Assert.True(AudioPacketHeader.TryReadDatagram(bytes, out var actual, out _, out _));
        Assert.Equal(uint.MaxValue, actual.PacketSequence);
        Assert.Equal(0U, unchecked(actual.PacketSequence + 1));
    }

    [Fact]
    public void Aead_RejectsTamperedCiphertext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(AudioPayloadProtector.SaltSize);
        var datagram = AudioPayloadProtector.Protect(
            CreateHeader(),
            "listen-sphere"u8,
            key,
            salt);

        Assert.True(AudioPayloadProtector.TryUnprotect(
            datagram, key, salt, out _, out var plaintext));
        Assert.Equal("listen-sphere"u8.ToArray(), plaintext);

        datagram[AudioPacketHeader.Size] ^= 0x01;
        Assert.False(AudioPayloadProtector.TryUnprotect(
            datagram, key, salt, out _, out var rejectedPlaintext));
        Assert.Empty(rejectedPlaintext);
    }

    [Fact]
    public void Protobuf_RetainsUnknownFieldsAcrossRoundTrip()
    {
        var envelope = new Envelope
        {
            Version = new ProtocolVersion { Major = 1, Minor = 0 },
            RequestId = 42,
            Heartbeat = new Heartbeat { MonotonicMilliseconds = 123 }
        };
        var knownBytes = envelope.ToByteArray();
        var withUnknownField = knownBytes.Concat(new byte[] { 0x98, 0x06, 0x01 }).ToArray();

        var parsed = Envelope.Parser.ParseFrom(withUnknownField);
        var roundTripped = parsed.ToByteArray();

        Assert.Equal(1U, parsed.Version.Major);
        Assert.Equal(42UL, parsed.RequestId);
        Assert.True(roundTripped.Length > knownBytes.Length);
    }

    private static AudioPacketHeader CreateHeader() =>
        AudioPacketHeader.CreateDefault(SessionId, 0x01020304) with
        {
            PacketSequence = 0xFFFFFFFE,
            FrameSequence = 0x0A0B0C0D,
            Timestamp = 0x0102030405060708
        };
}
