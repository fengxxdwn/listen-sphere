using System.Net;
using ListenSphere.Device;
using ListenSphere.Network;
using Xunit;

namespace ListenSphere.Core.Tests;

public sealed class NetworkControlTests
{
    [Fact]
    public void PairingCode_IsSixDigitsAndSingleUse()
    {
        var service = new PairingCodeService();

        PairingCode code = service.Generate();

        Assert.Matches("^[0-9]{6}$", code.Value);
        Assert.Equal(PairingCodeValidation.Accepted, service.ValidateAndConsume(code.Value));
        Assert.Equal(PairingCodeValidation.Expired, service.ValidateAndConsume(code.Value));
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
            Assert.Equal(
                device,
                await reloaded.FindAsync(
                    device.DeviceId,
                    TestContext.Current.CancellationToken));

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
