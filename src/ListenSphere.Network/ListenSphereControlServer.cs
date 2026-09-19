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

public sealed partial class ListenSphereControlServer : IAsyncDisposable
{
    private readonly LocalDeviceIdentity identity;
    private readonly ITrustedDeviceStore trustStore;
    private readonly PairingCodeService pairingCodes;
    private readonly UdpAudioReceiver audioReceiver = new();
    private readonly ConcurrentDictionary<Guid, ControlServerClient> clients = new();
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
        if (clients.TryRemove(deviceId, out ControlServerClient? client))
        {
            await DisconnectClientAsync(
                client,
                ErrorCode.NotPaired,
                "主控端已撤销此设备的信任。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisconnectDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        if (clients.TryRemove(deviceId, out ControlServerClient? client))
        {
            await DisconnectClientAsync(
                client,
                ErrorCode.Unspecified,
                "主控端已断开本次连接，设备信任仍保留。",
                cancellationToken).ConfigureAwait(false);
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
        envelope.StartStream = CreateStartStream(session);
        await ControlFrameCodec.WriteAsync(stream, envelope, cancellationToken)
            .ConfigureAwait(false);
    }

    private StartStream CreateStartStream(AudioSessionParameters session) => new()
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

        PairingCodeValidation result = pairingCodes.ValidateAndConsume(
            pairRequest.OneTimeCode,
            $"Wireless:{remoteDevice.DeviceId:D}");
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

    private async Task ProcessMessagesAsync(
        Stream stream,
        DeviceDescriptor remoteDevice,
        IPAddress remoteAddress,
        string transport,
        Dictionary<uint, AdditionalServerAudioStream> additionalSessions,
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
            else if (message.OpenAudioStream is { } open)
            {
                string sourceId = open.SourceId.Trim();
                string displayName = open.DisplayName.Trim();
                string sourceKind = open.SourceKind.Trim().ToLowerInvariant();
                Envelope response = ProtocolConstants.CreateEnvelope(message.RequestId);
                if (sourceId.Length is 0 or > 512 || displayName.Length is 0 or > 128 ||
                    sourceKind is not ("application" or "system"))
                {
                    response.AudioStreamOpened = new AudioStreamOpened
                    {
                        SourceId = sourceId,
                        DisplayName = displayName,
                        SourceKind = sourceKind,
                        Error = ErrorCode.StreamRejected
                    };
                }
                else
                {
                    AudioSessionParameters session = CreateAudioSession(remoteAddress);
                    Guid channelId = AudioStreamIdentity.CreateChannelId(
                        remoteDevice.DeviceId,
                        sourceId);
                    var additional = new AdditionalServerAudioStream(
                        channelId,
                        sourceId,
                        displayName,
                        sourceKind,
                        session);
                    additionalSessions.Add(session.StreamId, additional);
                    audioReceiver.RegisterSession(session);
                    response.AudioStreamOpened = new AudioStreamOpened
                    {
                        SourceId = sourceId,
                        DisplayName = displayName,
                        SourceKind = sourceKind,
                        ChannelId = ByteString.CopyFrom(channelId.ToByteArray()),
                        Stream = CreateStartStream(session),
                        Error = ErrorCode.Unspecified
                    };
                    PeerChanged?.Invoke(
                        this,
                        new ControlPeerEvent(
                            remoteDevice,
                            DeviceConnectionState.Streaming,
                            $"应用声道已就绪：{displayName}",
                            DateTimeOffset.UtcNow,
                            session.SessionId,
                            transport,
                            channelId,
                            displayName,
                            sourceKind));
                }
                await ControlFrameCodec.WriteAsync(stream, response, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (message.CloseAudioStream is { } close)
            {
                if (additionalSessions.Remove(close.StreamId, out AdditionalServerAudioStream? closed))
                {
                    audioReceiver.RemoveSession(closed.Session.SessionId);
                    PeerChanged?.Invoke(
                        this,
                        new ControlPeerEvent(
                            remoteDevice,
                            DeviceConnectionState.Offline,
                            $"应用声道已关闭：{closed.DisplayName}",
                            DateTimeOffset.UtcNow,
                            closed.Session.SessionId,
                            transport,
                            closed.ChannelId,
                            closed.DisplayName,
                            closed.SourceKind));
                }
                Envelope acknowledgement = ProtocolConstants.CreateEnvelope(message.RequestId);
                acknowledgement.StopStream = new StopStream
                {
                    StreamId = close.StreamId,
                    Reason = close.Reason
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

    private static async ValueTask DisconnectClientAsync(
        ControlServerClient client,
        ErrorCode error,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendDisconnectAsync(client.Stream, error, reason, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or ObjectDisposedException or
                OperationCanceledException)
        {
            client.Client.Dispose();
            return;
        }

        // Do not close immediately after the TLS write. The mobile heartbeat reader
        // may be between requests; keeping the full-duplex connection alive lets it
        // consume the framed Disconnect instead of observing an ambiguous EOF. A
        // bounded fallback still releases peers that never read the notification.
        _ = DisposeAfterDisconnectGraceAsync(client);
    }

    private static async Task DisposeAfterDisconnectGraceAsync(ControlServerClient client)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(7)).ConfigureAwait(false);
            client.Client.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
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
        foreach (ControlServerClient client in clients.Values)
        {
            client.Client.Dispose();
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
