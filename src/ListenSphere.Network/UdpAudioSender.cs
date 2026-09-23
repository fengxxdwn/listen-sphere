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
    // Owned by this sender for its lifetime; sendGate covers every awaited socket send.
    private readonly byte[] datagramBuffer = new byte[AudioPacketHeader.MaximumDatagramSize];
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
            const int maximumPayload = AudioPacketHeader.MaximumDatagramSize -
                AudioPacketHeader.Size - AudioPayloadProtector.TagSize;
            uint fragmentCount = checked((uint)((pcm.Length + maximumPayload - 1) / maximumPayload));
            for (int index = 0, offset = 0; index < fragmentCount; index++)
            {
                int length = Math.Min(maximumPayload, pcm.Length - offset);
                var header = template with
                {
                    FragmentIndex = (byte)index,
                    FragmentCount = (byte)fragmentCount,
                    PacketSequence = unchecked(template.PacketSequence + (uint)index),
                    PayloadLength = checked((ushort)length)
                };
                int datagramLength = AudioPayloadProtector.Protect(
                    header, pcm.Span.Slice(offset, length), session.Key, session.Salt, datagramBuffer);
                offset += length;
                try
                {
                    int sent = await udp.SendAsync(datagramBuffer.AsMemory(0, datagramLength), cancellationToken)
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
