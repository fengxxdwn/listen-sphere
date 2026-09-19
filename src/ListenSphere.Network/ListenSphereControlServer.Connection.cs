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

public sealed partial class ListenSphereControlServer
{
    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        DeviceDescriptor? remoteDevice = null;
        AudioSessionParameters? audioSession = null;
        var additionalSessions = new Dictionary<uint, AdditionalServerAudioStream>();
        ControlServerClient? registeredClient = null;
        string connectionTransport = await ControlTransportPreface.ReadConnectionTransportAsync(client, cancellationToken)
            .ConfigureAwait(false);
        using (client)
        await using (var ssl = new SslStream(client.GetStream(), false))
        {
            try
            {
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = identity.Certificate,
                        // TLS 1.3 client-certificate post-handshake authentication is not
                        // interoperable with every Android Conscrypt build. Device ownership
                        // is proven inside the TLS channel with a signed HelloRequest instead.
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    },
                    cancellationToken).ConfigureAwait(false);

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

                byte[] deviceFingerprint = DeviceProof.Validate(
                    helloEnvelope.HelloRequest,
                    identity.CertificateFingerprint);

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
                    deviceFingerprint,
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
                    FingerprintMatches(trusted.CertificateFingerprint, deviceFingerprint);
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
                        deviceFingerprint,
                        cancellationToken).ConfigureAwait(false);
                    if (!paired)
                    {
                        return;
                    }
                }

                DateTimeOffset connectedAt = DateTimeOffset.UtcNow;
                await trustStore.UpsertAsync(
                    new TrustedDevice(
                        remoteDevice.DeviceId,
                        remoteDevice.DisplayName,
                        remoteDevice.Platform.ToString(),
                        (ulong)remoteDevice.Capabilities,
                        Convert.ToHexString(deviceFingerprint),
                        trusted?.PairedAt ?? connectedAt,
                        connectedAt,
                        connectionTransport),
                    cancellationToken).ConfigureAwait(false);

                registeredClient = new ControlServerClient(client, ssl);
                clients.AddOrUpdate(
                    remoteDevice.DeviceId,
                    registeredClient,
                    (_, previous) =>
                    {
                        previous.Client.Dispose();
                        return registeredClient;
                    });
                PeerChanged?.Invoke(
                    this,
                    new ControlPeerEvent(
                        remoteDevice,
                        DeviceConnectionState.Connected,
                        "控制通道已通过 TLS 1.3 认证。",
                        DateTimeOffset.UtcNow,
                        Transport: connectionTransport));

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
                        audioSession.SessionId,
                        connectionTransport,
                        SourceName: helloEnvelope.HelloRequest.SourceName,
                        SourceKind: helloEnvelope.HelloRequest.SourceKind));

                await ProcessMessagesAsync(
                    ssl,
                    remoteDevice,
                    remoteAddress,
                    connectionTransport,
                    additionalSessions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PeerChanged?.Invoke(
                    this,
                    new ControlPeerEvent(
                        remoteDevice,
                        DeviceConnectionState.Offline,
                        "控制连接已停止。",
                        DateTimeOffset.UtcNow,
                        Transport: connectionTransport));
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
                        DateTimeOffset.UtcNow,
                        Transport: connectionTransport));
            }
            finally
            {
                if (audioSession is not null)
                {
                    audioReceiver.RemoveSession(audioSession.SessionId);
                }

                foreach (AdditionalServerAudioStream additional in additionalSessions.Values)
                {
                    audioReceiver.RemoveSession(additional.Session.SessionId);
                    PeerChanged?.Invoke(
                        this,
                        new ControlPeerEvent(
                            remoteDevice,
                            DeviceConnectionState.Offline,
                            $"应用声道已关闭：{additional.DisplayName}",
                            DateTimeOffset.UtcNow,
                            additional.Session.SessionId,
                            connectionTransport,
                            additional.ChannelId,
                            additional.DisplayName,
                            additional.SourceKind));
                }

                if (remoteDevice is not null)
                {
                    if (registeredClient is not null)
                    {
                        clients.TryRemove(
                            new KeyValuePair<Guid, ControlServerClient>(
                                remoteDevice.DeviceId,
                                registeredClient));
                    }
                    PeerChanged?.Invoke(
                        this,
                        new ControlPeerEvent(
                            remoteDevice,
                            DeviceConnectionState.Offline,
                            "设备控制连接已断开。",
                            DateTimeOffset.UtcNow,
                            audioSession?.SessionId,
                            connectionTransport));
                }
            }
        }
    }

}
