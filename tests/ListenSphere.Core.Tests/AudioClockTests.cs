using Xunit;
using System.Runtime.InteropServices;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;

namespace ListenSphere.Core.Tests;

[Collection("Audio performance")]
public sealed class AudioClockTests
{
    [Theory]
    [InlineData(-300)]
    [InlineData(-100)]
    [InlineData(100)]
    [InlineData(300)]
    public void TwoHourVirtualClock_WithJitter_HasBoundedLatency(int ppm)
    {
        var controller = new AdaptivePlaybackController();
        double buffer = 60, previous = 0, minimum = double.MaxValue, maximum = 0;
        for (int frame = 0; frame <= 720_000; frame++)
        {
            double now = frame * .01 / (1 + ppm / 1_000_000d) + Math.Sin(frame * .137) * .003;
            if (frame > 0) buffer -= (now - previous) * 1000;
            controller.ObserveClock((ulong)frame * 480, TimeSpan.FromSeconds(now));
            double ratio = controller.GetResamplingRatio(buffer);
            buffer += 10 / ratio;
            previous = now;
            if (frame > 6000) { minimum = Math.Min(minimum, buffer); maximum = Math.Max(maximum, buffer); }
        }
        Assert.InRange(controller.EstimatedDriftPpm, ppm - 10, ppm + 10);
        Assert.InRange(minimum, 50, 80);
        Assert.InRange(maximum, 50, 80);
        Assert.Equal(0, controller.ClockDiscontinuities);
    }

    [Fact]
    public void Estimator_HandlesWrapResetReorderingAndReconnect()
    {
        foreach (ulong origin in new[] { (ulong)uint.MaxValue - 479, ulong.MaxValue - 479 })
        {
            var estimator = new RemoteClockEstimator();
            estimator.Observe(origin, TimeSpan.Zero);
            estimator.Observe(0, TimeSpan.FromMilliseconds(10));
            estimator.Observe(480, TimeSpan.FromMilliseconds(20));
            Assert.Equal(0, estimator.Discontinuities);
        }
        var clock = new RemoteClockEstimator();
        clock.Observe(9600, TimeSpan.Zero);
        clock.Observe(10080, TimeSpan.FromMilliseconds(10));
        clock.Observe(9600, TimeSpan.FromMilliseconds(15)); // reordered
        Assert.Equal(0, clock.Discontinuities);
        clock.Observe(0, TimeSpan.FromMilliseconds(20)); // source restart
        Assert.Equal(1, clock.Discontinuities);
        clock.Observe(480, TimeSpan.FromSeconds(5)); // reconnect/pause
        Assert.Equal(2, clock.Discontinuities);
        clock.Observe(960, TimeSpan.Zero); // local epoch changes
        Assert.Equal(3, clock.Discontinuities);
        clock.Reset();
        Assert.Equal(0, clock.Discontinuities);
        Assert.Equal(0, clock.EstimatedDriftPpm);
    }

    [Theory]
    [InlineData(.998)]
    [InlineData(1)]
    [InlineData(1.002)]
    public void Resampler_PreservesContinuityAcrossFrameBoundaries(double ratio)
    {
        byte[] input = new byte[3840 * 100];
        Span<float> samples = MemoryMarshal.Cast<byte, float>(input.AsSpan());
        for (int i = 0; i < samples.Length / 2; i++)
        { samples[i * 2] = (float)Math.Sin(i * .03); samples[i * 2 + 1] = -samples[i * 2]; }
        var whole = new AdaptiveLinearResampler { Ratio = ratio };
        var chunked = new AdaptiveLinearResampler { Ratio = ratio };
        byte[] expected = new byte[whole.GetMaximumOutputBytes(input.Length)];
        byte[] actual = new byte[expected.Length];
        int length = whole.Convert(input, expected), offset = 0;
        for (int i = 0; i < 100; i++) offset += chunked.Convert(input.AsSpan(i * 3840, 3840), actual.AsSpan(offset));
        Assert.Equal(length, offset);
        var a = MemoryMarshal.Cast<byte, float>(actual.AsSpan(0, offset));
        var e = MemoryMarshal.Cast<byte, float>(expected.AsSpan(0, length));
        for (int i = 0; i < a.Length; i++) Assert.InRange(Math.Abs(a[i] - e[i]), 0, .00001f);
        for (int i = 2; i < a.Length; i += 2) Assert.InRange(Math.Abs(a[i] - a[i - 2]), 0, .031f);
        chunked.Reset();
        Assert.Equal(1, chunked.Ratio);
    }

    [Fact]
    public void Scheduler_CoarseWindowsTicksDoNotSlowTheMixer()
    {
        var clock = new VirtualClock();
        var scheduler = new PlayoutFrameScheduler(clock, TimeSpan.FromMilliseconds(10));
        int frames = 0;
        for (int tick = 1; tick <= 64000; tick++)
        { clock.Elapsed = TimeSpan.FromMilliseconds(tick * 15.625); frames += scheduler.TakeDueFrames(); }
        Assert.Equal(100000, frames);
        clock.Elapsed += TimeSpan.FromHours(1);
        Assert.Equal(8, scheduler.TakeDueFrames());
        Assert.Equal(0, scheduler.TakeDueFrames());
    }

    private sealed class VirtualClock : IPlayoutClock { public TimeSpan Elapsed { get; set; } }
}
