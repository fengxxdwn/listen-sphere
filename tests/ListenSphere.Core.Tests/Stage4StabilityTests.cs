using System.Net;
using ListenSphere.Network;
using Xunit;

namespace ListenSphere.Core.Tests;

public sealed class Stage4StabilityTests
{
    [Fact]
    public void JitterBuffer_RemainsBoundedForThirtyMinutesEquivalentTraffic()
    {
        const uint frameCount = 180_000;
        var session = new AudioSessionParameters(
            Guid.NewGuid(),
            17,
            new byte[32],
            new byte[4],
            new IPEndPoint(IPAddress.Loopback, 1));
        var jitter = new AudioJitterBuffer(session);
        long emittedFrames = 0;
        long concealmentFrames = 0;

        for (uint sequence = 0; sequence < frameCount; sequence++)
        {
            // Model a short periodic loss burst while keeping the test deterministic.
            if (sequence > 0 && sequence % 997 is 0 or 1)
            {
                continue;
            }

            IReadOnlyList<NetworkAudioFrame> emitted = jitter.Push(new NetworkAudioFrame(
                session.SessionId,
                session.StreamId,
                sequence,
                sequence * 480UL,
                [],
                false));
            emittedFrames += emitted.Count;
            concealmentFrames += emitted.Count(frame => frame.IsConcealment);

            AudioJitterBufferStatistics current = jitter.Statistics;
            Assert.InRange(current.BufferedFrames, 0, 12);
            Assert.InRange(current.TargetFrames, 3, 12);
        }

        AudioJitterBufferStatistics final = jitter.Statistics;
        Assert.True(emittedFrames > 175_000);
        Assert.True(concealmentFrames >= 300);
        Assert.True(final.TargetIncreases > 0);
        Assert.InRange(final.BufferedFrames, 0, 12);
        Assert.InRange(final.TargetFrames, 3, 12);
    }

    [Fact]
    public void ReconnectBackoff_SurvivesRepeatedTransportSwitchCycles()
    {
        var tracker = new ReconnectBackoffTracker(
            [TimeSpan.Zero, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)]);
        Guid wireless = Guid.NewGuid();
        Guid bluetooth = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        for (var cycle = 0; cycle < 10_000; cycle++)
        {
            Guid active = cycle % 2 == 0 ? wireless : bluetooth;
            Guid inactive = active == wireless ? bluetooth : wireless;
            ReconnectBackoffDecision failure = tracker.RecordFailure(active, now);
            Assert.InRange(failure.RetryAfter, TimeSpan.Zero, TimeSpan.FromMilliseconds(2));

            tracker.Reset(active);
            Assert.True(tracker.Check(active, now).CanAttempt);
            Assert.True(tracker.Check(inactive, now.AddSeconds(1)).CanAttempt);
            now = now.AddMilliseconds(3);
        }
    }
}
