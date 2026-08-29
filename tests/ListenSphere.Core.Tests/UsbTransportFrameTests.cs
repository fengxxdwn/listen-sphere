using ListenSphere.Network;
using Xunit;

namespace ListenSphere.Core.Tests;

public sealed class UsbTransportFrameTests
{
    [Fact]
    public void Header_IsFixedLengthAndBigEndian()
    {
        byte[] frame = UsbTransportFrame.Encode(
            UsbTransportFrameKind.Audio,
            [1, 2, 3],
            0xfedcba98,
            0x0102030405060708,
            0x1020);

        Assert.Equal(UsbTransportFrame.HeaderLength + 3, frame.Length);
        Assert.Equal("LSUB"u8.ToArray(), frame[..4]);
        Assert.Equal(3, frame[11]);
        Assert.True(UsbTransportFrame.TryReadHeader(frame, out UsbTransportFrameHeader header));
        Assert.Equal(UsbTransportFrameKind.Audio, header.Kind);
        Assert.Equal(0x1020, header.Flags);
        Assert.Equal(3, header.PayloadLength);
        Assert.Equal(0xfedcba98u, header.Sequence);
        Assert.Equal(0x0102030405060708ul, header.Timestamp);
    }

    [Fact]
    public void Header_RejectsOversizedPayloadAndUnknownVersion()
    {
        byte[] frame = UsbTransportFrame.Encode(UsbTransportFrameKind.Control, [], 1);
        frame[4] = 2;
        Assert.False(UsbTransportFrame.TryReadHeader(frame, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => UsbTransportFrame.Encode(
            UsbTransportFrameKind.Audio,
            new byte[UsbTransportFrame.MaximumPayloadLength + 1],
            1));
    }
}
