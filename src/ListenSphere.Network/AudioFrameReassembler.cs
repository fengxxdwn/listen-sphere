using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using ListenSphere.Protocol;

namespace ListenSphere.Network;

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
