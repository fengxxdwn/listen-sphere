using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using ListenSphere.Device;
using ListenSphere.Network;
using ListenSphere.Protocol;
using ListenSphere.Protocol.V1;

namespace ListenSphere.Windows.Usb;

public sealed record UsbAccessoryStatusEvent(string Message, DateTimeOffset Timestamp);

public sealed record UsbAccessoryFaultEvent(
    string ExceptionType,
    string Message,
    DateTimeOffset Timestamp);

public sealed record UsbAccessorySessionEvent(
    DeviceDescriptor Device,
    DeviceConnectionState State,
    string Message,
    DateTimeOffset Timestamp,
    Guid SessionId,
    string? SourceName = null,
    string? SourceKind = null);

public sealed record UsbPcm16Frame(
    Guid DeviceId,
    Guid SessionId,
    ulong Timestamp,
    int ChannelCount,
    byte[] Pcm);

public sealed class UsbAccessoryHost : IAsyncDisposable
{
    public const int FrameSamples = 480;
    public const int ChannelCount = 2;
    public const int PcmBytesPerFrame = FrameSamples * ChannelCount * sizeof(short);

    private static ReadOnlySpan<byte> RequestMagic => "LSUR"u8;
    private static ReadOnlySpan<byte> ResponseMagic => "LSUH"u8;
    private readonly LocalDeviceIdentity identity;
    private readonly ITrustedDeviceStore trustStore;
    private readonly PairingCodeService pairingCodes;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, WinUsbBulkConnection> connections = [];
    private readonly System.Threading.Channels.Channel<UsbPcm16Frame> audioFrames =
        System.Threading.Channels.Channel.CreateBounded<UsbPcm16Frame>(
            new System.Threading.Channels.BoundedChannelOptions(96)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });
    private readonly SemaphoreSlim startGate = new(1, 1);
    private Task? monitorLoop;
    private int disposed;

    public UsbAccessoryHost(
        LocalDeviceIdentity identity,
        ITrustedDeviceStore trustStore,
        PairingCodeService pairingCodes)
    {
        this.identity = identity;
        this.trustStore = trustStore;
        this.pairingCodes = pairingCodes;
    }

    public event EventHandler<UsbAccessoryStatusEvent>? StatusChanged;
    public event EventHandler<UsbAccessorySessionEvent>? SessionChanged;
    public event EventHandler<UsbAccessoryFaultEvent>? Faulted;

    public bool IsRunning => monitorLoop is { IsCompleted: false };

    public IAsyncEnumerable<UsbPcm16Frame> ReadAudioFramesAsync(
        CancellationToken cancellationToken = default) =>
        audioFrames.Reader.ReadAllAsync(cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            monitorLoop = MonitorAsync(lifetime.Token);
            StatusChanged?.Invoke(this, new UsbAccessoryStatusEvent(
                "正在等待原生 USB 设备", DateTimeOffset.UtcNow));
        }
        finally
        {
            startGate.Release();
        }
    }

    public ValueTask DisconnectDeviceAsync(Guid deviceId)
    {
        if (connections.TryRemove(deviceId, out WinUsbBulkConnection? connection))
        {
            return connection.DisposeAsync();
        }
        return ValueTask.CompletedTask;
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        string? activePath = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<string> paths = WinUsbBulkConnection.EnumerateDevicePaths();
                string? accessoryPath = paths.FirstOrDefault(WinUsbBulkConnection.IsAccessoryPath);
                if (accessoryPath is not null &&
                    !string.Equals(activePath, accessoryPath, StringComparison.OrdinalIgnoreCase))
                {
                    WinUsbBulkConnection? connection =
                        WinUsbBulkConnection.TryOpenAccessory(accessoryPath);
                    if (connection is not null)
                    {
                        activePath = accessoryPath;
                        StatusChanged?.Invoke(this, new UsbAccessoryStatusEvent(
                            "检测到 Android 原生 USB 通道", DateTimeOffset.UtcNow));
                        await HandleConnectionSafelyAsync(connection, cancellationToken)
                            .ConfigureAwait(false);
                        activePath = null;
                    }
                }
                else if (accessoryPath is null)
                {
                    foreach (string path in paths)
                    {
                        if (WinUsbBulkConnection.TryActivateAccessory(
                            path, identity.Device.DeviceId.ToString("N")))
                        {
                            StatusChanged?.Invoke(this, new UsbAccessoryStatusEvent(
                                "已请求手机切换到 ListenSphere USB 模式",
                                DateTimeOffset.UtcNow));
                            break;
                        }
                    }
                }

                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Faulted?.Invoke(this, new UsbAccessoryFaultEvent(
                exception.GetType().Name, exception.Message, DateTimeOffset.UtcNow));
        }
    }

    private async Task HandleConnectionSafelyAsync(
        WinUsbBulkConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Faulted?.Invoke(this, new UsbAccessoryFaultEvent(
                exception.GetType().Name, exception.Message, DateTimeOffset.UtcNow));
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(
        WinUsbBulkConnection connection,
        CancellationToken cancellationToken)
    {
        DeviceDescriptor? remoteDevice = null;
        Guid sessionId = Guid.Empty;
        string? sourceName = null;
        string? sourceKind = null;
        try
        {
            UsbFrame request = await ReadFrameAsync(connection, cancellationToken).ConfigureAwait(false);
            if (request.Header.Kind != UsbTransportFrameKind.Status ||
                request.Payload.Length != 5 ||
                !request.Payload.AsSpan(0, 4).SequenceEqual(RequestMagic) ||
                request.Payload[4] != 1)
            {
                throw new InvalidDataException("Android USB 握手请求无效。");
            }
            await SendIdentityAsync(connection, cancellationToken).ConfigureAwait(false);

            Envelope hello = await ReadControlAsync(connection, cancellationToken).ConfigureAwait(false);
            if (hello.HelloRequest?.Device is not { } remoteIdentity ||
                !ProtocolConstants.IsCompatible(hello.Version))
            {
                throw new InvalidDataException("Android USB 控制握手无效。");
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
            await SendHelloAsync(connection, hello.RequestId, paired, cancellationToken)
                .ConfigureAwait(false);
            if (!paired && !await CompletePairingAsync(
                connection, remoteDevice, fingerprint, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            Envelope format = await ReadControlAsync(connection, cancellationToken).ConfigureAwait(false);
            if (format.StartStream is not { } requested ||
                requested.Codec != (uint)AudioCodec.PcmInt16 ||
                requested.SampleRate != 48_000 ||
                requested.ChannelCount != ChannelCount ||
                requested.FrameSamples != FrameSamples ||
                requested.FrameDurationMs != 10)
            {
                throw new InvalidDataException("Android 请求了不受支持的 USB 音频格式。");
            }

            DateTimeOffset connectedAt = DateTimeOffset.UtcNow;
            await trustStore.UpsertAsync(new TrustedDevice(
                remoteDevice.DeviceId,
                remoteDevice.DisplayName,
                remoteDevice.Platform.ToString(),
                (ulong)remoteDevice.Capabilities,
                Convert.ToHexString(fingerprint),
                trusted?.PairedAt ?? connectedAt,
                connectedAt,
                "Wired"),
                cancellationToken).ConfigureAwait(false);

            sessionId = Guid.NewGuid();
            connections.AddOrUpdate(remoteDevice.DeviceId, connection, (deviceId, previous) =>
            {
                _ = previous.DisposeAsync();
                return connection;
            });
            await SendStartStreamAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
            SessionChanged?.Invoke(this, new UsbAccessorySessionEvent(
                remoteDevice,
                DeviceConnectionState.Streaming,
                "原生 USB 音频正在传输：PCM16 · 双声道。",
                DateTimeOffset.UtcNow,
                sessionId,
                sourceName,
                sourceKind));

            while (!cancellationToken.IsCancellationRequested)
            {
                UsbFrame audio = await ReadFrameAsync(connection, cancellationToken).ConfigureAwait(false);
                if (audio.Header.Kind == UsbTransportFrameKind.KeepAlive) continue;
                if (audio.Header.Kind != UsbTransportFrameKind.Audio ||
                    audio.Payload.Length != PcmBytesPerFrame)
                {
                    throw new InvalidDataException("Android USB 音频帧无效。");
                }
                audioFrames.Writer.TryWrite(new UsbPcm16Frame(
                    remoteDevice.DeviceId,
                    sessionId,
                    audio.Header.Timestamp,
                    ChannelCount,
                    audio.Payload));
            }
        }
        finally
        {
            if (remoteDevice is not null)
            {
                connections.TryRemove(
                    new KeyValuePair<Guid, WinUsbBulkConnection>(remoteDevice.DeviceId, connection));
                SessionChanged?.Invoke(this, new UsbAccessorySessionEvent(
                    remoteDevice,
                    DeviceConnectionState.Offline,
                    "原生 USB 音频连接已断开。",
                    DateTimeOffset.UtcNow,
                    sessionId,
                    sourceName,
                    sourceKind));
            }
        }
    }

    private async Task SendIdentityAsync(
        WinUsbBulkConnection connection,
        CancellationToken cancellationToken)
    {
        byte[] name = System.Text.Encoding.UTF8.GetBytes(identity.Device.DisplayName);
        byte[] certificate = identity.Certificate.RawData;
        byte[] payload = new byte[9 + name.Length + certificate.Length];
        ResponseMagic.CopyTo(payload);
        payload[4] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(5), checked((ushort)name.Length));
        name.CopyTo(payload, 7);
        BinaryPrimitives.WriteUInt16BigEndian(
            payload.AsSpan(7 + name.Length), checked((ushort)certificate.Length));
        certificate.CopyTo(payload, 9 + name.Length);
        await SendFrameAsync(connection, UsbTransportFrameKind.Status, payload, 0, 0, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendHelloAsync(
        WinUsbBulkConnection connection,
        ulong requestId,
        bool paired,
        CancellationToken cancellationToken)
    {
        Envelope response = ProtocolConstants.CreateEnvelope(requestId);
        response.HelloResponse = new HelloResponse
        {
            Controller = identity.ToProtocolIdentity(),
            Paired = paired,
            Error = paired ? ErrorCode.Unspecified : ErrorCode.NotPaired
        };
        await SendControlAsync(connection, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CompletePairingAsync(
        WinUsbBulkConnection connection,
        DeviceDescriptor device,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        Envelope request = await ReadControlAsync(connection, cancellationToken).ConfigureAwait(false);
        if (request.PairRequest is not { } pairRequest) return false;
        PairingCodeValidation validation = pairingCodes.ValidateAndConsume(
            pairRequest.OneTimeCode,
            $"Wired:{device.DeviceId:D}");
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
                device.DeviceId,
                device.DisplayName,
                device.Platform.ToString(),
                (ulong)device.Capabilities,
                Convert.ToHexString(fingerprint),
                now,
                now,
                "Wired"),
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
        await SendControlAsync(connection, response, cancellationToken).ConfigureAwait(false);
        return accepted;
    }

    private static async Task SendStartStreamAsync(
        WinUsbBulkConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        Envelope response = ProtocolConstants.CreateEnvelope(0);
        response.StartStream = new StartStream
        {
            StreamId = 1,
            Codec = (uint)AudioCodec.PcmInt16,
            SampleRate = 48_000,
            ChannelCount = ChannelCount,
            FrameDurationMs = 10,
            FrameSamples = FrameSamples,
            SessionId = ByteString.CopyFrom(sessionId.ToByteArray())
        };
        await SendControlAsync(connection, response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Envelope> ReadControlAsync(
        WinUsbBulkConnection connection,
        CancellationToken cancellationToken)
    {
        UsbFrame frame = await ReadFrameAsync(connection, cancellationToken).ConfigureAwait(false);
        if (frame.Header.Kind != UsbTransportFrameKind.Control)
        {
            throw new InvalidDataException("USB 会话收到非控制帧。");
        }
        return Envelope.Parser.ParseFrom(frame.Payload);
    }

    private static Task SendControlAsync(
        WinUsbBulkConnection connection,
        Envelope envelope,
        CancellationToken cancellationToken) =>
        SendFrameAsync(
            connection,
            UsbTransportFrameKind.Control,
            envelope.ToByteArray(),
            checked((uint)envelope.RequestId),
            0,
            cancellationToken);

    private static async Task<UsbFrame> ReadFrameAsync(
        WinUsbBulkConnection connection,
        CancellationToken cancellationToken)
    {
        byte[] headerBytes = new byte[UsbTransportFrame.HeaderLength];
        await connection.ReadExactlyAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (!UsbTransportFrame.TryReadHeader(headerBytes, out UsbTransportFrameHeader header))
        {
            throw new InvalidDataException("USB 传输帧头无效。");
        }
        byte[] payload = new byte[header.PayloadLength];
        if (payload.Length > 0)
        {
            await connection.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        return new UsbFrame(header, payload);
    }

    private static Task SendFrameAsync(
        WinUsbBulkConnection connection,
        UsbTransportFrameKind kind,
        ReadOnlyMemory<byte> payload,
        uint sequence,
        ulong timestamp,
        CancellationToken cancellationToken) =>
        connection.WriteAsync(
            UsbTransportFrame.Encode(kind, payload.Span, sequence, timestamp), cancellationToken)
            .AsTask();

    private static bool FingerprintMatches(string expectedHex, ReadOnlySpan<byte> actual)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHex), actual);
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
        foreach (WinUsbBulkConnection connection in connections.Values)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        connections.Clear();
        if (monitorLoop is not null)
        {
            try { await monitorLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        audioFrames.Writer.TryComplete();
        startGate.Dispose();
        lifetime.Dispose();
    }

    private sealed record UsbFrame(UsbTransportFrameHeader Header, byte[] Payload);
}
