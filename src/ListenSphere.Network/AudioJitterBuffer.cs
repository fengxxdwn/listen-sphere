using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using ListenSphere.Protocol;

namespace ListenSphere.Network;

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
        var ready = new List<NetworkAudioFrame>();
        Push(frame, ready);
        return ready;
    }

    /// <summary>Clears and fills caller-owned staging storage. Frames keep independent PCM ownership.</summary>
    public void Push(NetworkAudioFrame frame, List<NetworkAudioFrame> ready)
    {
        ArgumentNullException.ThrowIfNull(ready);
        ready.Clear();
        ObserveArrival(frame);
        if (!frames.TryAdd(frame.FrameSequence, frame))
        {
            return;
        }

        Volatile.Write(ref bufferedFrameCount, frames.Count);
        if (!started)
        {
            if (frames.Count < targetFrames)
            {
                return;
            }

            NetworkAudioFrame first = frames.First().Value;
            nextSequence = first.FrameSequence;
            nextTimestamp = first.Timestamp;
            started = true;
        }


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
