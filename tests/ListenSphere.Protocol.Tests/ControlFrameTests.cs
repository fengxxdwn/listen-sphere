using System.Buffers.Binary;
using Google.Protobuf;
using ListenSphere.Protocol.V1;
using Xunit;

namespace ListenSphere.Protocol.Tests;

public sealed class ControlFrameTests
{
    [Fact]
    public async Task Frame_UsesBigEndianLengthAndRoundTrips()
    {
        var message = ProtocolConstants.CreateEnvelope(42);
        message.Heartbeat = new Heartbeat { MonotonicMilliseconds = 1234 };
        await using var stream = new MemoryStream();

        await ControlFrameCodec.WriteAsync(
            stream,
            message,
            TestContext.Current.CancellationToken);

        byte[] bytes = stream.ToArray();
        Assert.Equal(
            bytes.Length - ControlFrameCodec.PrefixLength,
            BinaryPrimitives.ReadInt32BigEndian(bytes));
        stream.Position = 0;
        Envelope? actual = await ControlFrameCodec.ReadAsync(
            stream,
            TestContext.Current.CancellationToken);
        Assert.NotNull(actual);
        Assert.Equal(42UL, actual.RequestId);
        Assert.Equal(1234UL, actual.Heartbeat.MonotonicMilliseconds);
    }

    [Fact]
    public async Task Frame_RejectsInvalidAndTruncatedLengths()
    {
        await using var oversized = new MemoryStream(new byte[] { 0x00, 0x10, 0x00, 0x01 });
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await ControlFrameCodec.ReadAsync(
                oversized,
                TestContext.Current.CancellationToken));

        await using var truncated = new MemoryStream(
            new byte[] { 0x00, 0x00, 0x00, 0x02, 0x08 });
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await ControlFrameCodec.ReadAsync(
                truncated,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void HelloRequest_RoundTripsMicrophoneSourceIdentity()
    {
        var request = new HelloRequest
        {
            SourceId = "android-default",
            SourceName = "手机麦克风",
            SourceKind = "microphone"
        };

        HelloRequest actual = HelloRequest.Parser.ParseFrom(request.ToByteArray());

        Assert.Equal("android-default", actual.SourceId);
        Assert.Equal("手机麦克风", actual.SourceName);
        Assert.Equal("microphone", actual.SourceKind);
    }

}
