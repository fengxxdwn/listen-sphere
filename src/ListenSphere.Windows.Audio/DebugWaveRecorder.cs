using ListenSphere.Audio.Abstractions;
using NAudio.Wave;

namespace ListenSphere.Windows.Audio;

/// <summary>Records an explicitly requested, time-bounded diagnostic WAV file.</summary>
public interface IDebugWaveRecorder
{
    ValueTask RecordAsync(
        string deviceId,
        string destinationPath,
        TimeSpan duration,
        CancellationToken cancellationToken);
}

/// <summary>Writes normalized loopback frames only after an explicit user action.</summary>
public sealed class DebugWaveRecorder(IWasapiCaptureSourceFactory captureFactory) : IDebugWaveRecorder
{
    public async ValueTask RecordAsync(
        string deviceId,
        string destinationPath,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The debug recording directory does not exist.");
        }

        await using var source = captureFactory.Create(deviceId);
        using var writer = new WaveFileWriter(
            destinationPath,
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2));
        var sink = new WaveFileSink(writer);
        await source.StartAsync(sink, cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class WaveFileSink(WaveFileWriter writer) : IAudioFrameSink
    {
        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.Format != AudioFormat.Default)
            {
                throw new InvalidDataException("Debug WAV expects normalized ListenSphere PCM.");
            }

            var bytes = frame.Data.ToArray();
            writer.Write(bytes, 0, bytes.Length);
            return ValueTask.CompletedTask;
        }
    }
}
