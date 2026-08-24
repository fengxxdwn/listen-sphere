using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using ListenSphere.Device;
using ListenSphere.Protocol;
using ListenSphere.Protocol.V1;

namespace ListenSphere.Network;

public sealed record ControlPeerEvent(
    DeviceDescriptor? Device,
    DeviceConnectionState State,
    string Message,
    DateTimeOffset Timestamp,
    Guid? AudioSessionId = null);

public sealed class ListenSphereControlServer : IAsyncDisposable
{
    private readonly LocalDeviceIdentity identity;
    private readonly ITrustedDeviceStore trustStore;
    private readonly PairingCodeService pairingCodes;
    private readonly UdpAudioReceiver audioReceiver = new();
    private readonly ConcurrentDictionary<Guid, TcpClient> clients = new();
    private readonly ConcurrentDictionary<long, Task> clientHandlers = new();
    private readonly CancellationTokenSource lifetime = new();
    private TcpListener? listener;
    private Task? acceptLoop;
    private long clientHandlerId;
    private int disposed;

    public ListenSphereControlServer(
        LocalDeviceIdentity identity,
        ITrustedDeviceStore trustStore,
        PairingCodeService pairingCodes)
    {
        this.identity = identity;
        this.trustStore = trustStore;
        this.pairingCodes = pairingCodes;
    }

    public event EventHandler<ControlPeerEvent>? PeerChanged;

    public int Port { get; private set; }
    public UdpAudioReceiver AudioReceiver => audioReceiver;

