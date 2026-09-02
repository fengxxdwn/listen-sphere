using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ListenSphere.Device;
using ListenSphere.Network;
using System.Net;
using System.Net.Sockets;
using ListenSphere.Protocol;
using ListenSphere.Protocol.V1;
using Xunit;

namespace ListenSphere.Core.Tests;

public sealed class NetworkControlTests
{
    [Fact]
    public void ManualConnectEndpoints_KeepPrivateIpv4AndIncludePort()
    {
        string endpoints = ListenSphereDiscovery.FormatManualConnectEndpoints(
            [
                IPAddress.Parse("192.168.137.1"),
                IPAddress.Parse("10.0.0.5"),
                IPAddress.Parse("8.8.8.8"),
                IPAddress.IPv6Loopback,
                IPAddress.Parse("192.168.137.1")
            ],
            58566);

        Assert.Equal("10.0.0.5:58566 · 192.168.137.1:58566", endpoints);
    }

    [Fact]
    public void ReconnectBackoff_IsBoundedAndIndependentPerTarget()
    {
        var tracker = new ReconnectBackoffTracker(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)]);
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.True(tracker.Check(first, now).CanAttempt);
        ReconnectBackoffDecision failed = tracker.RecordFailure(first, now);
        Assert.Equal(TimeSpan.FromSeconds(1), failed.RetryAfter);
        Assert.False(tracker.Check(first, now).CanAttempt);
        Assert.True(tracker.Check(second, now).CanAttempt);
        Assert.True(tracker.Check(first, now.AddSeconds(1)).CanAttempt);

        tracker.RecordFailure(first, now.AddSeconds(1));
        ReconnectBackoffDecision bounded = tracker.RecordFailure(first, now.AddSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(3), bounded.RetryAfter);
        tracker.Reset(first);
        Assert.True(tracker.Check(first, now).CanAttempt);
    }

    [Fact]
    public void LocalMachineAddressRecognizesLoopbackAndActiveInterfaces()
    {
        Assert.True(ListenSphereDiscovery.IsLocalMachineAddress(IPAddress.Loopback));

        IPAddress[] activeAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(candidate => candidate.GetIPProperties().UnicastAddresses)
            .Select(candidate => candidate.Address)
            .Where(candidate => !IPAddress.IsLoopback(candidate))
            .ToArray();
        Assert.All(activeAddresses, candidate =>
            Assert.True(ListenSphereDiscovery.IsLocalMachineAddress(candidate)));
    }

    [Theory]
    [InlineData(1, "Wireless")]
    [InlineData(2, "Bluetooth")]
    [InlineData(3, "Wired")]
    public void TransportPreface_ParsesStableCodes(byte code, string expected)
    {
        byte[] value = [(byte)'L', (byte)'S', (byte)'T', (byte)'H', code];

        Assert.True(ControlTransportPreface.TryParse(value, out string transport));
        Assert.Equal(expected, transport);
        Assert.False(ControlTransportPreface.TryParse("TLS"u8, out _));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.31.164", true)]
    [InlineData("26.231.136.157", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("8.8.8.8", false)]
    public void Discovery_ClassifiesPrivateLanAddresses(string text, bool expected)
    {
        Assert.Equal(
            expected,
            ListenSphereDiscovery.IsLocalNetworkAddress(IPAddress.Parse(text)));
    }

    [Fact]
    public void ApplicationChannelIdentity_IsStableAndScopedToDevice()
    {
        Guid firstDevice = Guid.NewGuid();
        Guid secondDevice = Guid.NewGuid();

        Guid first = AudioStreamIdentity.CreateChannelId(firstDevice, @"C:\Apps\Game.exe");
        Guid same = AudioStreamIdentity.CreateChannelId(firstDevice, @"c:\apps\game.exe");
        Guid otherDevice = AudioStreamIdentity.CreateChannelId(secondDevice, @"C:\Apps\Game.exe");
        Guid otherApplication = AudioStreamIdentity.CreateChannelId(firstDevice, @"C:\Apps\Chat.exe");

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherDevice);
        Assert.NotEqual(first, otherApplication);
    }

    [Fact]
    public void PairingCode_IsSixDigitsAndSingleUse()
    {
        var service = new PairingCodeService();

        PairingCode code = service.Generate();

        Assert.Matches("^[0-9]{6}$", code.Value);
        Assert.InRange(
            code.ExpiresAt - DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(4.9),
            TimeSpan.FromMinutes(5.1));
        Assert.Equal(PairingCodeValidation.Accepted, service.ValidateAndConsume(code.Value));
        Assert.Equal(PairingCodeValidation.Expired, service.ValidateAndConsume(code.Value));
    }

    [Fact]
    public void PairingCode_RateLimitIsScopedAndRegenerationDoesNotBypassBlock()
    {
        var service = new PairingCodeService();
        PairingCode first = service.Generate();
        string incorrect = first.Value == "000000" ? "999999" : "000000";

        for (var attempt = 1; attempt < 5; attempt++)
        {
            Assert.Equal(
                PairingCodeValidation.Invalid,
                service.ValidateAndConsume(incorrect, "Bluetooth:device-a"));
        }

        Assert.Equal(
            PairingCodeValidation.RateLimited,
            service.ValidateAndConsume(incorrect, "Bluetooth:device-a"));

        PairingCode replacement = service.Generate();

        Assert.Equal(
            PairingCodeValidation.RateLimited,
            service.ValidateAndConsume(replacement.Value, "Bluetooth:device-a"));
        Assert.Equal(
            PairingCodeValidation.Accepted,
            service.ValidateAndConsume(replacement.Value, "Wireless:device-b"));
    }

    [Fact]
    public void PairingCode_CancelImmediatelyInvalidatesActiveCode()
    {
        var service = new PairingCodeService();
        PairingCode code = service.Generate();

        Assert.True(service.HasActiveCode);
        service.Cancel();

        Assert.False(service.HasActiveCode);
        Assert.Equal(
            PairingCodeValidation.Expired,
            service.ValidateAndConsume(code.Value, "Wireless:device-a"));
    }

    [Fact]
    public async Task TrustStore_PersistsAndRevokesDevice()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "trusted-devices.json");
        var store = new JsonTrustedDeviceStore(path);
        var device = new TrustedDevice(
            Guid.NewGuid(),
            "Sender",
            "Windows",
            2,
            "001122",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        try
        {
            await store.UpsertAsync(device, TestContext.Current.CancellationToken);

            var reloaded = new JsonTrustedDeviceStore(path);
            TrustedDevice stored = Assert.IsType<TrustedDevice>(
                await reloaded.FindAsync(
                    device.DeviceId,
                    TestContext.Current.CancellationToken));
            Assert.Equal(device.DeviceId, stored.DeviceId);
            Assert.Equal(device.DisplayName, stored.DisplayName);
            Assert.Equal(device.CertificateFingerprint, stored.CertificateFingerprint);
            Assert.Equal(device.PairedAt, stored.PairedAt);
            Assert.Equal(device.LastSeen, stored.LastSeen);
            Assert.True(stored.SupportsTransport("Wireless"));

            DateTimeOffset bluetoothSeen = DateTimeOffset.UtcNow.AddMinutes(1);
            TrustedDevice bluetoothDevice = device with
            {
                LastSeen = bluetoothSeen,
                Transport = "Bluetooth"
            };
            await reloaded.UpsertAsync(
                bluetoothDevice,
                TestContext.Current.CancellationToken);
            IReadOnlyList<TrustedDevice> updated = await reloaded.GetAllAsync(
                TestContext.Current.CancellationToken);
            Assert.Single(updated);
            Assert.Equal("Bluetooth", updated[0].Transport);
            Assert.Equal(bluetoothSeen, updated[0].LastSeen);
            Assert.True(updated[0].SupportsTransport("Wireless"));
            Assert.True(updated[0].SupportsTransport("Bluetooth"));
            Assert.Equal(2, updated[0].ObservedTransports.Count);

            await reloaded.RemoveAsync(
                device.DeviceId,
                TestContext.Current.CancellationToken);
            Assert.Null(
                await reloaded.FindAsync(
                    device.DeviceId,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TlsControlChannel_AllowsHelloWithoutClientCertificate()
    {
        string directory = CreateTemporaryDirectory();
        string controllerDirectory = Path.Combine(directory, "controller");
        string senderDirectory = Path.Combine(directory, "sender");
        using LocalDeviceIdentity controllerIdentity = LocalIdentityStore.LoadOrCreate(
            controllerDirectory,
            "Test Controller",
            DeviceCapabilities.Controller | DeviceCapabilities.AudioReceive);
        using LocalDeviceIdentity senderIdentity = LocalIdentityStore.LoadOrCreate(
            senderDirectory,
            "Test Sender",
            DeviceCapabilities.AudioSend);
        var controllerTrust = new JsonTrustedDeviceStore(
            Path.Combine(controllerDirectory, "trusted-devices.json"));
        await using var server = new ListenSphereControlServer(
            controllerIdentity,
            controllerTrust,
            new PairingCodeService());

        try
        {
            await server.StartAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            await tcp.ConnectAsync(
                IPAddress.Loopback,
                server.Port,
                TestContext.Current.CancellationToken);
            byte[]? controllerFingerprint = null;
            await using var tls = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, _, _) =>
                {
                    if (certificate is null)
                    {
                        return false;
                    }

                    controllerFingerprint = SHA256.HashData(certificate.GetRawCertData());
                    return true;
                });
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = $"ListenSphere-{controllerIdentity.Device.DeviceId:N}",
                    EnabledSslProtocols = SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                TestContext.Current.CancellationToken);

            Assert.Null(tls.LocalCertificate);
            Envelope hello = ProtocolConstants.CreateEnvelope(1);
            hello.HelloRequest = DeviceProof.Create(
                senderIdentity,
                Assert.IsType<byte[]>(controllerFingerprint));
            await ControlFrameCodec.WriteAsync(
                tls,
                hello,
                TestContext.Current.CancellationToken);
            Envelope response = Assert.IsType<Envelope>(
                await ControlFrameCodec.ReadAsync(
                    tls,
                    TestContext.Current.CancellationToken));

            Assert.NotNull(response.HelloResponse);
            Assert.False(response.HelloResponse.Paired);
            Assert.Equal(ErrorCode.NotPaired, response.HelloResponse.Error);
            Assert.Equal(hello.RequestId, response.RequestId);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TlsControlChannel_PairsPinsAndReconnects()
    {
        string directory = CreateTemporaryDirectory();
        string controllerDirectory = Path.Combine(directory, "controller");
        string senderDirectory = Path.Combine(directory, "sender");
        using LocalDeviceIdentity controllerIdentity = LocalIdentityStore.LoadOrCreate(
            controllerDirectory,
            "Test Controller",
            DeviceCapabilities.Controller | DeviceCapabilities.AudioReceive);
        using LocalDeviceIdentity senderIdentity = LocalIdentityStore.LoadOrCreate(
            senderDirectory,
            "Test Sender",
            DeviceCapabilities.AudioSend);
        var controllerTrust = new JsonTrustedDeviceStore(
            Path.Combine(controllerDirectory, "trusted-devices.json"));
        var senderTrust = new JsonTrustedDeviceStore(
            Path.Combine(senderDirectory, "trusted-devices.json"));
        var pairingCodes = new PairingCodeService();
        await using var server = new ListenSphereControlServer(
            controllerIdentity,
            controllerTrust,
            pairingCodes);

        try
        {
            await server.StartAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            var discovered = new DiscoveredController(
                controllerIdentity.Device,
                new IPEndPoint(IPAddress.Loopback, server.Port));
            string code = pairingCodes.Generate().Value;

            await using (var firstClient = new ListenSphereControlClient(
                senderIdentity,
                senderTrust))
            {
                ControlClientResult first = await firstClient.ConnectAsync(
                    discovered,
                    code,
                    TestContext.Current.CancellationToken);
                Assert.Equal(ControlClientOutcome.Connected, first.Outcome);
                Assert.NotNull(first.AudioSession);
                Assert.Equal(server.AudioReceiver.Port, first.AudioSession.RemoteEndpoint.Port);
                Assert.Equal(32, first.AudioSession.Key.Length);

                await using var audioSender = new UdpAudioSender(first.AudioSession);
                byte[] pcm = new byte[3_840];
                for (ulong timestamp = 0; timestamp < 4 * 480; timestamp += 480)
                {
                    await audioSender.SendFrameAsync(
                        pcm,
                        timestamp,
                        TestContext.Current.CancellationToken);
                }

                using var audioTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.Current.CancellationToken);
                audioTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using IAsyncEnumerator<NetworkAudioFrame> receivedAudio = server
                    .AudioReceiver
                    .ReadAllAsync(audioTimeout.Token)
                    .GetAsyncEnumerator(audioTimeout.Token);
                Assert.True(await receivedAudio.MoveNextAsync());
                Assert.Equal(first.AudioSession.SessionId, receivedAudio.Current.SessionId);
                Assert.Equal(pcm, receivedAudio.Current.Pcm);

                OpenedAudioStream game = await firstClient.OpenAudioStreamAsync(
                    @"C:\Games\Example.exe",
                    "Example Game",
                    cancellationToken: TestContext.Current.CancellationToken);
                OpenedAudioStream chat = await firstClient.OpenAudioStreamAsync(
                    @"C:\Apps\Chat.exe",
                    "Example Chat",
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.NotEqual(game.ChannelId, chat.ChannelId);
                Assert.NotEqual(game.Session.SessionId, chat.Session.SessionId);

                await using var gameSender = new UdpAudioSender(game.Session);
                await using var chatSender = new UdpAudioSender(chat.Session);
                for (ulong timestamp = 0; timestamp < 4 * 480; timestamp += 480)
                {
                    await gameSender.SendFrameAsync(
                        pcm, timestamp, TestContext.Current.CancellationToken);
                    await chatSender.SendFrameAsync(
                        pcm, timestamp, TestContext.Current.CancellationToken);
                }

                var receivedSessions = new HashSet<Guid>();
                while (receivedSessions.Count < 2)
                {
                    Assert.True(await receivedAudio.MoveNextAsync());
                    if (receivedAudio.Current.SessionId == game.Session.SessionId ||
                        receivedAudio.Current.SessionId == chat.Session.SessionId)
                    {
                        receivedSessions.Add(receivedAudio.Current.SessionId);
                    }
                }
                Assert.Contains(game.Session.SessionId, receivedSessions);
                Assert.Contains(chat.Session.SessionId, receivedSessions);

                await firstClient.CloseAudioStreamAsync(
                    game.Session.StreamId,
                    "test complete",
                    TestContext.Current.CancellationToken);
                await firstClient.CloseAudioStreamAsync(
                    chat.Session.StreamId,
                    "test complete",
                    TestContext.Current.CancellationToken);
            }

            Assert.NotNull(
                await controllerTrust.FindAsync(
                    senderIdentity.Device.DeviceId,
                    TestContext.Current.CancellationToken));
            Assert.NotNull(
                await senderTrust.FindAsync(
                    controllerIdentity.Device.DeviceId,
                    TestContext.Current.CancellationToken));

            await using var secondClient = new ListenSphereControlClient(
                senderIdentity,
                senderTrust);
            ControlClientResult second = await secondClient.ConnectAsync(
                discovered,
                null,
                TestContext.Current.CancellationToken);
            Assert.Equal(ControlClientOutcome.Connected, second.Outcome);
            Assert.NotNull(second.AudioSession);
            Assert.NotEqual(Guid.Empty, second.AudioSession.SessionId);

            var intentionalDisconnect = new TaskCompletionSource<ControlPeerEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            secondClient.StateChanged += (_, peer) =>
            {
                if (peer.State == DeviceConnectionState.Offline &&
                    peer.Message.Contains("主控端已断开", StringComparison.Ordinal))
                {
                    intentionalDisconnect.TrySetResult(peer);
                }
            };
            await server.DisconnectDeviceAsync(
                senderIdentity.Device.DeviceId,
                TestContext.Current.CancellationToken);
            ControlPeerEvent disconnected = await intentionalDisconnect.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(DeviceConnectionState.Offline, disconnected.State);
            Assert.NotNull(await controllerTrust.FindAsync(
                senderIdentity.Device.DeviceId,
                TestContext.Current.CancellationToken));

            await server.RevokeAsync(
                senderIdentity.Device.DeviceId,
                TestContext.Current.CancellationToken);
            Assert.Null(
                await controllerTrust.FindAsync(
                    senderIdentity.Device.DeviceId,
                    TestContext.Current.CancellationToken));

            await using var revokedClient = new ListenSphereControlClient(
                senderIdentity,
                senderTrust);
            ControlClientResult revoked = await revokedClient.ConnectAsync(
                discovered,
                null,
                TestContext.Current.CancellationToken);
            Assert.Equal(ControlClientOutcome.PairingRequired, revoked.Outcome);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ListenSphere.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
