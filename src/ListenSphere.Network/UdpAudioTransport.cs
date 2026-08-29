using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using ListenSphere.Protocol;

namespace ListenSphere.Network;

public sealed record AudioSessionParameters(
    Guid SessionId,
    uint StreamId,
    byte[] Key,
    byte[] Salt,
    IPEndPoint RemoteEndpoint,
    uint SampleRate = 48_000,
    byte ChannelCount = 2,
    ushort FrameSamples = 480)
{
    public void Validate()
    {
        if (SessionId == Guid.Empty || StreamId == 0)
        {
            throw new ArgumentException("Audio session and stream identifiers must be non-zero.");
        }

        if (Key.Length != 32 || Salt.Length != AudioPayloadProtector.SaltSize)
        {
            throw new ArgumentException("P4 requires a 256-bit key and a 4-byte session salt.");
        }

        if (SampleRate != 48_000 || ChannelCount != 2 || FrameSamples != 480)
        {
            throw new ArgumentException("P4 only accepts 48 kHz Float32 stereo 10 ms frames.");
        }
    }
}

public sealed record NetworkAudioFrame(
    Guid SessionId,
    uint StreamId,
    uint FrameSequence,
    ulong Timestamp,
    byte[] Pcm,
    bool IsConcealment);

public sealed record UdpAudioSenderStatistics(
    long FramesSent,
    long DatagramsSent,
    long BytesSent,
    long SendFailures);

public sealed class UdpAudioSender : IAsyncDisposable
{
    private readonly AudioSessionParameters session;
    private readonly UdpClient udp;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private uint packetSequence;
    private uint frameSequence;
    private bool packetSequenceExhausted;
    private long framesSent;
    private long datagramsSent;
    private long bytesSent;
    private long sendFailures;
    private bool disposed;

    public UdpAudioSender(AudioSessionParameters session)
    {
        session.Validate();
        this.session = session;
        udp = new UdpClient(session.RemoteEndpoint.AddressFamily);
        udp.Connect(session.RemoteEndpoint);
    }

    public UdpAudioSenderStatistics Statistics => new(
        Interlocked.Read(ref framesSent),
        Interlocked.Read(ref datagramsSent),
        Interlocked.Read(ref bytesSent),
        Interlocked.Read(ref sendFailures));

    public async ValueTask SendFrameAsync(
        ReadOnlyMemory<byte> pcm,
        ulong timestamp,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int expectedLength = session.FrameSamples * session.ChannelCount * sizeof(float);
        if (pcm.Length != expectedLength)
        {
            throw new ArgumentException(
                $"A P4 audio frame must contain exactly {expectedLength} bytes.",
                nameof(pcm));
        }

        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (packetSequenceExhausted)
            {
                throw new InvalidOperationException(
                    "The UDP packet sequence is exhausted; negotiate a new key before continuing.");
            }

            var template = AudioPacketHeader.CreateDefault(
                session.SessionId,
                session.StreamId) with
            {
                PacketSequence = packetSequence,
                FrameSequence = frameSequence,
                Timestamp = timestamp,
                SampleRate = session.SampleRate,
                ChannelCount = session.ChannelCount,
                FrameSamples = session.FrameSamples
            };
            IReadOnlyList<byte[]> fragments = AudioPacketFragmenter.Fragment(pcm.Span, template);
            foreach (byte[] fragment in fragments)
            {
                if (!AudioPacketHeader.TryReadDatagram(
                    fragment,
                    out AudioPacketHeader header,
                    out ReadOnlySpan<byte> plaintext,
                    out _))
                {
                    throw new InvalidDataException("The local fragmenter produced an invalid datagram.");
                }

                byte[] fragmentPlaintext = plaintext.ToArray();
                byte[] protectedDatagram = AudioPayloadProtector.Protect(
                    header,
                    fragmentPlaintext,
                    session.Key,
                    session.Salt);
                try
                {
                    int sent = await udp.SendAsync(protectedDatagram, cancellationToken)
                        .ConfigureAwait(false);
                    Interlocked.Increment(ref datagramsSent);
                    Interlocked.Add(ref bytesSent, sent);
                }
                catch
                {
                    Interlocked.Increment(ref sendFailures);
                    throw;
                }
            }

            uint fragmentCount = checked((uint)fragments.Count);
            if (packetSequence > uint.MaxValue - fragmentCount)
            {
                packetSequenceExhausted = true;
            }
            else
            {
                packetSequence += fragmentCount;
            }

            frameSequence = unchecked(frameSequence + 1);
            Interlocked.Increment(ref framesSent);
        }
        finally
        {
            sendGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            udp.Dispose();
            CryptographicOperations.ZeroMemory(session.Key);
            disposed = true;
        }

        return ValueTask.CompletedTask;
    }
}

public sealed record UdpAudioReceiverStatistics(
    long DatagramsReceived,
    long InvalidDatagrams,
    long AuthenticationFailures,
    long DuplicateDatagrams,
    long ExpiredFrames,
    long FramesCompleted,
    long ConcealmentFrames,
    long EstimatedLostDatagrams,
    long LateDatagrams,
    long OutputOverflows,
    int OutputQueueDepth,
    int ActiveSessions,
    int JitterBufferedFrames,
    int AdaptiveTargetMilliseconds,
    double EstimatedJitterMilliseconds);

