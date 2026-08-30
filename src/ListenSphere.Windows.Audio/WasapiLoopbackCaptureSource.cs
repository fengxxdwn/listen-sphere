using System.Buffers;
using System.Threading.Channels;
using ListenSphere.Audio.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ListenSphere.Windows.Audio;

public sealed record WasapiCaptureStatistics(long CapturedBuffers, long DroppedBuffers);

public sealed class WasapiCaptureStoppedEventArgs(Exception? exception) : EventArgs
{
    public Exception? Exception { get; } = exception;
}

/// <summary>Creates loopback capture sources for stable Windows endpoint IDs.</summary>
public interface IWasapiCaptureSourceFactory
{
    WasapiLoopbackCaptureSource Create(string deviceId);
}

public sealed class WasapiCaptureSourceFactory : IWasapiCaptureSourceFactory
{
    public WasapiLoopbackCaptureSource Create(string deviceId) => new(deviceId);
}

public interface IWasapiRecordingCaptureSourceFactory
{
    WasapiLoopbackCaptureSource Create(string deviceId);
}

public sealed class WasapiRecordingCaptureSourceFactory : IWasapiRecordingCaptureSourceFactory
{
    public WasapiLoopbackCaptureSource Create(string deviceId) => new(deviceId, false);
}

/// <summary>
/// Captures a selected Windows render endpoint, resamples off the callback thread, and
/// emits normalized 10 ms frames.
/// </summary>
public sealed class WasapiLoopbackCaptureSource : IAudioCaptureSource
{
    private const int QueueCapacity = 32;
    private const int FrameSamples = 480;
    private const int FrameBytes = FrameSamples * 2 * sizeof(float);

    private readonly object gate = new();
    private readonly string deviceId;
    private readonly bool captureLoopback;
    private MMDevice? captureDevice;
    private WasapiCapture? capture;
    private CancellationTokenSource? lifetime;
    private Channel<PooledCaptureBuffer>? queue;
    private Task? processingTask;
    private long capturedBuffers;
    private long droppedBuffers;
    private bool disposed;

    public WasapiLoopbackCaptureSource(string deviceId, bool captureLoopback = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        this.deviceId = deviceId;
        this.captureLoopback = captureLoopback;
    }

    public event EventHandler<WasapiCaptureStoppedEventArgs>? CaptureStopped;

    public AudioFormat OutputFormat => AudioFormat.Default;

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return capture is not null;
            }
        }
    }

    public WasapiCaptureStatistics Statistics =>
        new(Interlocked.Read(ref capturedBuffers), Interlocked.Read(ref droppedBuffers));

    public ValueTask StartAsync(IAudioFrameSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (capture is not null)
            {
                throw new InvalidOperationException("Capture is already running.");
            }

            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            queue = Channel.CreateBounded<PooledCaptureBuffer>(
                new BoundedChannelOptions(QueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.DropOldest,
                    AllowSynchronousContinuations = false
                },
                dropped =>
                {
                    Interlocked.Increment(ref droppedBuffers);
                    dropped.Dispose();
                });

            using var enumerator = new MMDeviceEnumerator();
            captureDevice = enumerator.GetDevice(deviceId);
            capture = captureLoopback
                ? new WasapiLoopbackCapture(captureDevice)
                : new WasapiCapture(captureDevice);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            processingTask = ProcessAsync(
                capture.WaveFormat,
                queue.Reader,
                sink,
                lifetime.Token);
            capture.StartRecording();
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        WasapiCapture? captureToStop;
        Task? taskToWait;
        CancellationTokenSource? lifetimeToDispose;
        Channel<PooledCaptureBuffer>? queueToComplete;
        MMDevice? deviceToDispose;
        lock (gate)
        {
            captureToStop = capture;
            if (captureToStop is null)
            {
                return;
            }

            capture = null;
            captureToStop.DataAvailable -= OnDataAvailable;
            captureToStop.RecordingStopped -= OnRecordingStopped;
            taskToWait = processingTask;
            processingTask = null;
            lifetimeToDispose = lifetime;
            lifetime = null;
            queueToComplete = queue;
            queue = null;
            deviceToDispose = captureDevice;
            captureDevice = null;
        }

        try
        {
            captureToStop.StopRecording();
            queueToComplete?.Writer.TryComplete();
            lifetimeToDispose?.Cancel();
            if (taskToWait is not null)
            {
                try
                {
                    await taskToWait.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetimeToDispose?.IsCancellationRequested == true)
                {
                    return;
                }
            }
        }
        finally
        {
            captureToStop.Dispose();
            deviceToDispose?.Dispose();
            lifetimeToDispose?.Dispose();
        }
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

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        var writer = queue?.Writer;
        if (writer is null || args.BytesRecorded <= 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(args.BytesRecorded);
        Buffer.BlockCopy(args.Buffer, 0, buffer, 0, args.BytesRecorded);
        Interlocked.Increment(ref capturedBuffers);
        var item = new PooledCaptureBuffer(buffer, args.BytesRecorded);
        if (!writer.TryWrite(item))
        {
            Interlocked.Increment(ref droppedBuffers);
            item.Dispose();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        queue?.Writer.TryComplete(args.Exception);
        CaptureStopped?.Invoke(this, new WasapiCaptureStoppedEventArgs(args.Exception));
    }

    private static async Task ProcessAsync(
        WaveFormat inputFormat,
        ChannelReader<PooledCaptureBuffer> reader,
        IAudioFrameSink sink,
        CancellationToken cancellationToken)
    {
        var input = new BufferedWaveProvider(inputFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(250),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        var outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        using var resampler = new MediaFoundationResampler(input, outputFormat)
        {
            ResamplerQuality = 60
        };
        var frame = new byte[FrameBytes];
        var bufferedOutput = 0;
        ulong timestamp = 0;

        await foreach (var captured in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            using (captured)
            {
                input.AddSamples(captured.Buffer, 0, captured.Length);
            }

            while (input.BufferedBytes > 0)
            {
                var read = resampler.Read(frame, bufferedOutput, frame.Length - bufferedOutput);
                if (read <= 0)
                {
                    break;
                }

                bufferedOutput += read;
                if (bufferedOutput != frame.Length)
                {
                    continue;
                }

                await sink.WriteAsync(
                    new AudioFrame(frame, AudioFormat.Default, FrameSamples, timestamp),
                    cancellationToken).ConfigureAwait(false);
                timestamp += FrameSamples;
                bufferedOutput = 0;
            }
        }
    }

    private sealed class PooledCaptureBuffer(byte[] buffer, int length) : IDisposable
    {
        private byte[]? buffer = buffer;

        public byte[] Buffer => buffer ?? throw new ObjectDisposedException(nameof(PooledCaptureBuffer));
        public int Length { get; } = length;

        public void Dispose()
        {
            var returned = Interlocked.Exchange(ref buffer, null);
            if (returned is not null)
            {
                ArrayPool<byte>.Shared.Return(returned);
            }
        }
    }
}
