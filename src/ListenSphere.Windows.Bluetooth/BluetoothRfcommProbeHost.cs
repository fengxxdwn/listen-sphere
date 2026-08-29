using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using InTheHand.Net.Sockets;
using ListenSphere.Device;
using ListenSphere.Audio.Engine;
using ListenSphere.Network;
using ListenSphere.Protocol;
using ListenSphere.Protocol.V1;

namespace ListenSphere.Windows.Bluetooth;

public sealed record BluetoothProbeEvent(string RemoteName, DateTimeOffset Timestamp);

public sealed record BluetoothHostFaultEvent(
    string RemoteName,
    string ExceptionType,
    string Message,
    DateTimeOffset Timestamp);

public sealed record BluetoothSessionEvent(
    DeviceDescriptor Device,
    DeviceConnectionState State,
    string Message,
    DateTimeOffset Timestamp,
    Guid SessionId,
    string? SourceName = null,
    string? SourceKind = null);

public sealed record BluetoothPcm16Frame(
    Guid DeviceId,
    Guid SessionId,
    ulong Timestamp,
    AudioCodec Codec,
    int ChannelCount,
    byte[] Pcm);

public sealed record BluetoothAudioSessionStatistics(
    Guid SessionId,
    AudioCodec Codec,
    int ChannelCount,
    long EncodedFrames,
    long DecodedFrames,
    long TimestampGaps,
    long QueueDrops,
    long DecodeFailures,
    int QueueDepth,
    double EstimatedJitterMilliseconds);

public sealed class BluetoothTimestampQualityTracker(
    int expectedFrameSamples,
    int sampleRate = 48_000)
{
    private ulong previousTimestamp;
    private long previousArrival;
    private bool initialized;
    private long timestampGaps;
    private double estimatedJitterMilliseconds;

    public long TimestampGaps => timestampGaps;
    public double EstimatedJitterMilliseconds => estimatedJitterMilliseconds;

    public void Observe(ulong timestamp, long arrivalTimestamp)
    {
        if (!initialized)
        {
            previousTimestamp = timestamp;
            previousArrival = arrivalTimestamp;
            initialized = true;
            return;
        }

        if (timestamp > previousTimestamp)
        {
            ulong sourceDelta = timestamp - previousTimestamp;
            if (sourceDelta > (ulong)expectedFrameSamples)
            {
                timestampGaps += checked((long)(sourceDelta / (ulong)expectedFrameSamples) - 1);
            }

            double arrivalMilliseconds =
                (arrivalTimestamp - previousArrival) * 1000d / Stopwatch.Frequency;
            double sourceMilliseconds = sourceDelta * 1000d / sampleRate;
            double deviation = Math.Abs(arrivalMilliseconds - sourceMilliseconds);
            estimatedJitterMilliseconds +=
                (Math.Min(deviation, 500d) - estimatedJitterMilliseconds) / 16d;
        }

        previousTimestamp = timestamp;
        previousArrival = arrivalTimestamp;
    }
}

public sealed class BluetoothRfcommProbeHost : IAsyncDisposable
{
    public static readonly Guid ServiceId = new("d8409f76-1c4f-4d6c-a34b-77d80e71f12a");
    public const int FrameSamples = 480;
    public const int AacFrameSamples = 1024;
    public const int PcmBytesPerChannel = FrameSamples * sizeof(short);
    public const int AdpcmBytesPerFrame = ImaAdpcmCodec.EncodedBytesPerFrame;
    public const uint SupportedCodecMask = (1u << 0) | (1u << 1) | (1u << 2);
    public const byte SupportedChannelMask = (1 << 0) | (1 << 1);

