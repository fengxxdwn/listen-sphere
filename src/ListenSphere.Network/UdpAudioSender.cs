using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using ListenSphere.Protocol;

namespace ListenSphere.Network;

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
