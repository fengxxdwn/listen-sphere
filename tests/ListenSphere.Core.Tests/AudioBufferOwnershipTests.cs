using System.Net;
using System.Net.Sockets;
using ListenSphere.Network;
using ListenSphere.Protocol;
using Xunit;

namespace ListenSphere.Core.Tests;

[Collection("Audio performance")]
public sealed class AudioBufferOwnershipTests(ITestOutputHelper output)
{
    [Fact]
    public void ProtectIntoCallerStorageMatchesContractAndDoesNotTouchTail()
    {
        var session = AudioAllocationTests.Session();
        var header = AudioPacketHeader.CreateDefault(session.SessionId, session.StreamId);
        byte[] pcm = Enumerable.Range(0, 1124).Select(i => (byte)i).ToArray();
        byte[] expected = AudioPayloadProtector.Protect(header, pcm, session.Key, session.Salt);
        byte[] storage = Enumerable.Repeat((byte)0xcc, 1300).ToArray();
        int length = AudioPayloadProtector.Protect(header, pcm, session.Key, session.Salt, storage);
        Assert.Equal(expected, storage[..length]);
        Assert.All(storage[length..], value => Assert.Equal(0xcc, value));
        Assert.Throws<ArgumentException>(() => AudioPayloadProtector.Protect(header, pcm, session.Key, session.Salt, new byte[1]));
    }

    [Fact]
    public void ReusableJitterStagingKeepsOwnedFramesAndBoundedAllocation()
    {
        var session = AudioAllocationTests.Session();
        var jitter = new AudioJitterBuffer(session);
        var staging = new List<NetworkAudioFrame>(16);
        var pcm = new byte[3840];
        void Push(uint sequence) => jitter.Push(new(session.SessionId, session.StreamId,
            sequence, sequence * 480UL, pcm, false), staging);
        for (uint i = 0; i < 100; i++) Push(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (uint i = 100; i < 2100; i++) Push(i);
        double allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 2000d;
        output.WriteLine($"Receiver reusable jitter staging: {allocated:F2} bytes/frame");
        Assert.InRange(allocated, 0, 160);
        var retained = staging.ToArray();
        Push(2100);
        Assert.All(retained, frame => Assert.Same(pcm, frame.Pcm));
        // A gap must not make independent silence frames share mutable storage.
        var silence = new List<NetworkAudioFrame>();
        for (uint i = 2102; i < 2160; i += 2)
        {
            Push(i);
            silence.AddRange(staging.Where(frame => frame.IsConcealment));
        }
        Assert.True(silence.Count > 1);
        silence[0].Pcm[0] = 99;
        Assert.NotSame(silence[0].Pcm, silence[1].Pcm);
        Assert.Equal(0, silence[1].Pcm[0]);
    }

    [Fact]
    public async Task ReusedSendBufferPreservesEveryFragmentAcrossConcurrentCalls()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var session = AudioAllocationTests.Session(((IPEndPoint)socket.Client.LocalEndPoint!).Port);
        await using var sender = new UdpAudioSender(session);
        var pcmA = Enumerable.Repeat((byte)0x11, 3840).ToArray();
        var pcmB = Enumerable.Repeat((byte)0x22, 3840).ToArray();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var sends = Task.WhenAll(sender.SendFrameAsync(pcmA, 0, timeout.Token).AsTask(),
            sender.SendFrameAsync(pcmB, 480, timeout.Token).AsTask());
        var frames = new Dictionary<uint, List<byte>>();
        var sequences = new HashSet<uint>();
        for (int i = 0; i < 8; i++)
        {
            var packet = await socket.ReceiveAsync(timeout.Token);
            Assert.True(AudioPayloadProtector.TryUnprotect(packet.Buffer, session.Key, session.Salt, out var header, out var payload));
            Assert.True(sequences.Add(header.PacketSequence));
            if (!frames.TryGetValue(header.FrameSequence, out var bytes)) frames.Add(header.FrameSequence, bytes = []);
            bytes.AddRange(payload);
        }
        await sends;
        Assert.Equal(pcmA, frames[0]);
        Assert.Equal(pcmB, frames[1]);
    }
}
