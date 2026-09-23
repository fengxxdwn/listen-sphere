using System.Runtime.InteropServices;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using Xunit;

namespace ListenSphere.Core.Tests;

public sealed class AudioClockBoundaryTests
{
    [Theory]
    [InlineData(-300)]
    [InlineData(-100)]
    [InlineData(100)]
    [InlineData(300)]
    public void IndependentPlaybackEndpoint_TwoHours_DoesNotAccumulateLatency(int endpointPpm)
    {
        var controller = new AdaptivePlaybackController();
        double buffer = 60, oneHour = 0;
        for (int frame = 0; frame < 720000; frame++)
        {
            buffer -= 10 * (1 + endpointPpm / 1_000_000d);
            controller.ObserveClock((ulong)frame * 480, TimeSpan.FromMilliseconds(frame * 10));
            buffer += 10 / controller.GetResamplingRatio(buffer);
            Assert.InRange(buffer, 30, 110);
            if (frame == 360000) oneHour = buffer;
        }
        Assert.InRange(Math.Abs(buffer - oneHour), 0, .1);
    }

    [Fact]
    public void Mixer_SourceRestartDiscardsOldEpoch_AndOwnsCopiedPcm()
    {
        var clock = new Clock();
        var mixer = new RemotePcmMixer(3840, playoutClock: clock);
        Guid id = Guid.NewGuid();
        byte[] input = new byte[3840], output = new byte[3840];
        MemoryMarshal.Cast<byte, float>(input.AsSpan()).Fill(.5f);
        mixer.Enqueue(id, input, 9600);
        mixer.Enqueue(id, input, 10080);
        clock.Elapsed = TimeSpan.FromMilliseconds(10);
        MemoryMarshal.Cast<byte, float>(input.AsSpan()).Fill(.25f);
        mixer.Enqueue(id, input, 0);
        mixer.Enqueue(id, input, 480);
        input.AsSpan().Clear();
        Assert.True(mixer.TryMixNext(output));
        foreach (float sample in MemoryMarshal.Cast<byte, float>(output.AsSpan())) Assert.Equal(.25f, sample);
        mixer.RemoveStream(id);
        Assert.False(mixer.TryMixNext(output));
        Assert.Equal(0, mixer.Statistics.BufferedFrames);
    }

    [Fact]
    public void Resampler_RejectsInvalidArgumentsWithoutAdvancingPhase()
    {
        var converter = new AdaptiveLinearResampler();
        byte[] input = new byte[3840], output = new byte[4096];
        Assert.Throws<ArgumentOutOfRangeException>(() => converter.Ratio = double.NaN);
        Assert.Throws<ArgumentException>(() => converter.Convert(input, input));
        Assert.Throws<ArgumentException>(() => converter.Convert(input, new byte[8]));
        Assert.Equal(3832, converter.Convert(input, output));
        Assert.Equal(3840, converter.Convert(input, output));
    }

    private sealed class Clock : IPlayoutClock { public TimeSpan Elapsed { get; set; } }
}
