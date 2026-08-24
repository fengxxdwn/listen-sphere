using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Windows.Audio;

/// <summary>Plays a short validation tone without changing endpoint master volume.</summary>
public interface ITestTonePlayer
{
    ValueTask PlayAsync(string deviceId, CancellationToken cancellationToken);
}

/// <summary>Plays a short, low-amplitude 440 Hz tone without changing endpoint volume.</summary>
public sealed class TestTonePlayer : ITestTonePlayer
{
    public async ValueTask PlayAsync(string deviceId, CancellationToken cancellationToken)
    {
        await using var sink = new WasapiPlaybackSink(deviceId);
        await sink.StartAsync(cancellationToken).ConfigureAwait(false);
        const int frameSamples = 480;
        var samples = new float[frameSamples * 2];
        ulong timestamp = 0;
        for (var frameIndex = 0; frameIndex < 100; frameIndex++)
        {
            for (var sampleIndex = 0; sampleIndex < frameSamples; sampleIndex++)
            {
                var position = frameIndex * frameSamples + sampleIndex;
                var sample = 0.08f * MathF.Sin(2 * MathF.PI * 440 * position / 48_000);
                samples[sampleIndex * 2] = sample;
                samples[sampleIndex * 2 + 1] = sample;
            }

            await sink.WriteAsync(
                new AudioFrame(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsMemory().Span).ToArray(),
                    AudioFormat.Default,
                    frameSamples,
                    timestamp),
                cancellationToken).ConfigureAwait(false);
            timestamp += frameSamples;
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        await sink.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
