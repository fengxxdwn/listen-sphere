using System.Net;
using System.Net.Sockets;
using ListenSphere.Audio.Engine;
using ListenSphere.Network;
using ListenSphere.Protocol;
using Xunit;

namespace ListenSphere.Core.Tests;

[CollectionDefinition("Audio performance", DisableParallelization = true)]
public sealed class AudioPerformanceCollection;

[Collection("Audio performance")]
public sealed class AudioAllocationTests(ITestOutputHelper output)
{
    internal static AudioSessionParameters Session(int port = 1) => new(
        Guid.NewGuid(), 42, Enumerable.Repeat((byte)7, 32).ToArray(),
        new byte[4], new IPEndPoint(IPAddress.Loopback, port));

    private double Measure(string name, Action action)
    {
        for (int i = 0; i < 100; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) action();
        double bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 2000d;
        output.WriteLine($"{name}: {bytes:F2} bytes/frame");
        return bytes;
    }

    [Fact]
    public void ReassemblyAllocation()
    {
        var session = Session();
        var reassembler = new AudioFrameReassembler(session);
        var header = AudioPacketHeader.CreateDefault(session.SessionId, session.StreamId) with { FragmentCount = 4 };
        var payloads = new[] { new byte[1124], new byte[1124], new byte[1124], new byte[468] };
        uint sequence = 0;
        double allocated = Measure("Reassembly (owned output; input excluded)", () =>
        {
            for (byte i = 0; i < 4; i++)
                reassembler.Add(header with { FrameSequence = sequence, FragmentIndex = i }, payloads[i]);
            sequence++;
        });
        Assert.InRange(allocated, 3840, 4500);
    }

    [Fact]
    public void ReceiveDecryptionAllocation()
    {
        var session = Session();
        var header = AudioPacketHeader.CreateDefault(session.SessionId, session.StreamId);
        var datagram = AudioPayloadProtector.Protect(header, new byte[1124], session.Key, session.Salt);
        double allocated = Measure("Decrypt (one owned 1124-byte fragment)", () =>
            AudioPayloadProtector.TryUnprotect(datagram, session.Key, session.Salt, out _, out _));
        Assert.InRange(allocated, 1124, 3000);
    }

    [Fact]
    public void JitterAndSilenceAllocation()
    {
        var session = Session();
        var buffer = new AudioJitterBuffer(session);
        var pcm = new byte[3840];
        uint sequence = 0;
        double regular = Measure("Jitter regular (input record included)", () =>
        {
            buffer.Push(new(session.SessionId, session.StreamId, sequence, sequence * 480UL, pcm, false));
            sequence++;
        });
        // Deliberately keep missing every other frame; silence must remain independently owned.
        double gaps = Measure("Jitter missing every other frame (owned silence included)", () =>
        {
            buffer.Push(new(session.SessionId, session.StreamId, sequence, sequence * 480UL, pcm, false));
            sequence += 2;
        });
        Assert.InRange(regular, 0, 1500);
        Assert.InRange(gaps, 3840, 15000);
    }

    [Fact]
    public void MixerHasNoSteadyStateFrameAllocation()
    {
        var mixer = new RemotePcmMixer(3840, startupFrames: 1);
        var id = Guid.NewGuid();
        mixer.RegisterStream(id);
        var pcm = new byte[3840];
        var destination = new byte[3840];
        double allocated = Measure("Mixer enqueue and mix", () =>
        {
            mixer.Enqueue(id, pcm);
            mixer.TryMixNext(destination);
        });
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task SenderAllocation()
    {
        using var drain = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var session = Session(((IPEndPoint)drain.Client.LocalEndPoint!).Port);
        await using var sender = new UdpAudioSender(session);
        var pcm = new byte[3840];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        async Task Frame(uint i)
        {
            await sender.SendFrameAsync(pcm, i * 480UL, timeout.Token);
            for (int j = 0; j < 4; j++) await drain.ReceiveAsync(timeout.Token);
        }
        for (uint i = 0; i < 100; i++) await Frame(i);
        long before = GC.GetTotalAllocatedBytes(true);
        for (uint i = 100; i < 1100; i++) await Frame(i);
        double allocated = (GC.GetTotalAllocatedBytes(true) - before) / 1000d;
        output.WriteLine($"Sender + raw UDP drain (process-wide): {allocated:F2} bytes/frame");
        Assert.InRange(allocated, 0, 9000);
        Assert.Equal(4400, sender.Statistics.DatagramsSent);
    }
}
