using System.Buffers.Binary;
using Google.Protobuf;
using ListenSphere.Protocol.V1;

namespace ListenSphere.Protocol;

public static class ControlFrameCodec
{
    public const int PrefixLength = sizeof(uint);
    public const int MaximumMessageLength = 1024 * 1024;

    public static async ValueTask WriteAsync(
        Stream stream,
        Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);

        byte[] payload = envelope.ToByteArray();
        int length = payload.Length;
        if (length <= 0 || length > MaximumMessageLength)
        {
            throw new InvalidDataException($"Control message length {length} is outside the allowed range.");
        }

        byte[] prefix = new byte[PrefixLength];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)length));
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<Envelope?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] prefix = new byte[PrefixLength];
        int prefixBytes = await ReadAtMostAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (prefixBytes == 0)
        {
            return null;
        }

        if (prefixBytes != PrefixLength)
        {
            throw new EndOfStreamException("The control frame length prefix was truncated.");
        }

        uint encodedLength = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (encodedLength is 0 or > MaximumMessageLength)
        {
            throw new InvalidDataException($"Control message length {encodedLength} is outside the allowed range.");
        }

        int length = checked((int)encodedLength);
        byte[] payload = GC.AllocateUninitializedArray<byte>(length);
        int payloadBytes = await ReadAtMostAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadBytes != length)
        {
            throw new EndOfStreamException("The control frame payload was truncated.");
        }

        return Envelope.Parser.ParseFrom(payload);
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