public sealed record UdpAudioSessionStatistics(
    Guid SessionId,
    long DatagramsReceived,
    long EstimatedLostDatagrams,
    long LateDatagrams,
    long FramesCompleted,
    long ConcealmentFrames,
    int BufferedFrames,
    int TargetBufferMilliseconds,
    double EstimatedJitterMilliseconds,
    long TargetIncreases,
    long TargetDecreases);

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

public readonly record struct PacketSequenceObservation(
    bool IsDuplicate,
    long LostDelta,
    bool IsLate);

/// <summary>Tracks packet gaps, late recovery, duplicates, and UInt32 wraparound.</summary>
public sealed class PacketSequenceTracker
{
    private const int RememberedPacketLimit = 4096;
    private readonly HashSet<uint> packets = [];
    private readonly Queue<uint> packetOrder = [];
    private readonly HashSet<uint> missing = [];
    private uint highest;
    private bool initialized;

    public PacketSequenceObservation Observe(uint sequence)
    {
        if (!packets.Add(sequence))
        {
            return new PacketSequenceObservation(true, 0, false);
        }

        packetOrder.Enqueue(sequence);
        if (packetOrder.Count > RememberedPacketLimit)
        {
            packets.Remove(packetOrder.Dequeue());
        }

        if (!initialized)
        {
            highest = sequence;
            initialized = true;
            return default;
        }

        int forward = unchecked((int)(sequence - highest));
        if (forward > 0)
        {
            int gap = forward - 1;
            if (gap <= RememberedPacketLimit)
            {
                for (var offset = 1; offset < forward; offset++)
                {
                    missing.Add(unchecked(highest + (uint)offset));
                }
            }

            highest = sequence;
            return new PacketSequenceObservation(false, gap, false);
        }

        if (missing.Remove(sequence))
        {
            return new PacketSequenceObservation(false, -1, true);
        }

        return default;
    }
}

public sealed record ReassemblyResult(NetworkAudioFrame? Frame, int ExpiredFrames);

public sealed class AudioFrameReassembler
{
    private static readonly TimeSpan ReassemblyTimeout = TimeSpan.FromMilliseconds(200);
    private readonly AudioSessionParameters session;
    private readonly Dictionary<uint, Assembly> assemblies = [];

    public AudioFrameReassembler(AudioSessionParameters session)
    {
        this.session = session;
    }

    public ReassemblyResult Add(AudioPacketHeader header, byte[] plaintext)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int expired = 0;
        foreach (uint key in assemblies
            .Where(item => now - item.Value.CreatedAt > ReassemblyTimeout)
            .Select(item => item.Key)
            .ToArray())
        {
            assemblies.Remove(key);
            expired++;
        }

        if (!assemblies.TryGetValue(header.FrameSequence, out Assembly? assembly))
        {
            assembly = new Assembly(header, now);
            assemblies.Add(header.FrameSequence, assembly);
        }

        if (!assembly.TryAdd(header, plaintext) || !assembly.IsComplete)
        {
            return new ReassemblyResult(null, expired);
        }

        assemblies.Remove(header.FrameSequence);
        byte[] frame = assembly.Join();
        int expectedLength = session.FrameSamples * session.ChannelCount * sizeof(float);
        if (frame.Length != expectedLength)
        {
            return new ReassemblyResult(null, expired + 1);
        }

        return new ReassemblyResult(
            new NetworkAudioFrame(
                header.SessionId,
                header.StreamId,
                header.FrameSequence,
                header.Timestamp,
                frame,
                false),
            expired);
    }

    private sealed class Assembly
    {
        private readonly byte[][] fragments;
        private readonly ulong timestamp;
        private int received;

        public Assembly(AudioPacketHeader header, DateTimeOffset createdAt)
        {
            fragments = new byte[header.FragmentCount][];
            timestamp = header.Timestamp;
            CreatedAt = createdAt;
        }

        public DateTimeOffset CreatedAt { get; }
        public bool IsComplete => received == fragments.Length;

        public bool TryAdd(AudioPacketHeader header, byte[] payload)
        {
            if (header.FragmentCount != fragments.Length ||
                header.Timestamp != timestamp ||
                fragments[header.FragmentIndex] is not null)
            {
                return false;
            }

            fragments[header.FragmentIndex] = payload;
            received++;
            return true;
        }

        public byte[] Join()
        {
            int length = fragments.Sum(fragment => fragment.Length);
            byte[] result = GC.AllocateUninitializedArray<byte>(length);
            int offset = 0;
            foreach (byte[] fragment in fragments)
            {
                fragment.CopyTo(result, offset);
                offset += fragment.Length;
            }

            return result;
        }
    }
}

public sealed record AudioJitterBufferStatistics(
    int BufferedFrames,
    int TargetFrames,
    double EstimatedJitterMilliseconds,
    long TargetIncreases,
    long TargetDecreases);

