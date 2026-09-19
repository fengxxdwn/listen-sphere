using System.Net;
using ListenSphere.Network;
using ListenSphere.Protocol;
using Xunit;

namespace ListenSphere.Core.Tests;

public sealed class NetworkDecompositionTests
{
    private static AudioSessionParameters Session() => new(
        Guid.NewGuid(), 42, Enumerable.Repeat((byte)7, 32).ToArray(),
        new byte[4], new IPEndPoint(IPAddress.Loopback, 1));

    [Fact]
    public void Reassembler_ReordersFragmentsAndIgnoresDuplicate()
    {
        var session = Session();
        var reassembler = new AudioFrameReassembler(session);
        var first = AudioPacketHeader.CreateDefault(session.SessionId, session.StreamId) with
        { FragmentCount = 2, FragmentIndex = 0 };
        var second = first with { FragmentIndex = 1 };
        byte[] a = Enumerable.Repeat((byte)1, 1920).ToArray();
        byte[] b = Enumerable.Repeat((byte)2, 1920).ToArray();
        Assert.Null(reassembler.Add(second, b).Frame);
        Assert.Null(reassembler.Add(second, b).Frame);
        var frame = reassembler.Add(first, a).Frame;
        Assert.NotNull(frame);
        Assert.Equal(a.Concat(b), frame.Pcm);
    }

    [Fact]
    public async Task Reassembler_ExpiresMissingFragmentWithoutPublishingPartialAudio()
    {
        var session = Session();
        var reassembler = new AudioFrameReassembler(session);
        var header = AudioPacketHeader.CreateDefault(session.SessionId, session.StreamId) with
        { FragmentCount = 2, FragmentIndex = 0 };
        Assert.Null(reassembler.Add(header, new byte[1920]).Frame);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        var result = reassembler.Add(header with { FrameSequence = 1 }, new byte[1920]);
        Assert.Null(result.Frame);
        Assert.Equal(1, result.ExpiredFrames);
    }

    [Fact]
    public async Task Sender_CancellationDoesNotSend_AndDisposeClearsKey()
    {
        var session = Session();
        var sender = new UdpAudioSender(session);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await sender.SendFrameAsync(new byte[3840], 0, cancellation.Token));
            Assert.Equal(0, sender.Statistics.FramesSent);
        }
        finally { await sender.DisposeAsync(); }
        Assert.All(session.Key, value => Assert.Equal(0, value));
        await sender.DisposeAsync();
    }
}
