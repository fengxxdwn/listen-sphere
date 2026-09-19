using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using ListenSphere.Protocol;

namespace ListenSphere.Network;

public sealed class UdpAudioReceiver : IAsyncDisposable
{
    private const int OutputCapacity = 128;
    private readonly ConcurrentDictionary<Guid, ReceiverSession> sessions = new();
    private readonly Channel<NetworkAudioFrame> output = Channel.CreateBounded<NetworkAudioFrame>(
        new BoundedChannelOptions(OutputCapacity)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource lifetime = new();
    private UdpClient? udp;
    private Task? receiveLoop;
    private long datagramsReceived;
    private long invalidDatagrams;
    private long authenticationFailures;
    private long duplicateDatagrams;
    private long expiredFrames;
    private long framesCompleted;
    private long concealmentFrames;
    private long estimatedLostDatagrams;
    private long lateDatagrams;
    private long outputOverflows;
    private int outputQueueDepth;

    public int Port { get; private set; }

    public UdpAudioReceiverStatistics Statistics
    {
        get
        {
            UdpAudioSessionStatistics[] sessionStatistics = SessionStatistics.ToArray();
            return new(
                Interlocked.Read(ref datagramsReceived),
                Interlocked.Read(ref invalidDatagrams),
                Interlocked.Read(ref authenticationFailures),
                Interlocked.Read(ref duplicateDatagrams),
                Interlocked.Read(ref expiredFrames),
                Interlocked.Read(ref framesCompleted),
                Interlocked.Read(ref concealmentFrames),
                Interlocked.Read(ref estimatedLostDatagrams),
                Interlocked.Read(ref lateDatagrams),
                Interlocked.Read(ref outputOverflows),
                Volatile.Read(ref outputQueueDepth),
                sessions.Count,
                sessionStatistics.Sum(session => session.BufferedFrames),
                sessionStatistics.Length == 0
                    ? 0
                    : sessionStatistics.Max(session => session.TargetBufferMilliseconds),
                sessionStatistics.Length == 0
                    ? 0
                    : sessionStatistics.Max(session => session.EstimatedJitterMilliseconds));
        }
    }

    public IReadOnlyList<UdpAudioSessionStatistics> SessionStatistics =>
        sessions.Values.Select(session => session.GetStatistics()).ToArray();

    public Task StartAsync(int port = 0, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        if (udp is not null)
        {
            throw new InvalidOperationException("The UDP audio receiver is already running.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        udp = new UdpClient(AddressFamily.InterNetworkV6);
        udp.Client.DualMode = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        Port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        receiveLoop = ReceiveLoopAsync(lifetime.Token);
        return Task.CompletedTask;
    }

    public void RegisterSession(AudioSessionParameters session)
    {
        session.Validate();
        sessions.AddOrUpdate(
            session.SessionId,
            _ => new ReceiverSession(session),
            (_, previous) =>
            {
                previous.Dispose();
                return new ReceiverSession(session);
            });
    }

    public void RemoveSession(Guid sessionId)
    {
        if (sessions.TryRemove(sessionId, out ReceiverSession? session))
        {
            session.Dispose();
        }
    }

    public async IAsyncEnumerable<NetworkAudioFrame> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (NetworkAudioFrame frame in
            output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref outputQueueDepth);
            yield return frame;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult received = await udp!.ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref datagramsReceived);
                ProcessDatagram(received.Buffer, received.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.Writer.TryComplete();
        }
        catch (SocketException exception) when (cancellationToken.IsCancellationRequested)
        {
            output.Writer.TryComplete(exception);
        }
        catch (Exception exception)
        {
            output.Writer.TryComplete(exception);
        }
    }

    private void ProcessDatagram(byte[] datagram, IPEndPoint remoteEndpoint)
    {
        if (!AudioPacketHeader.TryReadDatagram(
            datagram,
            out AudioPacketHeader untrustedHeader,
            out _,
            out _))
        {
            Interlocked.Increment(ref invalidDatagrams);
            return;
        }

        if (!sessions.TryGetValue(untrustedHeader.SessionId, out ReceiverSession? session) ||
            untrustedHeader.StreamId != session.Parameters.StreamId ||
            !AddressesEqual(remoteEndpoint.Address, session.Parameters.RemoteEndpoint.Address))
        {
            Interlocked.Increment(ref invalidDatagrams);
            return;
        }

        if (!AudioPayloadProtector.TryUnprotect(
            datagram,
            session.Parameters.Key,
            session.Parameters.Salt,
            out AudioPacketHeader header,
            out byte[] plaintext))
        {
            Interlocked.Increment(ref authenticationFailures);
            return;
        }

        PacketSequenceObservation packet = session.PacketTracker.Observe(header.PacketSequence);
        if (packet.IsDuplicate)
        {
            Interlocked.Increment(ref duplicateDatagrams);
            return;
        }

        Interlocked.Add(ref estimatedLostDatagrams, packet.LostDelta);
        session.RecordDatagram(packet);
        if (packet.IsLate)
        {
            Interlocked.Increment(ref lateDatagrams);
        }

        if (header.Codec != AudioCodec.PcmFloat32 ||
            header.SampleFormat != AudioPacketSampleFormat.Float32LittleEndian ||
            header.SampleRate != session.Parameters.SampleRate ||
            header.ChannelCount != session.Parameters.ChannelCount ||
            header.FrameSamples != session.Parameters.FrameSamples)
        {
            Interlocked.Increment(ref invalidDatagrams);
            return;
        }

        ReassemblyResult result = session.Reassembler.Add(header, plaintext);
        Interlocked.Add(ref expiredFrames, result.ExpiredFrames);
        if (result.Frame is null)
        {
            return;
        }

        Interlocked.Increment(ref framesCompleted);
        session.RecordCompletedFrame();
        foreach (NetworkAudioFrame frame in session.JitterBuffer.Push(result.Frame))
        {
            if (frame.IsConcealment)
            {
                Interlocked.Increment(ref concealmentFrames);
                session.RecordConcealmentFrame();
            }

            if (output.Writer.TryWrite(frame))
            {
                Interlocked.Increment(ref outputQueueDepth);
            }
            else
            {
                Interlocked.Increment(ref outputOverflows);
            }
        }
    }

    private static bool AddressesEqual(IPAddress first, IPAddress second)
    {
        IPAddress normalizedFirst = first.IsIPv4MappedToIPv6 ? first.MapToIPv4() : first;
        IPAddress normalizedSecond = second.IsIPv4MappedToIPv6 ? second.MapToIPv4() : second;
        return normalizedFirst.Equals(normalizedSecond);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        udp?.Dispose();
        if (receiveLoop is not null)
        {
            await receiveLoop.ConfigureAwait(false);
        }

        foreach (ReceiverSession session in sessions.Values)
        {
            session.Dispose();
        }

        sessions.Clear();
        lifetime.Dispose();
    }

    private sealed class ReceiverSession : IDisposable
    {
        public ReceiverSession(AudioSessionParameters parameters)
        {
            Parameters = parameters;
            Reassembler = new AudioFrameReassembler(parameters);
            JitterBuffer = new AudioJitterBuffer(parameters);
        }

        public AudioSessionParameters Parameters { get; }
        public AudioFrameReassembler Reassembler { get; }
        public AudioJitterBuffer JitterBuffer { get; }
        public PacketSequenceTracker PacketTracker { get; } = new();

        private long datagramsReceived;
        private long estimatedLostDatagrams;
        private long lateDatagrams;
        private long framesCompleted;
        private long concealmentFrames;

        public void RecordDatagram(PacketSequenceObservation observation)
        {
            Interlocked.Increment(ref datagramsReceived);
            Interlocked.Add(ref estimatedLostDatagrams, observation.LostDelta);
            if (observation.IsLate)
            {
                Interlocked.Increment(ref lateDatagrams);
            }
        }

        public void RecordCompletedFrame() => Interlocked.Increment(ref framesCompleted);

        public void RecordConcealmentFrame() => Interlocked.Increment(ref concealmentFrames);

        public UdpAudioSessionStatistics GetStatistics()
        {
            AudioJitterBufferStatistics jitter = JitterBuffer.Statistics;
            return new UdpAudioSessionStatistics(
                Parameters.SessionId,
                Interlocked.Read(ref datagramsReceived),
                Math.Max(0, Interlocked.Read(ref estimatedLostDatagrams)),
                Interlocked.Read(ref lateDatagrams),
                Interlocked.Read(ref framesCompleted),
                Interlocked.Read(ref concealmentFrames),
                jitter.BufferedFrames,
                jitter.TargetFrames * 10,
                jitter.EstimatedJitterMilliseconds,
                jitter.TargetIncreases,
                jitter.TargetDecreases);
        }

        public void Dispose() => CryptographicOperations.ZeroMemory(Parameters.Key);
    }
}
