using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ListenSphere.Network;
using ListenSphere.Protocol;
using Xunit;

namespace ListenSphere.Core.Tests;

public sealed class UdpAudioTransportTests
{
    [Fact]
    public void PacketSequenceTracker_HandlesGapsLatePacketsDuplicatesAndWrap()
    {
        var tracker = new PacketSequenceTracker();

        Assert.Equal(default, tracker.Observe(uint.MaxValue - 1));
        Assert.Equal(default, tracker.Observe(uint.MaxValue));
        Assert.Equal(default, tracker.Observe(0));
        PacketSequenceObservation gap = tracker.Observe(3);
        Assert.Equal(2, gap.LostDelta);
        Assert.False(gap.IsDuplicate);
        PacketSequenceObservation late = tracker.Observe(1);
        Assert.Equal(-1, late.LostDelta);
        Assert.True(late.IsLate);
        Assert.True(tracker.Observe(1).IsDuplicate);
    }

    [Fact]
    public async Task EncryptedUdp_RoundTripsFragmentedFramesThroughJitterBuffer()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] salt = RandomNumberGenerator.GetBytes(4);
        Guid sessionId = Guid.NewGuid();
        const uint streamId = 42;
        await using var receiver = new UdpAudioReceiver();
        await receiver.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        receiver.RegisterSession(new AudioSessionParameters(
            sessionId,
            streamId,
            key.ToArray(),
            salt.ToArray(),
            new IPEndPoint(IPAddress.Loopback, 0)));
        await using var sender = new UdpAudioSender(new AudioSessionParameters(
            sessionId,
            streamId,
            key.ToArray(),
            salt.ToArray(),
            new IPEndPoint(IPAddress.Loopback, receiver.Port)));
        byte[] frame = Enumerable.Range(0, 3_840)
            .Select(index => (byte)(index % 251))
            .ToArray();

        for (ulong timestamp = 0; timestamp < 4 * 480; timestamp += 480)
        {
            await sender.SendFrameAsync(
                frame,
                timestamp,
                TestContext.Current.CancellationToken);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<NetworkAudioFrame> frames = receiver
            .ReadAllAsync(timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await frames.MoveNextAsync());
        NetworkAudioFrame first = frames.Current;
        Assert.Equal(0U, first.FrameSequence);
        Assert.Equal(0UL, first.Timestamp);
        Assert.Equal(frame, first.Pcm);
        Assert.False(first.IsConcealment);

        Assert.True(await frames.MoveNextAsync());
        Assert.Equal(1U, frames.Current.FrameSequence);
        Assert.Equal(480UL, frames.Current.Timestamp);

        UdpAudioSenderStatistics sent = sender.Statistics;
        UdpAudioReceiverStatistics received = receiver.Statistics;
        Assert.Equal(4, sent.FramesSent);
        Assert.Equal(16, sent.DatagramsSent);
        Assert.Equal(4, received.FramesCompleted);
        Assert.Equal(0, received.AuthenticationFailures);
    }

    [Fact]
    public void JitterBuffer_InsertsSilenceForAFrameMissingBeyondTarget()
    {
        var session = new AudioSessionParameters(
            Guid.NewGuid(),
            7,
            new byte[32],
            new byte[4],
            new IPEndPoint(IPAddress.Loopback, 1));
        var jitter = new AudioJitterBuffer(session);

        Assert.Empty(jitter.Push(CreateFrame(session, 0)));
        Assert.Empty(jitter.Push(CreateFrame(session, 2)));
        IReadOnlyList<NetworkAudioFrame> first = jitter.Push(CreateFrame(session, 3));
        IReadOnlyList<NetworkAudioFrame> second = jitter.Push(CreateFrame(session, 4));

        Assert.Single(first);
        Assert.Equal(0U, first[0].FrameSequence);
        Assert.Equal(2, second.Count);
        Assert.Equal(1U, second[0].FrameSequence);
        Assert.True(second[0].IsConcealment);
        Assert.All(second[0].Pcm, value => Assert.Equal(0, value));
        Assert.Equal(2U, second[1].FrameSequence);
        Assert.False(second[1].IsConcealment);
    }

    [Fact]
    public async Task Receiver_RejectsTamperedAuthenticatedPayloadBeforeReassembly()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] salt = RandomNumberGenerator.GetBytes(4);
        Guid sessionId = Guid.NewGuid();
        const uint streamId = 99;
        await using var receiver = new UdpAudioReceiver();
        await receiver.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        receiver.RegisterSession(new AudioSessionParameters(
            sessionId,
            streamId,
            key.ToArray(),
            salt.ToArray(),
            new IPEndPoint(IPAddress.Loopback, 0)));
        byte[] datagram = AudioPayloadProtector.Protect(
            AudioPacketHeader.CreateDefault(sessionId, streamId),
            new byte[100],
            key,
            salt);
        datagram[AudioPacketHeader.Size] ^= 0x80;
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(IPAddress.Loopback, receiver.Port);

        await udp.SendAsync(datagram, TestContext.Current.CancellationToken);
        for (int attempt = 0;
             attempt < 50 && receiver.Statistics.AuthenticationFailures == 0;
             attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, receiver.Statistics.AuthenticationFailures);
        Assert.Equal(0, receiver.Statistics.FramesCompleted);
    }

    private static NetworkAudioFrame CreateFrame(
        AudioSessionParameters session,
        uint sequence) =>
        new(
            session.SessionId,
            session.StreamId,
            sequence,
            sequence * 480UL,
            new byte[3_840],
            false);
}
