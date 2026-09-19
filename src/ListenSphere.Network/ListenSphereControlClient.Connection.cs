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

public sealed partial class ListenSphereControlClient
{
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
        await stream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = $"ListenSphere-{discovered.Device.DeviceId:N}",
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = null
            },
            cancellationToken).ConfigureAwait(false);

        Envelope hello = NextEnvelope();
        hello.HelloRequest = DeviceProof.Create(
            identity,
            remoteFingerprint ?? throw new AuthenticationException(
                "Controller did not provide a TLS identity."));
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

}