    public async Task StartAsync(int port = 0, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        if (listener is not null)
        {
            throw new InvalidOperationException("The control server is already running.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await audioReceiver.StartAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        acceptLoop = AcceptLoopAsync(lifetime.Token);
    }

    public async ValueTask RevokeAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        await trustStore.RemoveAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (clients.TryRemove(deviceId, out TcpClient? client))
        {
            client.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener!.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                long handlerId = Interlocked.Increment(ref clientHandlerId);
                Task handler = HandleClientSafelyAsync(client, cancellationToken);
                clientHandlers[handlerId] = handler;
                _ = RemoveClientHandlerWhenCompleteAsync(handlerId, handler);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PeerChanged?.Invoke(
                this,
                new ControlPeerEvent(
                    null,
                    DeviceConnectionState.Offline,
                    "控制服务已停止。",
                    DateTimeOffset.UtcNow));
        }
        catch (SocketException exception) when (cancellationToken.IsCancellationRequested)
        {
            PeerChanged?.Invoke(
                this,
                new ControlPeerEvent(
                    null,
                    DeviceConnectionState.Offline,
                    $"控制服务关闭：{exception.SocketErrorCode}",
                    DateTimeOffset.UtcNow));
        }
    }

    private async Task HandleClientSafelyAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            client.Dispose();
            PeerChanged?.Invoke(
                this,
                new ControlPeerEvent(
                    null,
                    DeviceConnectionState.Faulted,
                    $"控制客户端处理失败：{exception.Message}",
                    DateTimeOffset.UtcNow));
        }
    }

    private async Task RemoveClientHandlerWhenCompleteAsync(long handlerId, Task handler)
    {
        await handler.ConfigureAwait(false);
        clientHandlers.TryRemove(handlerId, out _);
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        DeviceDescriptor? remoteDevice = null;
        AudioSessionParameters? audioSession = null;
        using (client)
        await using (var ssl = new SslStream(
            client.GetStream(),
            false,
            static (_, certificate, _, _) => certificate is not null))
        {
            try
            {
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = identity.Certificate,
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    },
                    cancellationToken).ConfigureAwait(false);

                byte[] tlsFingerprint = GetRemoteFingerprint(ssl);
                Envelope? helloEnvelope = await ControlFrameCodec.ReadAsync(ssl, cancellationToken)
                    .ConfigureAwait(false);
                if (helloEnvelope?.HelloRequest?.Device is not { } remoteIdentity)
                {
                    await SendDisconnectAsync(
                        ssl,
                        ErrorCode.Internal,
                        "首个控制消息必须是 HelloRequest。",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!ProtocolConstants.IsCompatible(helloEnvelope.Version))
                {
                    await SendHelloAsync(
                        ssl,
                        helloEnvelope.RequestId,
                        false,
                        ErrorCode.IncompatibleVersion,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                remoteDevice = ProtocolIdentity.ToDescriptor(remoteIdentity);
                if (!CryptographicOperations.FixedTimeEquals(
                    tlsFingerprint,
                    remoteIdentity.CertificateFingerprint.Span))
                {
                    await SendDisconnectAsync(
                        ssl,
                        ErrorCode.NotPaired,
                        "TLS 证书与设备身份指纹不一致。",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                TrustedDevice? trusted = await trustStore.FindAsync(
                    remoteDevice.DeviceId,
                    cancellationToken).ConfigureAwait(false);
                bool paired = trusted is not null &&
                    FingerprintMatches(trusted.CertificateFingerprint, tlsFingerprint);
                await SendHelloAsync(
                    ssl,
                    helloEnvelope.RequestId,
                    paired,
                    paired ? ErrorCode.Unspecified : ErrorCode.NotPaired,
                    cancellationToken).ConfigureAwait(false);

                if (!paired)
                {
                    paired = await CompletePairingAsync(
                        ssl,
                        remoteDevice,
                        tlsFingerprint,
                        cancellationToken).ConfigureAwait(false);
                    if (!paired)
                    {
                        return;
                    }
                }

                clients.AddOrUpdate(
                    remoteDevice.DeviceId,
                    client,
                    (_, previous) =>
                    {
                        previous.Dispose();
                        return client;
                    });
                PeerChanged?.Invoke(
                    this,
                    new ControlPeerEvent(
                        remoteDevice,
                        DeviceConnectionState.Connected,
                        "控制通道已通过 TLS 1.3 认证。",
                        DateTimeOffset.UtcNow));

                IPAddress remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                audioSession = CreateAudioSession(remoteAddress);
                audioReceiver.RegisterSession(audioSession);
                await SendStartStreamAsync(ssl, audioSession, cancellationToken)
                    .ConfigureAwait(false);
                PeerChanged?.Invoke(
                    this,
                    new ControlPeerEvent(
                        remoteDevice,
                        DeviceConnectionState.Streaming,
                        "远程音频流已就绪。",
                        DateTimeOffset.UtcNow,
                        audioSession.SessionId));

                await ProcessMessagesAsync(ssl, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PeerChanged?.Invoke(
                    this,
                    new ControlPeerEvent(
                        remoteDevice,
                        DeviceConnectionState.Offline,
                        "控制连接已停止。",
                        DateTimeOffset.UtcNow));
            }
            catch (Exception exception) when (
                exception is IOException or AuthenticationException or SocketException or InvalidDataException)
            {
                PeerChanged?.Invoke(
                    this,
                    new ControlPeerEvent(
                        remoteDevice,
                        DeviceConnectionState.Faulted,
                        $"控制连接异常：{exception.Message}",
                        DateTimeOffset.UtcNow));
            }
            finally
            {
                if (audioSession is not null)
                {
                    audioReceiver.RemoveSession(audioSession.SessionId);
                }

                if (remoteDevice is not null)
                {
                    clients.TryRemove(
                        new KeyValuePair<Guid, TcpClient>(remoteDevice.DeviceId, client));
                    PeerChanged?.Invoke(
                        this,
                        new ControlPeerEvent(
                            remoteDevice,
                            DeviceConnectionState.Offline,
                            "设备控制连接已断开。",
                            DateTimeOffset.UtcNow,
                            audioSession?.SessionId));
                }
            }
        }
    }

    private AudioSessionParameters CreateAudioSession(IPAddress remoteAddress) =>
        new(
            Guid.NewGuid(),
            CreateNonZeroStreamId(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(AudioPayloadProtector.SaltSize),
            new IPEndPoint(remoteAddress, 0));

    private static uint CreateNonZeroStreamId()
    {
        uint streamId;
        do
        {
            streamId = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(sizeof(uint)));
        }
        while (streamId == 0);

        return streamId;
    }

    private async ValueTask SendStartStreamAsync(
        Stream stream,
        AudioSessionParameters session,
        CancellationToken cancellationToken)
    {
        Envelope envelope = ProtocolConstants.CreateEnvelope(0);
        envelope.StartStream = new StartStream
        {
            StreamId = session.StreamId,
            Codec = (uint)AudioCodec.PcmFloat32,
            SampleRate = session.SampleRate,
            ChannelCount = session.ChannelCount,
            FrameDurationMs = 10,
            SessionId = ByteString.CopyFrom(session.SessionId.ToByteArray()),
            SessionKey = ByteString.CopyFrom(session.Key),
            SessionSalt = ByteString.CopyFrom(session.Salt),
            UdpPort = checked((uint)audioReceiver.Port),
            FrameSamples = session.FrameSamples
        };
        await ControlFrameCodec.WriteAsync(stream, envelope, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<bool> CompletePairingAsync(
        Stream stream,
        DeviceDescriptor remoteDevice,
        byte[] tlsFingerprint,
        CancellationToken cancellationToken)
    {
        Envelope? request = await ControlFrameCodec.ReadAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        if (request?.PairRequest is not { } pairRequest)
        {
            await SendDisconnectAsync(
                stream,
                ErrorCode.NotPaired,
                "设备尚未配对。",
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        PairingCodeValidation result = pairingCodes.ValidateAndConsume(pairRequest.OneTimeCode);
        ErrorCode error = result switch
        {
            PairingCodeValidation.Accepted => ErrorCode.Unspecified,
            PairingCodeValidation.RateLimited => ErrorCode.PairingRateLimited,
            _ => ErrorCode.PairingCodeInvalid
        };
        bool accepted = result == PairingCodeValidation.Accepted;
        if (accepted)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await trustStore.UpsertAsync(
                new TrustedDevice(
                    remoteDevice.DeviceId,
                    remoteDevice.DisplayName,
                    remoteDevice.Platform.ToString(),
                    (ulong)remoteDevice.Capabilities,
                    Convert.ToHexString(tlsFingerprint),
                    now,
                    now),
                cancellationToken).ConfigureAwait(false);
        }

        Envelope response = ProtocolConstants.CreateEnvelope(request.RequestId);
        response.PairResponse = new PairResponse
        {
            Accepted = accepted,
            ControllerNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            Error = error,
            Controller = identity.ToProtocolIdentity()
        };
        await ControlFrameCodec.WriteAsync(stream, response, cancellationToken)
            .ConfigureAwait(false);
        return accepted;
    }

    private static async Task ProcessMessagesAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Envelope? message = await ControlFrameCodec.ReadAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            if (!ProtocolConstants.IsCompatible(message.Version))
            {
                await SendDisconnectAsync(
                    stream,
                    ErrorCode.IncompatibleVersion,
                    "协议主版本不兼容。",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (message.Heartbeat is { } heartbeat)
            {
                Envelope acknowledgement = ProtocolConstants.CreateEnvelope(message.RequestId);
                acknowledgement.HeartbeatAck = new HeartbeatAck
                {
                    EchoMonotonicMilliseconds = heartbeat.MonotonicMilliseconds,
                    ResponderMonotonicMilliseconds = checked((ulong)Environment.TickCount64)
                };
                await ControlFrameCodec.WriteAsync(stream, acknowledgement, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (message.Disconnect is not null)
            {
                return;
            }
        }
    }

    private async ValueTask SendHelloAsync(
        Stream stream,
        ulong requestId,
        bool paired,
        ErrorCode error,
        CancellationToken cancellationToken)
    {
        Envelope response = ProtocolConstants.CreateEnvelope(requestId);
        response.HelloResponse = new HelloResponse
        {
            Controller = identity.ToProtocolIdentity(),
            Paired = paired,
            Error = error
        };
        await ControlFrameCodec.WriteAsync(stream, response, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask SendDisconnectAsync(
        Stream stream,
        ErrorCode error,
        string reason,
        CancellationToken cancellationToken)
    {
        Envelope response = ProtocolConstants.CreateEnvelope(0);
        response.Disconnect = new Disconnect { Error = error, Reason = reason };
        await ControlFrameCodec.WriteAsync(stream, response, cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] GetRemoteFingerprint(SslStream stream)
    {
        X509Certificate certificate = stream.RemoteCertificate
            ?? throw new AuthenticationException("The remote endpoint did not provide a certificate.");
        return SHA256.HashData(certificate.GetRawCertData());
    }

    private static bool FingerprintMatches(string expectedHex, ReadOnlySpan<byte> actual)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHex),
                actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        listener?.Stop();
        foreach (TcpClient client in clients.Values)
        {
            client.Dispose();
        }

        if (acceptLoop is not null)
        {
            await acceptLoop.ConfigureAwait(false);
        }

        Task[] handlers = clientHandlers.Values.ToArray();
        if (handlers.Length > 0)
        {
            await Task.WhenAll(handlers).ConfigureAwait(false);
        }

        await audioReceiver.DisposeAsync().ConfigureAwait(false);

        lifetime.Dispose();
    }
}

public enum ControlClientOutcome
{
    Connected,
    PairingRequired,
    PairingRejected
}

public sealed record ControlClientResult(
    ControlClientOutcome Outcome,
    DeviceDescriptor Controller,
    string Message,
    AudioSessionParameters? AudioSession = null);

public sealed record ControlClientStatistics(
    double LastRoundTripMilliseconds,
    long SuccessfulHeartbeats,
    long FailedHeartbeats);

public sealed class ListenSphereControlClient : IAsyncDisposable
{
    private readonly LocalDeviceIdentity identity;
    private readonly ITrustedDeviceStore trustStore;
    private readonly CancellationTokenSource lifetime = new();
    private TcpClient? tcpClient;
    private SslStream? stream;
    private Task? heartbeatLoop;
    private CancellationTokenSource? connectionLifetime;
    private DeviceDescriptor? controller;
    private long requestId;
    private double lastRoundTripMilliseconds;
    private long successfulHeartbeats;
    private long failedHeartbeats;
    private int disposed;

    public ListenSphereControlClient(
        LocalDeviceIdentity identity,
        ITrustedDeviceStore trustStore)
    {
        this.identity = identity;
        this.trustStore = trustStore;
    }

    public event EventHandler<ControlPeerEvent>? StateChanged;

    public ControlClientStatistics Statistics => new(
        Volatile.Read(ref lastRoundTripMilliseconds),
        Interlocked.Read(ref successfulHeartbeats),
        Interlocked.Read(ref failedHeartbeats));

    public async ValueTask<ControlClientResult> ConnectAsync(
        DiscoveredController discovered,
        string? pairingCode,
        CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        TrustedDevice? trusted = await trustStore.FindAsync(
            discovered.Device.DeviceId,
            cancellationToken).ConfigureAwait(false);
        byte[]? remoteFingerprint = null;

        tcpClient = new TcpClient(discovered.ControlEndpoint.AddressFamily);
        await tcpClient.ConnectAsync(
            discovered.ControlEndpoint,
            cancellationToken).ConfigureAwait(false);
        stream = new SslStream(
            tcpClient.GetStream(),
            false,
            (_, certificate, _, _) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                remoteFingerprint = SHA256.HashData(certificate.GetRawCertData());
                return trusted is null ||
                    FingerprintMatches(trusted.CertificateFingerprint, remoteFingerprint);
            });
        var certificates = new X509CertificateCollection { identity.Certificate };
        await stream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = $"ListenSphere-{discovered.Device.DeviceId:N}",
                ClientCertificates = certificates,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = null
            },
            cancellationToken).ConfigureAwait(false);

        Envelope hello = NextEnvelope();
        hello.HelloRequest = new HelloRequest { Device = identity.ToProtocolIdentity() };
        await ControlFrameCodec.WriteAsync(stream, hello, cancellationToken)
            .ConfigureAwait(false);
        Envelope response = await ControlFrameCodec.ReadAsync(stream, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new EndOfStreamException("Controller closed the connection during hello.");
        if (!ProtocolConstants.IsCompatible(response.Version) ||
            response.HelloResponse?.Controller is not { } controllerIdentity)
        {
            throw new InvalidDataException("Controller returned an invalid hello response.");
        }

        controller = ProtocolIdentity.ToDescriptor(controllerIdentity);
        if (controller.DeviceId != discovered.Device.DeviceId)
        {
            throw new AuthenticationException("Discovered and connected controller UUIDs differ.");
        }

        if (remoteFingerprint is null ||
            !CryptographicOperations.FixedTimeEquals(
                remoteFingerprint,
                controllerIdentity.CertificateFingerprint.Span))
        {
            throw new AuthenticationException("Controller TLS identity fingerprint mismatch.");
        }

        if (!response.HelloResponse.Paired)
        {
            if (string.IsNullOrWhiteSpace(pairingCode))
            {
                await DisconnectAsync().ConfigureAwait(false);
                return new ControlClientResult(
                    ControlClientOutcome.PairingRequired,
                    controller,
                    "请输入 Controller 显示的六位验证码。");
            }

            Envelope pair = NextEnvelope();
            pair.PairRequest = new PairRequest
            {
                Sender = identity.ToProtocolIdentity(),
                SenderNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
                OneTimeCode = pairingCode
            };
            await ControlFrameCodec.WriteAsync(stream, pair, cancellationToken)
                .ConfigureAwait(false);
            Envelope pairResponse = await ControlFrameCodec.ReadAsync(stream, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new EndOfStreamException("Controller closed the connection during pairing.");
            if (pairResponse.PairResponse is not { Accepted: true })
            {
                await DisconnectAsync().ConfigureAwait(false);
                return new ControlClientResult(
                    ControlClientOutcome.PairingRejected,
                    controller,
                    $"配对被拒绝：{pairResponse.PairResponse?.Error}");
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            await trustStore.UpsertAsync(
                new TrustedDevice(
                    controller.DeviceId,
                    controller.DisplayName,
                    controller.Platform.ToString(),
                    (ulong)controller.Capabilities,
                    Convert.ToHexString(remoteFingerprint),
                    now,
                    now),
                cancellationToken).ConfigureAwait(false);
        }
        else if (trusted is null)
        {
            throw new AuthenticationException("Controller reports paired but no local trust record exists.");
        }

        using var streamOfferTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        streamOfferTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        Envelope streamOffer = await ControlFrameCodec.ReadAsync(
            stream,
            streamOfferTimeout.Token).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Controller closed before offering an audio stream.");
        AudioSessionParameters audioSession = ParseAudioSession(
            streamOffer.StartStream,
            discovered.ControlEndpoint.Address);

        StateChanged?.Invoke(
            this,
            new ControlPeerEvent(
                controller,
                DeviceConnectionState.Connected,
                "已连接，心跳运行中。",
                DateTimeOffset.UtcNow));
        heartbeatLoop = HeartbeatLoopAsync(connectionLifetime.Token);
        return new ControlClientResult(
            ControlClientOutcome.Connected,
            controller,
            "控制通道和 UDP 音频会话已就绪。",
            audioSession);
    }

    private static AudioSessionParameters ParseAudioSession(
        StartStream? offer,
        IPAddress controllerAddress)
    {
        if (offer is null ||
            offer.SessionId.Length != 16 ||
            offer.SessionKey.Length != 32 ||
            offer.SessionSalt.Length != AudioPayloadProtector.SaltSize ||
            offer.StreamId == 0 ||
            offer.Codec != (uint)AudioCodec.PcmFloat32 ||
            offer.SampleRate != 48_000 ||
            offer.ChannelCount != 2 ||
            offer.FrameDurationMs != 10 ||
            offer.FrameSamples != 480 ||
            offer.UdpPort is 0 or > 65_535)
        {
            throw new InvalidDataException("Controller offered an invalid P4 audio session.");
        }

        var session = new AudioSessionParameters(
            new Guid(offer.SessionId.Span),
            offer.StreamId,
            offer.SessionKey.ToByteArray(),
            offer.SessionSalt.ToByteArray(),
            new IPEndPoint(controllerAddress, checked((int)offer.UdpPort)),
            offer.SampleRate,
            checked((byte)offer.ChannelCount),
            checked((ushort)offer.FrameSamples));
        session.Validate();
        return session;
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Envelope heartbeat = NextEnvelope();
                heartbeat.Heartbeat = new Heartbeat
                {
                    MonotonicMilliseconds = checked((ulong)Environment.TickCount64)
                };
                long sentAt = Stopwatch.GetTimestamp();
                await ControlFrameCodec.WriteAsync(stream!, heartbeat, cancellationToken)
                    .ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                Envelope? response = await ControlFrameCodec.ReadAsync(stream!, timeout.Token)
                    .ConfigureAwait(false);
                if (response?.HeartbeatAck?.EchoMonotonicMilliseconds !=
                    heartbeat.Heartbeat.MonotonicMilliseconds)
                {
                    throw new InvalidDataException("Controller returned an invalid heartbeat response.");
                }

                Volatile.Write(
                    ref lastRoundTripMilliseconds,
                    Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds);
                Interlocked.Increment(ref successfulHeartbeats);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StateChanged?.Invoke(
                this,
                new ControlPeerEvent(
                    controller,
                    DeviceConnectionState.Offline,
                    "心跳已停止。",
                    DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or InvalidDataException or OperationCanceledException)
        {
            Interlocked.Increment(ref failedHeartbeats);
            StateChanged?.Invoke(
                this,
                new ControlPeerEvent(
                    controller,
                    DeviceConnectionState.Faulted,
                    $"心跳中断：{exception.Message}",
                    DateTimeOffset.UtcNow));
        }
    }

    private Envelope NextEnvelope() =>
        ProtocolConstants.CreateEnvelope(checked((ulong)Interlocked.Increment(ref requestId)));

    private static bool FingerprintMatches(string expectedHex, ReadOnlySpan<byte> actual)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHex),
                actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async ValueTask DisconnectAsync()
    {
        if (connectionLifetime is not null)
        {
            await connectionLifetime.CancelAsync().ConfigureAwait(false);
        }

        if (stream is not null)
        {
            try
            {
                Envelope disconnect = NextEnvelope();
                disconnect.Disconnect = new Disconnect
                {
                    Error = ErrorCode.Unspecified,
                    Reason = "Sender 主动断开。"
                };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await ControlFrameCodec.WriteAsync(stream, disconnect, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or OperationCanceledException or
                    ObjectDisposedException or InvalidOperationException)
            {
                StateChanged?.Invoke(
                    this,
                    new ControlPeerEvent(
                        controller,
                        DeviceConnectionState.Offline,
                        $"连接关闭：{exception.Message}",
                        DateTimeOffset.UtcNow));
            }
        }

        stream?.Dispose();
        tcpClient?.Dispose();
        Task? previousHeartbeat = heartbeatLoop;
        stream = null;
        tcpClient = null;
        heartbeatLoop = null;
        controller = null;
        connectionLifetime?.Dispose();
        connectionLifetime = null;
        if (previousHeartbeat is not null)
        {
            await previousHeartbeat.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        await DisconnectAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
}