    private static ReadOnlySpan<byte> HandshakeMagic => "LSPB"u8;
    private static ReadOnlySpan<byte> AudioMagic => "LSAF"u8;
    private readonly LocalDeviceIdentity identity;
    private readonly ITrustedDeviceStore trustStore;
    private readonly PairingCodeService pairingCodes;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, BluetoothServerConnection> clients = new();
    private readonly ConcurrentDictionary<Guid, BluetoothSessionMonitor> sessionMonitors = new();
    private readonly ConcurrentDictionary<long, Task> handlers = new();
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly Channel<BluetoothPcm16Frame> audioFrames = Channel.CreateBounded<BluetoothPcm16Frame>(
        new BoundedChannelOptions(64)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private BluetoothListener? listener;
    private Task? acceptLoop;
    private long handlerId;
    private int disposed;

    public BluetoothRfcommProbeHost(
        LocalDeviceIdentity identity,
        ITrustedDeviceStore trustStore,
        PairingCodeService pairingCodes)
    {
        this.identity = identity;
        this.trustStore = trustStore;
        this.pairingCodes = pairingCodes;
    }

    public event EventHandler<BluetoothProbeEvent>? ProbeReceived;
    public event EventHandler<BluetoothSessionEvent>? SessionChanged;
    public event EventHandler<BluetoothHostFaultEvent>? Faulted;

    public bool IsListening => listener is not null && acceptLoop is { IsCompleted: false };

    public IReadOnlyList<BluetoothAudioSessionStatistics> SessionStatistics =>
        sessionMonitors.Values.Select(monitor => monitor.GetStatistics()).ToArray();

    public async IAsyncEnumerable<BluetoothPcm16Frame> ReadAudioFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (BluetoothPcm16Frame frame in
            audioFrames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (sessionMonitors.TryGetValue(frame.SessionId, out BluetoothSessionMonitor? monitor))
            {
                monitor.RecordDequeued();
            }
            yield return frame;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsListening)
            {
                return;
            }

            BluetoothListener? staleListener = listener;
            listener = null;
            staleListener?.Stop();
            staleListener?.Dispose();
            if (acceptLoop is { IsCompleted: true } completedLoop)
            {
                try { await completedLoop.ConfigureAwait(false); }
                catch (Exception) { }
                acceptLoop = null;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var next = new BluetoothListener(ServiceId)
                {
                    ServiceName = "ListenSphere Controller"
                };
                try
                {
                    next.Start();
                    listener = next;
                    acceptLoop = AcceptLoopAsync(next, lifetime.Token);
                    return;
                }
                catch (SocketException) when (attempt < 2)
                {
                    next.Dispose();
                    await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    next.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            startGate.Release();
        }
    }

    public async ValueTask DisconnectDeviceAsync(Guid deviceId)
    {
        if (clients.TryRemove(deviceId, out BluetoothServerConnection? connection))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SendDisconnectAsync(
                    connection.Stream,
                    ErrorCode.Unspecified,
                    "主控端已断开本次蓝牙连接，设备信任仍保留。",
                    timeout.Token).ConfigureAwait(false);
                // Give the RFCOMM stack a short window to deliver the framed reason
                // before closing the socket. Unexpected link loss still has no frame
                // and remains eligible for automatic reconnect on Android.
                await Task.Delay(75, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or ObjectDisposedException or
                    OperationCanceledException)
            {
            }
            finally
            {
                connection.Client.Dispose();
            }
        }
    }

    private async Task AcceptLoopAsync(BluetoothListener activeListener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                BluetoothClient client;
                try
                {
                    client = await activeListener.AcceptBluetoothClientAsync()
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                long id = Interlocked.Increment(ref handlerId);
                Task handler = HandleClientSafelyAsync(client, cancellationToken);
                handlers[id] = handler;
                _ = RemoveHandlerAsync(id, handler);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Faulted?.Invoke(this, new BluetoothHostFaultEvent(
                "本机蓝牙适配器",
                exception.GetType().Name,
                exception.Message,
                DateTimeOffset.UtcNow));
        }
        finally
        {
            if (ReferenceEquals(listener, activeListener)) listener = null;
            activeListener.Stop();
            activeListener.Dispose();
        }
    }

    private async Task RemoveHandlerAsync(long id, Task handler)
    {
        try { await handler.ConfigureAwait(false); }
        finally { handlers.TryRemove(id, out _); }
    }

