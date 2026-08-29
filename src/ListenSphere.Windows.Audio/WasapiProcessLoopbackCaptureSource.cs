using System.Buffers;
using System.Runtime.InteropServices;
using ListenSphere.Audio.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ListenSphere.Windows.Audio;

public interface IProcessLoopbackCaptureSourceFactory
{
    WasapiProcessLoopbackCaptureSource Create(int processId);
}

public sealed class ProcessLoopbackCaptureSourceFactory : IProcessLoopbackCaptureSourceFactory
{
    public WasapiProcessLoopbackCaptureSource Create(int processId) => new(processId);
}

/// <summary>
/// Captures audio rendered by one Windows process tree through the process-loopback
/// virtual audio device and emits normalized 10 ms frames.
/// </summary>
public sealed class WasapiProcessLoopbackCaptureSource : IAudioCaptureSource
{
    private const int FrameSamples = 480;
    private const int FrameBytes = FrameSamples * 2 * sizeof(float);
    private readonly object gate = new();
    private readonly int processId;
    private AudioClient? audioClient;
    private AudioCaptureClient? captureClient;
    private EventWaitHandle? sampleReady;
    private CancellationTokenSource? lifetime;
    private Task? captureTask;
    private long capturedBuffers;
    private bool disposed;

    public WasapiProcessLoopbackCaptureSource(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        this.processId = processId;
    }