public sealed class AudioJitterBuffer
{
    private const int MinimumTargetFrames = 3;
    private const int MaximumTargetFrames = 12;
    private const int StableFramesBeforeDecrease = 500;
    private readonly AudioSessionParameters session;
    private readonly SortedDictionary<uint, NetworkAudioFrame> frames = [];
    private uint nextSequence;
    private ulong nextTimestamp;
    private int bufferedFrameCount;
    private int targetFrames = MinimumTargetFrames;
    private long targetIncreases;
    private long targetDecreases;
    private long lastArrivalTimestamp;
    private ulong lastSourceTimestamp;
    private double estimatedJitterMilliseconds;
    private int stableFrames;
    private uint highestArrivalSequence;
    private bool hasArrivalSequence;
    private bool hasTimingSample;
    private bool started;

    public AudioJitterBuffer(AudioSessionParameters session)
    {
        this.session = session;
    }

    public int BufferedFrameCount => Volatile.Read(ref bufferedFrameCount);

    public AudioJitterBufferStatistics Statistics => new(
        Volatile.Read(ref bufferedFrameCount),
        Volatile.Read(ref targetFrames),
        Volatile.Read(ref estimatedJitterMilliseconds),
        Interlocked.Read(ref targetIncreases),
        Interlocked.Read(ref targetDecreases));

    public IReadOnlyList<NetworkAudioFrame> Push(NetworkAudioFrame frame)
    {
        ObserveArrival(frame);
        if (!frames.TryAdd(frame.FrameSequence, frame))
        {
            return [];
        }

        Volatile.Write(ref bufferedFrameCount, frames.Count);
        if (!started)
        {
            if (frames.Count < targetFrames)
            {
                return [];
            }

            NetworkAudioFrame first = frames.First().Value;
            nextSequence = first.FrameSequence;
            nextTimestamp = first.Timestamp;
            started = true;
        }

        var ready = new List<NetworkAudioFrame>();
        while (frames.Count >= targetFrames)
        {
            if (frames.Remove(nextSequence, out NetworkAudioFrame? current))
            {
                ready.Add(current);
                nextTimestamp = current.Timestamp + session.FrameSamples;
            }
            else
            {
                IncreaseTarget();
                ready.Add(new NetworkAudioFrame(
                    session.SessionId,
                    session.StreamId,
                    nextSequence,
                    nextTimestamp,
                    new byte[session.FrameSamples * session.ChannelCount * sizeof(float)],
                    true));
                nextTimestamp += session.FrameSamples;
            }

            nextSequence = unchecked(nextSequence + 1);
        }

        Volatile.Write(ref bufferedFrameCount, frames.Count);
        return ready;
    }

    private void ObserveArrival(NetworkAudioFrame frame)
    {
        long arrival = Stopwatch.GetTimestamp();
        bool discontinuity = false;
        if (hasArrivalSequence)
        {
            int forward = unchecked((int)(frame.FrameSequence - highestArrivalSequence));
            discontinuity = forward != 1;
            if (forward > 0)
            {
                highestArrivalSequence = frame.FrameSequence;
            }
        }
        else
        {
            highestArrivalSequence = frame.FrameSequence;
            hasArrivalSequence = true;
        }

        if (hasTimingSample && frame.Timestamp >= lastSourceTimestamp)
        {
            double arrivalMilliseconds =
                (arrival - lastArrivalTimestamp) * 1000d / Stopwatch.Frequency;
            double sourceMilliseconds =
                (frame.Timestamp - lastSourceTimestamp) * 1000d / session.SampleRate;
            double deviation = Math.Abs(arrivalMilliseconds - sourceMilliseconds);
            estimatedJitterMilliseconds +=
                (Math.Min(deviation, 250d) - estimatedJitterMilliseconds) / 16d;
        }

        lastArrivalTimestamp = arrival;
        lastSourceTimestamp = frame.Timestamp;
        hasTimingSample = true;

        int desired = Math.Clamp(
            (int)Math.Ceiling(estimatedJitterMilliseconds / 10d) + 2,
            MinimumTargetFrames,
            MaximumTargetFrames);
        if (discontinuity)
        {
            desired = Math.Min(MaximumTargetFrames, Math.Max(desired, targetFrames + 1));
            stableFrames = 0;
        }
        else
        {
            stableFrames++;
        }

        if (desired > targetFrames)
        {
            int increase = desired - targetFrames;
            Volatile.Write(ref targetFrames, desired);
            Interlocked.Add(ref targetIncreases, increase);
            stableFrames = 0;
        }
        else if (desired < targetFrames && stableFrames >= StableFramesBeforeDecrease)
        {
            Volatile.Write(ref targetFrames, targetFrames - 1);
            Interlocked.Increment(ref targetDecreases);
            stableFrames = 0;
        }

        Volatile.Write(ref estimatedJitterMilliseconds, estimatedJitterMilliseconds);
    }

    private void IncreaseTarget()
    {
        if (targetFrames >= MaximumTargetFrames)
        {
            return;
        }

        Volatile.Write(ref targetFrames, targetFrames + 1);
        Interlocked.Increment(ref targetIncreases);
        stableFrames = 0;
    }
}
