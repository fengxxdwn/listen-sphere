using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ListenSphere.Windows.Audio;

public sealed class WasapiPlaybackStoppedEventArgs(Exception? exception) : EventArgs
{
    public Exception? Exception { get; } = exception;
}

/// <summary>Plays normalized PCM to a selected Windows endpoint in shared mode.</summary>
public sealed class WasapiPlaybackSink(string deviceId) :
    IAudioPlaybackSink,
    IAudioPlaybackDiagnostics
{
    private readonly object gate = new();
    private readonly AdaptivePlaybackController adaptiveBuffer = new();
    private MMDevice? device;
    private BufferedWaveProvider? buffer;
    private WasapiOut? output;
    private long playbackStartedAt;
    private long framesWritten;
    private long bufferUnderruns;
    private long bufferOverflows;
    private long driftCorrections;
    private bool bufferWasLow = true;
    private double estimatedClockDriftPpm;
    private bool disposed;

    public AudioFormat InputFormat => AudioFormat.Default;
    public event EventHandler<WasapiPlaybackStoppedEventArgs>? PlaybackStopped;

    public AudioPlaybackStatistics Statistics
    {
        get
        {
            lock (gate)
            {
                return new AudioPlaybackStatistics(
                    framesWritten,
                    bufferUnderruns,
                    bufferOverflows,
                    driftCorrections,
                    buffer is null ? 0 : checked((int)buffer.BufferedDuration.TotalMilliseconds),
                    estimatedClockDriftPpm);
            }
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (output is not null)
            {
                throw new InvalidOperationException("Playback is already running.");
            }

            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
            buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2))
            {
                BufferDuration = TimeSpan.FromMilliseconds(250),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
            output.PlaybackStopped += OnPlaybackStopped;
            output.Init(buffer);
            output.Play();
            adaptiveBuffer.Reset();
            playbackStartedAt = Stopwatch.GetTimestamp();
            bufferWasLow = true;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Format != InputFormat)
        {
            throw new ArgumentException("Playback accepts normalized ListenSphere PCM only.", nameof(frame));
        }

        lock (gate)
        {
            if (buffer is null)
            {
                throw new InvalidOperationException("Playback is not running.");
            }

            int bufferedMilliseconds =
                checked((int)buffer.BufferedDuration.TotalMilliseconds);
            BufferCorrection correction = adaptiveBuffer.EvaluateBuffer(bufferedMilliseconds);
            if (correction == BufferCorrection.DropFrame)
            {
                bufferOverflows++;
                driftCorrections++;
                return ValueTask.CompletedTask;
            }

            if (correction == BufferCorrection.RecordUnderrun)
            {
                if (!bufferWasLow && framesWritten > 0)
                {
                    bufferUnderruns++;
                }

                bufferWasLow = true;
            }
            else
            {
                bufferWasLow = false;
            }

            if (buffer.BufferedBytes + frame.Data.Length > buffer.BufferLength)
            {
                bufferOverflows++;
                return ValueTask.CompletedTask;
            }

            estimatedClockDriftPpm = adaptiveBuffer.ObserveClock(
                frame.Timestamp,
                Stopwatch.GetElapsedTime(playbackStartedAt));
            if (MemoryMarshal.TryGetArray(frame.Data, out var segment) &&
                segment.Array is not null)
            {
                buffer.AddSamples(segment.Array, segment.Offset, segment.Count);
            }
            else
            {
                var rented = ArrayPool<byte>.Shared.Rent(frame.Data.Length);
                try
                {
                    frame.Data.Span.CopyTo(rented);
                    buffer.AddSamples(rented, 0, frame.Data.Length);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            framesWritten++;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (output is not null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                output.Stop();
                output.Dispose();
            }
            output = null;
            buffer = null;
            adaptiveBuffer.Reset();
            device?.Dispose();
            device = null;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        disposed = true;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args) =>
        PlaybackStopped?.Invoke(this, new WasapiPlaybackStoppedEventArgs(args.Exception));
}
