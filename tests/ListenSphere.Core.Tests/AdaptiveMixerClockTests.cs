using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using Xunit;

namespace ListenSphere.Core.Tests;

[Collection("Audio performance")]
public sealed class AdaptiveMixerClockTests
{
    [Fact]
    public void FourIndependentSources_TwoHoursOfPcm_StayBounded()
    {
        var clock = new VirtualClock();
        var mixer = new RemotePcmMixer(3840, startupFrames: 6, maximumFrames: 24, playoutClock: clock);
        int[] ppms = [-300, -100, 100, 300];
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var sent = new int[4];
        byte[] input = new byte[3840], output = new byte[3840];
        for (int source = 0; source < 4; source++)
        {
            mixer.RegisterStream(ids[source]);
            for (int i = 0; i < 6; i++) mixer.Enqueue(ids[source], input, (ulong)sent[source]++ * 480);
        }
        for (int tick = 1; tick <= 720000; tick++)
        {
            clock.Elapsed = TimeSpan.FromMilliseconds(tick * 10);
            for (int source = 0; source < 4; source++)
            {
                int due = 6 + (int)Math.Floor(tick * (1 + ppms[source] / 1_000_000d) + Math.Sin(tick * .137 + source) * .3);
                while (sent[source] < due) mixer.Enqueue(ids[source], input, (ulong)sent[source]++ * 480);
            }
            Assert.True(mixer.TryMixNext(output));
            if (tick % 6000 == 0) Assert.InRange(mixer.Statistics.BufferedFrames, 8, 40);
        }
        Assert.Equal(0, mixer.Statistics.StreamOverflows);
        Assert.Equal(0, mixer.Statistics.StreamUnderflows);
        Assert.Equal(720000, mixer.Statistics.MixedFrames);
    }

    [Fact]
    public void TimestampedMixer_AfterWarmup_DoesNotAllocatePerFrame()
    {
        var clock = new VirtualClock();
        var mixer = new RemotePcmMixer(3840, playoutClock: clock);
        Guid id = Guid.NewGuid();
        byte[] pcm = new byte[3840], output = new byte[3840];
        mixer.Enqueue(id, pcm, 0);
        for (int i = 1; i <= 10000; i++)
        { clock.Elapsed = TimeSpan.FromMilliseconds(i * 10); mixer.Enqueue(id, pcm, (ulong)i * 480); mixer.TryMixNext(output); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 10001; i <= 20000; i++)
        { clock.Elapsed = TimeSpan.FromMilliseconds(i * 10); mixer.Enqueue(id, pcm, (ulong)i * 480); mixer.TryMixNext(output); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class VirtualClock : IPlayoutClock { public TimeSpan Elapsed { get; set; } }
}
