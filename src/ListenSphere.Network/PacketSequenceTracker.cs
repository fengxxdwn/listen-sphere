using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using ListenSphere.Protocol;

namespace ListenSphere.Network;

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
