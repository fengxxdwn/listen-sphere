using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using Serilog;

namespace ListenSphere.Controller.Coordinators;

internal sealed class SecondaryPlaybackRoute(IAudioPlaybackSink sink) : IAsyncDisposable
{
    private readonly MasterSoftLimiter limiter = new();

    public ValueTask StartAsync(CancellationToken cancellationToken) =>
        sink.StartAsync(cancellationToken);

    public async ValueTask WriteAsync(
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken,
        float gain = 1f)
    {
        byte[] copy = pcm.ToArray();
        PcmGainProcessor.Apply(copy, Math.Clamp(gain, 0, 1));
        limiter.Process(copy);
        await sink.WriteAsync(
            new AudioFrame(copy, AudioFormat.Default, 480, timestamp),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await sink.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Secondary playback stop failed");
        }
        try
        {
            await sink.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Secondary playback dispose failed");
        }
    }
}
