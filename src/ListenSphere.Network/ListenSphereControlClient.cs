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

public sealed partial class ListenSphereControlClient : IAsyncDisposable
{
    private readonly LocalDeviceIdentity identity;
    private readonly ITrustedDeviceStore trustStore;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim controlExchangeGate = new(1, 1);
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
                await controlExchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                Envelope? response;
                try
                {
                    await ControlFrameCodec.WriteAsync(stream!, heartbeat, cancellationToken)
                        .ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(6));
                    response = await ControlFrameCodec.ReadAsync(stream!, timeout.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    controlExchangeGate.Release();
                }
                if (response?.Disconnect is { } disconnect)
                {
                    throw new ControllerRequestedDisconnectException(
                        disconnect.Reason.Length == 0
                            ? "Controller requested disconnect."
                            : disconnect.Reason);
                }
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
        catch (ControllerRequestedDisconnectException exception)
        {
            StateChanged?.Invoke(
                this,
                new ControlPeerEvent(
                    controller,
                    DeviceConnectionState.Offline,
                    exception.Message,
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

    public async ValueTask<OpenedAudioStream> OpenAudioStreamAsync(
        string sourceId,
        string displayName,
        string sourceKind = "application",
        CancellationToken cancellationToken = default)
    {
        if (stream is null || controller is null || connectionLifetime is null)
        {
            throw new InvalidOperationException("Controller control channel is not connected.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Envelope request = NextEnvelope();
        request.OpenAudioStream = new OpenAudioStream
        {
            SourceId = sourceId,
            DisplayName = displayName,
            SourceKind = sourceKind
        };

        await controlExchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ControlFrameCodec.WriteAsync(stream, request, cancellationToken)
                .ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            Envelope response = await ControlFrameCodec.ReadAsync(stream, timeout.Token)
                .ConfigureAwait(false)
                ?? throw new EndOfStreamException("Controller closed while opening an audio stream.");
            AudioStreamOpened opened = response.AudioStreamOpened
                ?? throw new InvalidDataException("Controller returned an invalid stream response.");
            if (opened.Error != ErrorCode.Unspecified || opened.ChannelId.Length != 16)
            {
                throw new InvalidOperationException(
                    $"Controller rejected source '{displayName}': {opened.Error}.");
            }

            AudioSessionParameters session = ParseAudioSession(
                opened.Stream,
                ((IPEndPoint)tcpClient!.Client.RemoteEndPoint!).Address);
            return new OpenedAudioStream(
                new Guid(opened.ChannelId.Span),
                opened.SourceId,
                opened.DisplayName,
                opened.SourceKind,
                session);
        }
        finally
        {
            controlExchangeGate.Release();
        }
    }

    public async ValueTask CloseAudioStreamAsync(
        uint streamId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (stream is null || streamId == 0)
        {
            return;
        }

        Envelope request = NextEnvelope();
        request.CloseAudioStream = new CloseAudioStream
        {
            StreamId = streamId,
            Reason = reason
        };
        await controlExchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ControlFrameCodec.WriteAsync(stream, request, cancellationToken)
                .ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            Envelope response = await ControlFrameCodec.ReadAsync(stream, timeout.Token)
                .ConfigureAwait(false)
                ?? throw new EndOfStreamException("Controller closed while closing an audio stream.");
            if (response.StopStream?.StreamId != streamId)
            {
                throw new InvalidDataException("Controller returned an invalid close response.");
            }
        }
        finally
        {
            controlExchangeGate.Release();
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
        controlExchangeGate.Dispose();
        lifetime.Dispose();
    }
}
