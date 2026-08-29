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
public enum WasapiPlaybackProfile
{
    Standard,
    BluetoothResilient
}

public sealed class WasapiPlaybackSink :
    IAudioPlaybackSink,
    IAudioPlaybackDiagnostics
{
    private readonly string deviceId;
    private readonly WasapiPlaybackProfile profile;
    private readonly object gate = new();
    private readonly AdaptivePlaybackController adaptiveBuffer;
    private MMDevice? device;
    private BufferedWaveProvider? buffer;
    private WasapiOut? output;
    private long playbackStartedAt;
    private long framesWritten;
    private long bufferUnderruns;
    private long bufferOverflows;
    private long driftCorrections;
    private bool bufferWasLow = true;
    private bool outputStarted;
    private double estimatedClockDriftPpm;
    private bool disposed;

    public WasapiPlaybackSink(
        string deviceId,
        WasapiPlaybackProfile profile = WasapiPlaybackProfile.Standard)
    {
        this.deviceId = deviceId;
        this.profile = profile;
        adaptiveBuffer = profile == WasapiPlaybackProfile.BluetoothResilient
            ? new AdaptivePlaybackController(targetMilliseconds: 100, toleranceMilliseconds: 80)
            : new AdaptivePlaybackController();
    }

    public WasapiPlaybackProfile Profile => profile;

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
                BufferDuration = profile == WasapiPlaybackProfile.BluetoothResilient
                    ? TimeSpan.FromMilliseconds(500)
                    : TimeSpan.FromMilliseconds(250),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            int deviceLatency = profile == WasapiPlaybackProfile.BluetoothResilient ? 100 : 40;
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, deviceLatency);
            output.PlaybackStopped += OnPlaybackStopped;
            output.Init(buffer);
            adaptiveBuffer.Reset();
            outputStarted = profile == WasapiPlaybackProfile.Standard;
            if (outputStarted)
            {
                output.Play();
                playbackStartedAt = Stopwatch.GetTimestamp();
            }
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
            BufferCorrection correction = outputStarted
                ? adaptiveBuffer.EvaluateBuffer(bufferedMilliseconds)
                : BufferCorrection.None;
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

            if (outputStarted)
            {
                estimatedClockDriftPpm = adaptiveBuffer.ObserveClock(
                    frame.Timestamp,
                    Stopwatch.GetElapsedTime(playbackStartedAt));
            }
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
            if (!outputStarted &&
                buffer.BufferedDuration >= TimeSpan.FromMilliseconds(100))
            {
                adaptiveBuffer.Reset();
                playbackStartedAt = Stopwatch.GetTimestamp();
                output!.Play();
                outputStarted = true;
                bufferWasLow = false;
            }
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
            outputStarted = false;
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