    public AudioFormat OutputFormat => AudioFormat.Default;
    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return audioClient is not null;
            }
        }
    }

    public WasapiCaptureStatistics Statistics =>
        new(Interlocked.Read(ref capturedBuffers), 0);

    public async ValueTask StartAsync(
        IAudioFrameSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            throw new PlatformNotSupportedException(
                "按应用捕获需要 Windows 10 build 20348 或更高版本。");
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (audioClient is not null)
            {
                throw new InvalidOperationException("Capture is already running.");
            }
        }

        AudioClient client = await ProcessLoopbackActivation.ActivateAsync(
            processId,
            cancellationToken).ConfigureAwait(false);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
        var sourceLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback |
                    AudioClientStreamFlags.EventCallback |
                    AudioClientStreamFlags.AutoConvertPcm |
                    AudioClientStreamFlags.SrcDefaultQuality,
                0,
                0,
                format,
                Guid.Empty);
            AudioCaptureClient reader = client.AudioCaptureClient;
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());

            lock (gate)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(WasapiProcessLoopbackCaptureSource));
                }

                audioClient = client;
                captureClient = reader;
                sampleReady = ready;
                lifetime = sourceLifetime;
                captureTask = Task.Run(
                    () => CaptureAsync(reader, ready, sink, sourceLifetime.Token),
                    CancellationToken.None);
            }

            client.Start();
        }
        catch
        {
            sourceLifetime.Cancel();
            sourceLifetime.Dispose();
            ready.Dispose();
            client.Dispose();
            lock (gate)
            {
                audioClient = null;
                captureClient = null;
                sampleReady = null;
                lifetime = null;
                captureTask = null;
            }
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        AudioClient? client;
        AudioCaptureClient? reader;
        EventWaitHandle? ready;
        CancellationTokenSource? sourceLifetime;
        Task? task;
        lock (gate)
        {
            client = audioClient;
            if (client is null)
            {
                return;
            }

            audioClient = null;
            reader = captureClient;
            captureClient = null;
            ready = sampleReady;
            sampleReady = null;
            sourceLifetime = lifetime;
            lifetime = null;
            task = captureTask;
            captureTask = null;
        }

        sourceLifetime?.Cancel();
        ready?.Set();
        try
        {
            client.Stop();
            if (task is not null)
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (sourceLifetime?.IsCancellationRequested == true)
        {
        }
        finally
        {
            reader?.Dispose();
            client.Dispose();
            ready?.Dispose();
            sourceLifetime?.Dispose();
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

    private async Task CaptureAsync(
        AudioCaptureClient reader,
        EventWaitHandle ready,
        IAudioFrameSink sink,
        CancellationToken cancellationToken)
    {
        var frame = new byte[FrameBytes];
        var buffered = 0;
        ulong timestamp = 0;
        WaitHandle[] waits = [ready, cancellationToken.WaitHandle];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(waits) != 0)
            {
                break;
            }

            while (!cancellationToken.IsCancellationRequested &&
                reader.GetNextPacketSize() > 0)
            {
                int frames = 0;
                AudioClientBufferFlags flags = 0;
                IntPtr pointer = IntPtr.Zero;
                byte[]? packet = null;
                try
                {
                    pointer = reader.GetBuffer(out frames, out flags);
                    int bytes = checked(frames * 2 * sizeof(float));
                    packet = ArrayPool<byte>.Shared.Rent(bytes);
                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                    {
                        packet.AsSpan(0, bytes).Clear();
                    }
                    else
                    {
                        Marshal.Copy(pointer, packet, 0, bytes);
                    }

                    Interlocked.Increment(ref capturedBuffers);
                    int offset = 0;
                    while (offset < bytes)
                    {
                        int copied = Math.Min(FrameBytes - buffered, bytes - offset);
                        packet.AsSpan(offset, copied).CopyTo(frame.AsSpan(buffered));
                        offset += copied;
                        buffered += copied;
                        if (buffered != FrameBytes)
                        {
                            continue;
                        }

                        await sink.WriteAsync(
                            new AudioFrame(frame, AudioFormat.Default, FrameSamples, timestamp),
                            cancellationToken).ConfigureAwait(false);
                        timestamp += FrameSamples;
                        buffered = 0;
                    }
                }
                finally
                {
                    if (frames > 0)
                    {
                        reader.ReleaseBuffer(frames);
                    }
                    if (packet is not null)
                    {
                        ArrayPool<byte>.Shared.Return(packet);
                    }
                }
            }
        }
    }

    private static class ProcessLoopbackActivation
    {
        private const string VirtualProcessLoopbackDevice = "VAD\\Process_Loopback";
        private const ushort VariantBlob = 65;

        public static async Task<AudioClient> ActivateAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            var parameters = new AudioClientActivationParameters
            {
                ActivationType = 1,
                ProcessLoopback = new AudioClientProcessLoopbackParameters
                {
                    TargetProcessId = checked((uint)processId),
                    ProcessLoopbackMode = 0
                }
            };
            IntPtr parameterMemory = Marshal.AllocCoTaskMem(
                Marshal.SizeOf<AudioClientActivationParameters>());
            IntPtr variantMemory = Marshal.AllocCoTaskMem(Marshal.SizeOf<PropVariant>());
            try
            {
                Marshal.StructureToPtr(parameters, parameterMemory, false);
                var variant = new PropVariant
                {
                    VariantType = VariantBlob,
                    Blob = new Blob
                    {
                        Size = checked((uint)Marshal.SizeOf<AudioClientActivationParameters>()),
                        Data = parameterMemory
                    }
                };
                Marshal.StructureToPtr(variant, variantMemory, false);

                var handler = new ActivationHandler();
                Guid iid = typeof(IAudioClient).GUID;
                int result = ActivateAudioInterfaceAsync(
                    VirtualProcessLoopbackDevice,
                    ref iid,
                    variantMemory,
                    handler,
                    out IActivateAudioInterfaceAsyncOperation operation);
                Marshal.ThrowExceptionForHR(result);
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    () => handler.Cancel(cancellationToken));
                object activated = await handler.Task.ConfigureAwait(false);
                GC.KeepAlive(operation);
                return new AudioClient((IAudioClient)activated);
            }
            finally
            {
                Marshal.FreeCoTaskMem(variantMemory);
                Marshal.FreeCoTaskMem(parameterMemory);
            }
        }

        [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid riid,
            IntPtr activationParameters,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
        {
            private readonly TaskCompletionSource<object> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<object> Task => completion.Task;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                try
                {
                    operation.GetActivateResult(out int result, out object activated);
                    Marshal.ThrowExceptionForHR(result);
                    completion.TrySetResult(activated);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }

            public void Cancel(CancellationToken cancellationToken) =>
                completion.TrySetCanceled(cancellationToken);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientActivationParameters
        {
            public int ActivationType;
            public AudioClientProcessLoopbackParameters ProcessLoopback;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientProcessLoopbackParameters
        {
            public uint TargetProcessId;
            public int ProcessLoopbackMode;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort VariantType;
            [FieldOffset(8)] public Blob Blob;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public uint Size;
            public IntPtr Data;
        }
    }
}
