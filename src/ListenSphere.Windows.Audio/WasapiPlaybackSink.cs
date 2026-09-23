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
    private readonly IPlayoutClock clock = new StopwatchPlayoutClock();
    private readonly IAdaptiveAudioResampler resampler = new AdaptiveLinearResampler();
    private byte[] resampled = new byte[4096];
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
            resampler.Reset();
            outputStarted = false;
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
                    clock.Elapsed);
                resampler.Ratio = adaptiveBuffer.GetResamplingRatio(bufferedMilliseconds, frame.Data.Length / 384000d);
            }
            int required = resampler.GetMaximumOutputBytes(frame.Data.Length);
            if (resampled.Length < required) resampled = new byte[required];
            int written = resampler.Convert(frame.Data.Span, resampled);
            if (buffer.BufferedBytes + written > buffer.BufferLength)
            {
                bufferOverflows++;
                return ValueTask.CompletedTask;
            }
            buffer.AddSamples(resampled, 0, written);
            framesWritten++;
            if (!outputStarted &&
                buffer.BufferedDuration >= TimeSpan.FromMilliseconds(profile == WasapiPlaybackProfile.BluetoothResilient ? 100 : 60))
            {
                adaptiveBuffer.Reset();

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
            resampler.Reset();
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
