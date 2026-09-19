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

public readonly record struct PacketSequenceObservation(
    bool IsDuplicate,
    long LostDelta,
    bool IsLate);

public sealed record ReassemblyResult(NetworkAudioFrame? Frame, int ExpiredFrames);

public sealed record AudioJitterBufferStatistics(
    int BufferedFrames,
    int TargetFrames,
    double EstimatedJitterMilliseconds,
    long TargetIncreases,
    long TargetDecreases);
