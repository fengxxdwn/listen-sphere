namespace ListenSphere.Protocol;

public static class AudioPacketFragmenter
{
    public static IReadOnlyList<byte[]> Fragment(
        ReadOnlySpan<byte> frame,
        AudioPacketHeader template,
        int maximumDatagramSize = AudioPacketHeader.MaximumDatagramSize)
    {
        if (maximumDatagramSize <= AudioPacketHeader.Size + AudioPayloadProtector.TagSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDatagramSize));
        }

        // Every transmitted v1 fragment is authenticated, so reserve the GCM tag now.
        var maximumPayload =
            maximumDatagramSize - AudioPacketHeader.Size - AudioPayloadProtector.TagSize;
        var count = Math.Max(1, (frame.Length + maximumPayload - 1) / maximumPayload);
        if (count > byte.MaxValue)
        {
            throw new ArgumentException("The frame requires more than 255 fragments.", nameof(frame));
        }

        var fragments = new List<byte[]>(count);
        var offset = 0;
        for (var index = 0; index < count; index++)
        {
            var payloadLength = Math.Min(maximumPayload, frame.Length - offset);
            var header = template with
            {
                FragmentIndex = (byte)index,
                FragmentCount = (byte)count,
                PacketSequence = unchecked(template.PacketSequence + (uint)index),
                PayloadLength = checked((ushort)payloadLength)
            };
            var datagram = new byte[AudioPacketHeader.Size + payloadLength];
            header.Write(datagram);
            frame.Slice(offset, payloadLength).CopyTo(datagram.AsSpan(AudioPacketHeader.Size));
            fragments.Add(datagram);
            offset += payloadLength;
        }

        return fragments;
    }
}
