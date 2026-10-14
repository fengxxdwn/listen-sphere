using System.Net;
using System.Text;
using ListenSphere.Device;
using ListenSphere.Network;
using MeaMod.DNS.Model;
using Xunit;

namespace ListenSphere.Core.Tests;

public sealed class MdnsDiscoveryTests
{
    [Fact]
    public void CreateProfile_WithUnicodeDeviceName_UsesSerializableAsciiTxtName()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "ListenSphere.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string unicodeDisplayName = "中文电脑 Controller";
            using LocalDeviceIdentity identity = LocalIdentityStore.LoadOrCreate(
                directory,
                unicodeDisplayName,
                DeviceCapabilities.Controller | DeviceCapabilities.AudioReceive);

            var profile = MdnsControllerPublisher.CreateProfile(
                identity,
                45000,
                [IPAddress.Loopback]);
            var message = new Message { QR = true };
            message.Answers.AddRange(profile.Resources);

            byte[] wireBytes = message.ToByteArray();
            TXTRecord text = Assert.Single(profile.Resources.OfType<TXTRecord>());
            string advertisedName = Assert.Single(
                text.Strings,
                value => value.StartsWith("name=", StringComparison.Ordinal))
                ["name=".Length..];

            Assert.NotEmpty(wireBytes);
            Assert.Equal("utf8-5Lit5paH55S16ISRIENvbnRyb2xsZXI", advertisedName);
            Assert.All(advertisedName, character => Assert.True(char.IsAscii(character)));
            Assert.Equal(unicodeDisplayName, identity.Device.DisplayName);
            Assert.Equal(unicodeDisplayName, identity.ToProtocolIdentity().DisplayName);
            Assert.Equal(advertisedName, Encoding.ASCII.GetString(
                Encoding.ASCII.GetBytes(advertisedName)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ToAsciiTxtName_WithAsciiName_PreservesOriginalValue()
    {
        const string displayName = "Studio-PC Controller";

        string advertisedName = MdnsControllerPublisher.ToAsciiTxtName(displayName);

        Assert.Equal(displayName, advertisedName);
    }

    [Fact]
    public void ToAsciiTxtName_WithChineseComputerName_UsesStableBase64UrlEncoding()
    {
        const string displayName = "月 Controller";

        string advertisedName = MdnsControllerPublisher.ToAsciiTxtName(displayName);

        Assert.Equal("utf8-5pyIIENvbnRyb2xsZXI", advertisedName);
        Assert.All(advertisedName, character => Assert.True(char.IsAscii(character)));
        Assert.DoesNotContain('=', advertisedName);
        Assert.DoesNotContain('+', advertisedName);
        Assert.DoesNotContain('/', advertisedName);
    }
}