    private async Task HandleClientSafelyAsync(BluetoothClient client, CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
        }
        catch (Exception exception)
        {
            client.Dispose();
            string remoteName;
            try { remoteName = client.RemoteMachineName; }
            catch { remoteName = "未知蓝牙设备"; }
            Faulted?.Invoke(this, new BluetoothHostFaultEvent(
                remoteName,
                exception.GetType().Name,
                exception.Message,
                DateTimeOffset.UtcNow));
        }
    }

    private async Task HandleClientAsync(BluetoothClient client, CancellationToken cancellationToken)
    {
        DeviceDescriptor? remoteDevice = null;
        BluetoothServerConnection? registeredConnection = null;
        Guid sessionId = Guid.Empty;
        string? sourceName = null;
        string? sourceKind = null;
        try
        {
            using (client)
            {
                Stream stream = client.GetStream();
            byte[] request = new byte[6];
            await stream.ReadExactlyAsync(request, cancellationToken).ConfigureAwait(false);
            if (!request.AsSpan(0, 4).SequenceEqual(HandshakeMagic) || request[4] != 1)
            {
                return;
            }

            byte requestedMode = request[5];
            bool negotiatedSession = (requestedMode & 0x80) != 0;
            bool streamRequestFollows = requestedMode == 3;
            bool sessionRequested = requestedMode == 1 || streamRequestFollows || negotiatedSession;
            bool capabilitiesRequested = requestedMode == 2 || negotiatedSession;
            AudioCodec selectedCodec = negotiatedSession
                ? (AudioCodec)((requestedMode >> 2) & 0x0f)
                : AudioCodec.ImaAdpcm;
            int selectedChannels = negotiatedSession ? (requestedMode & 0x01) + 1 : 1;
            await WriteHandshakeResponseAsync(
                    stream, sessionRequested, capabilitiesRequested, cancellationToken)
                .ConfigureAwait(false);
            ProbeReceived?.Invoke(this, new BluetoothProbeEvent(
                client.RemoteMachineName, DateTimeOffset.UtcNow));
            if (!sessionRequested)
            {
                return;
            }
            if (!streamRequestFollows && !IsSupported(selectedCodec, selectedChannels))
            {
                return;
            }

            Envelope? hello = await ControlFrameCodec.ReadAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (hello?.HelloRequest?.Device is not { } remoteIdentity ||
                !ProtocolConstants.IsCompatible(hello.Version))
            {
                await SendDisconnectAsync(stream, ErrorCode.IncompatibleVersion,
                    "蓝牙控制握手无效。", cancellationToken).ConfigureAwait(false);
                return;
            }

            byte[] fingerprint = DeviceProof.Validate(
                hello.HelloRequest, identity.CertificateFingerprint);
            sourceName = hello.HelloRequest.SourceName;
            sourceKind = hello.HelloRequest.SourceKind;
            remoteDevice = ProtocolIdentity.ToDescriptor(remoteIdentity);
            TrustedDevice? trusted = await trustStore.FindAsync(
                remoteDevice.DeviceId, cancellationToken).ConfigureAwait(false);
            bool paired = trusted is not null && FingerprintMatches(
                trusted.CertificateFingerprint, fingerprint);
            await SendHelloAsync(stream, hello.RequestId, paired, cancellationToken)
                .ConfigureAwait(false);
            if (!paired && !await CompletePairingAsync(
                    stream, remoteDevice, fingerprint, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (streamRequestFollows)
            {
                Envelope? formatRequest = await ControlFrameCodec.ReadAsync(
                        stream, cancellationToken)
                    .ConfigureAwait(false);
                if (formatRequest?.StartStream is not { } requestedFormat)
                {
                    await SendDisconnectAsync(
                        stream,
                        ErrorCode.StreamRejected,
                        "缺少蓝牙音频格式请求。",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                selectedCodec = (AudioCodec)requestedFormat.Codec;
                selectedChannels = checked((int)requestedFormat.ChannelCount);
                int expectedFrameSamples = FrameSamplesFor(selectedCodec);
                int expectedFrameDuration = FrameDurationFor(selectedCodec);
                if (!IsSupported(selectedCodec, selectedChannels) ||
                    requestedFormat.SampleRate != 48_000 ||
                    requestedFormat.FrameDurationMs != expectedFrameDuration ||
                    requestedFormat.FrameSamples != expectedFrameSamples)
                {
                    await SendDisconnectAsync(
                        stream,
                        ErrorCode.StreamRejected,
                        "主控端不支持请求的蓝牙音频格式。",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            // A device may originally have been paired over Wi-Fi.  Successful
            // certificate-pinned Bluetooth reconnects must still update the durable
            // record so the Controller lists the device under Bluetooth after restart.
            DateTimeOffset connectedAt = DateTimeOffset.UtcNow;
            await trustStore.UpsertAsync(new TrustedDevice(
                remoteDevice.DeviceId,
                remoteDevice.DisplayName,
                remoteDevice.Platform.ToString(),
                (ulong)remoteDevice.Capabilities,
                Convert.ToHexString(fingerprint),
                trusted?.PairedAt ?? connectedAt,
                connectedAt,
                "Bluetooth"),
                cancellationToken).ConfigureAwait(false);

            registeredConnection = new BluetoothServerConnection(client, stream);
            clients.AddOrUpdate(remoteDevice.DeviceId, registeredConnection, (_, previous) =>
            {
                previous.Client.Dispose();
                return registeredConnection;
            });
            sessionId = Guid.NewGuid();
            sessionMonitors[sessionId] = new BluetoothSessionMonitor(
                sessionId,
                selectedCodec,
                selectedChannels,
                FrameSamplesFor(selectedCodec));
            await SendStartStreamAsync(
                    stream, sessionId, selectedCodec, selectedChannels, cancellationToken)
                .ConfigureAwait(false);
            SessionChanged?.Invoke(this, new BluetoothSessionEvent(
                remoteDevice, DeviceConnectionState.Streaming,
                $"蓝牙音频正在传输：{CodecLabel(selectedCodec)} · " +
                $"{(selectedChannels == 2 ? "双声道" : "单声道")}。",
                DateTimeOffset.UtcNow, sessionId, sourceName, sourceKind));

                await ReceiveAudioAsync(
                        stream, remoteDevice.DeviceId, sessionId,
                        selectedCodec, selectedChannels, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (remoteDevice is not null)
            {
                if (registeredConnection is not null)
                {
                    clients.TryRemove(new KeyValuePair<Guid, BluetoothServerConnection>(
                        remoteDevice.DeviceId,
                        registeredConnection));
                }
                SessionChanged?.Invoke(this, new BluetoothSessionEvent(
                    remoteDevice, DeviceConnectionState.Offline,
                    "蓝牙音频连接已断开。", DateTimeOffset.UtcNow, sessionId,
                    sourceName, sourceKind));
            }
            if (sessionId != Guid.Empty)
            {
                sessionMonitors.TryRemove(sessionId, out _);
            }
        }
    }

    private async Task WriteHandshakeResponseAsync(
        Stream stream,
        bool includeCertificate,
        bool includeCapabilities,
        CancellationToken cancellationToken)
    {
        byte[] name = System.Text.Encoding.UTF8.GetBytes(identity.Device.DisplayName);
        byte[] certificate = includeCertificate ? identity.Certificate.RawData : [];
        const byte certificateIncludedFlag = 0x01;
        const byte controllerIdIncludedFlag = 0x02;
        int capabilityLength = includeCapabilities ? 5 + 16 : 0;
        byte[] response = new byte[10 + name.Length + certificate.Length + capabilityLength];
        HandshakeMagic.CopyTo(response);
        response[4] = 1;
        response[5] = (byte)(
            (includeCertificate ? certificateIncludedFlag : 0) |
            (includeCapabilities ? controllerIdIncludedFlag : 0));
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), checked((ushort)name.Length));
        name.CopyTo(response, 8);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(8 + name.Length),
            checked((ushort)certificate.Length));
        certificate.CopyTo(response, 10 + name.Length);
        if (includeCapabilities)
        {
            int offset = 10 + name.Length + certificate.Length;
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(offset), SupportedCodecMask);
            response[offset + sizeof(uint)] = SupportedChannelMask;
            identity.Device.DeviceId.ToByteArray()
                .CopyTo(response, offset + sizeof(uint) + sizeof(byte));
        }
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendHelloAsync(
        Stream stream, ulong requestId, bool paired, CancellationToken cancellationToken)
    {
        Envelope response = ProtocolConstants.CreateEnvelope(requestId);
        response.HelloResponse = new HelloResponse
        {
            Controller = identity.ToProtocolIdentity(),
            Paired = paired,
            Error = paired ? ErrorCode.Unspecified : ErrorCode.NotPaired
        };
        await ControlFrameCodec.WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CompletePairingAsync(
        Stream stream, DeviceDescriptor device, byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        Envelope? request = await ControlFrameCodec.ReadAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        if (request?.PairRequest is not { } pairRequest)
        {
            return false;
        }

        PairingCodeValidation validation = pairingCodes.ValidateAndConsume(
            pairRequest.OneTimeCode,
            $"Bluetooth:{device.DeviceId:D}");
        bool accepted = validation == PairingCodeValidation.Accepted;
        ErrorCode error = validation switch
        {
            PairingCodeValidation.Accepted => ErrorCode.Unspecified,
            PairingCodeValidation.RateLimited => ErrorCode.PairingRateLimited,
            _ => ErrorCode.PairingCodeInvalid
        };
        if (accepted)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await trustStore.UpsertAsync(new TrustedDevice(
                device.DeviceId, device.DisplayName, device.Platform.ToString(),
                (ulong)device.Capabilities, Convert.ToHexString(fingerprint), now, now,
                "Bluetooth"),
                cancellationToken).ConfigureAwait(false);
        }

        Envelope response = ProtocolConstants.CreateEnvelope(request.RequestId);
        response.PairResponse = new PairResponse
        {
            Accepted = accepted,
            Error = error,
            ControllerNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            Controller = identity.ToProtocolIdentity()
        };
        await ControlFrameCodec.WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
        return accepted;
    }

    private static async Task SendStartStreamAsync(
        Stream stream,
        Guid sessionId,
        AudioCodec codec,
        int channelCount,
        CancellationToken cancellationToken)
    {
        Envelope response = ProtocolConstants.CreateEnvelope(0);
        response.StartStream = new StartStream
        {
            StreamId = 1,
            Codec = (uint)codec,
            SampleRate = 48_000,
            ChannelCount = (uint)channelCount,
            FrameDurationMs = (uint)FrameDurationFor(codec),
            SessionId = ByteString.CopyFrom(sessionId.ToByteArray()),
            FrameSamples = (uint)FrameSamplesFor(codec)
        };
        await ControlFrameCodec.WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveAudioAsync(
        Stream stream,
        Guid deviceId,
        Guid sessionId,
        AudioCodec codec,
        int channelCount,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[16];
        int expectedPayloadLength = codec == AudioCodec.ImaAdpcm
            ? AdpcmBytesPerFrame * channelCount
            : PcmBytesPerChannel * channelCount;
        using AacAdtsDecoder? aacDecoder = codec == AudioCodec.AacLc
            ? new AacAdtsDecoder(channelCount)
            : null;
        var aacPcm = new List<byte>(AacFrameSamples * channelCount * sizeof(short) * 2);
        ulong outputTimestamp = 0;
        bool hasOutputTimestamp = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            int payloadLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)));
            bool validPayloadLength = codec == AudioCodec.AacLc
                ? payloadLength is > 7 and <= 8192
                : payloadLength == expectedPayloadLength;
            if (!header.AsSpan(0, 4).SequenceEqual(AudioMagic) || !validPayloadLength)
            {
                throw new InvalidDataException("Bluetooth audio frame header is invalid.");
            }

            ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
            BluetoothSessionMonitor monitor = sessionMonitors[sessionId];
            monitor.RecordEncodedFrame(timestamp);
            byte[] encoded = new byte[payloadLength];
            await stream.ReadExactlyAsync(encoded, cancellationToken).ConfigureAwait(false);
            if (codec == AudioCodec.AacLc)
            {
                if (!hasOutputTimestamp)
                {
                    outputTimestamp = timestamp;
                    hasOutputTimestamp = true;
                }
                try
                {
                    aacPcm.AddRange(aacDecoder!.Decode(encoded));
                }
                catch
                {
                    monitor.RecordDecodeFailure();
                    throw;
                }
                int outputFrameBytes = PcmBytesPerChannel * channelCount;
                while (aacPcm.Count >= outputFrameBytes)
                {
                    byte[] outputFrame = aacPcm.GetRange(0, outputFrameBytes).ToArray();
                    aacPcm.RemoveRange(0, outputFrameBytes);
                    PublishAudioFrame(new BluetoothPcm16Frame(
                        deviceId, sessionId, outputTimestamp, codec, channelCount, outputFrame));
                    outputTimestamp += FrameSamples;
                }
                continue;
            }
            byte[] pcm;
            if (codec == AudioCodec.PcmInt16)
            {
                pcm = encoded;
            }
            else
            {
                pcm = new byte[PcmBytesPerChannel * channelCount];
                var channelSamples = new short[FrameSamples];
                for (int channel = 0; channel < channelCount; channel++)
                {
                    ImaAdpcmCodec.Decode(
                        encoded.AsSpan(channel * AdpcmBytesPerFrame, AdpcmBytesPerFrame),
                        channelSamples);
                    for (int sample = 0; sample < FrameSamples; sample++)
                    {
                        int outputIndex = (sample * channelCount) + channel;
                        BinaryPrimitives.WriteInt16LittleEndian(
                            pcm.AsSpan(outputIndex * sizeof(short)), channelSamples[sample]);
                    }
                }
            }
            PublishAudioFrame(new BluetoothPcm16Frame(
                deviceId, sessionId, timestamp, codec, channelCount, pcm));
        }
    }

    private void PublishAudioFrame(BluetoothPcm16Frame frame)
    {
        if (!sessionMonitors.TryGetValue(frame.SessionId, out BluetoothSessionMonitor? monitor))
        {
            return;
        }

        monitor.RecordDecodedFrame();
        if (!audioFrames.Writer.TryWrite(frame))
        {
            if (audioFrames.Reader.TryRead(out BluetoothPcm16Frame? dropped) &&
                sessionMonitors.TryGetValue(
                    dropped.SessionId,
                    out BluetoothSessionMonitor? droppedMonitor))
            {
                droppedMonitor.RecordQueueDrop();
                droppedMonitor.RecordDequeued();
            }

            if (!audioFrames.Writer.TryWrite(frame))
            {
                monitor.RecordQueueDrop();
                return;
            }
        }

        monitor.RecordQueued();
    }

    private static bool IsSupported(AudioCodec codec, int channelCount) =>
        channelCount is 1 or 2 &&
        codec is AudioCodec.PcmInt16 or AudioCodec.ImaAdpcm or AudioCodec.AacLc;

    private static int FrameSamplesFor(AudioCodec codec) =>
        codec == AudioCodec.AacLc ? AacFrameSamples : FrameSamples;

    private static int FrameDurationFor(AudioCodec codec) =>
        codec == AudioCodec.AacLc ? 21 : 10;

    private static string CodecLabel(AudioCodec codec) => codec switch
    {
        AudioCodec.PcmInt16 => "PCM16",
        AudioCodec.ImaAdpcm => "IMA ADPCM",
        AudioCodec.AacLc => "AAC-LC",
        _ => codec.ToString()
    };

    private static async Task SendDisconnectAsync(
        Stream stream, ErrorCode error, string reason, CancellationToken cancellationToken)
    {
        Envelope response = ProtocolConstants.CreateEnvelope(0);
        response.Disconnect = new Disconnect { Error = error, Reason = reason };
        await ControlFrameCodec.WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
    }

    private static bool FingerprintMatches(string expectedHex, ReadOnlySpan<byte> actual)
    {
        try
        {
            byte[] expected = Convert.FromHexString(expectedHex);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        listener?.Stop();
        listener = null;
        foreach (BluetoothServerConnection connection in clients.Values)
        {
            connection.Client.Dispose();
        }
        clients.Clear();
        if (acceptLoop is not null)
        {
            try { await acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        try { await Task.WhenAll(handlers.Values).ConfigureAwait(false); }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        audioFrames.Writer.TryComplete();
        sessionMonitors.Clear();
        startGate.Dispose();
        lifetime.Dispose();
    }
}

internal sealed class BluetoothSessionMonitor(
    Guid sessionId,
    AudioCodec codec,
    int channelCount,
    int expectedFrameSamples)
{
    private readonly BluetoothTimestampQualityTracker timestampTracker =
        new(expectedFrameSamples);
    private long encodedFrames;
    private long decodedFrames;
    private long queueDrops;
    private long decodeFailures;
    private int queueDepth;

    public void RecordEncodedFrame(ulong timestamp)
    {
        Interlocked.Increment(ref encodedFrames);
        timestampTracker.Observe(timestamp, Stopwatch.GetTimestamp());
    }

    public void RecordDecodedFrame() => Interlocked.Increment(ref decodedFrames);
    public void RecordQueueDrop() => Interlocked.Increment(ref queueDrops);
    public void RecordDecodeFailure() => Interlocked.Increment(ref decodeFailures);
    public void RecordQueued() => Interlocked.Increment(ref queueDepth);
    public void RecordDequeued()
    {
        if (Interlocked.Decrement(ref queueDepth) < 0)
        {
            Interlocked.Exchange(ref queueDepth, 0);
        }
    }

    public BluetoothAudioSessionStatistics GetStatistics() => new(
        sessionId,
        codec,
        channelCount,
        Interlocked.Read(ref encodedFrames),
        Interlocked.Read(ref decodedFrames),
        timestampTracker.TimestampGaps,
        Interlocked.Read(ref queueDrops),
        Interlocked.Read(ref decodeFailures),
        Math.Max(0, Volatile.Read(ref queueDepth)),
        timestampTracker.EstimatedJitterMilliseconds);
}

internal sealed record BluetoothServerConnection(BluetoothClient Client, Stream Stream);
