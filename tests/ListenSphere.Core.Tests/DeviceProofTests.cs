using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using ListenSphere.Device;
using ListenSphere.Network;
using ListenSphere.Protocol.V1;
using Xunit;

namespace ListenSphere.Core.Tests;

public sealed class DeviceProofTests
{
    [Fact]
    public void Validate_AcceptsSharedCrossPlatformSignatureVector()
    {
        IReadOnlyDictionary<string, string> vector = ReadVector();
        var request = new HelloRequest
        {
            Device = new DeviceIdentity
            {
                DeviceId = ByteString.CopyFrom(Convert.FromHexString(
                    vector["device_id_dotnet"])),
                DisplayName = "Vector Sender",
                Platform = "Android",
                Capabilities = (ulong)DeviceCapabilities.AudioSend,
                CertificateFingerprint = ByteString.CopyFrom(Convert.FromHexString(
                    vector["certificate_fingerprint"]))
            },
            DeviceCertificate = ByteString.CopyFrom(Convert.FromHexString(
                vector["certificate_der"])),
            ClientNonce = ByteString.CopyFrom(Convert.FromHexString(
                vector["client_nonce"])),
            IdentitySignature = ByteString.CopyFrom(Convert.FromHexString(
                vector["signature"]))
        };

        byte[] fingerprint = DeviceProof.Validate(
            request,
            Convert.FromHexString(vector["controller_fingerprint"]));

        Assert.Equal(vector["certificate_fingerprint"], Convert.ToHexStringLower(fingerprint));
    }

    [Fact]
    public void CreateAndValidate_BindsControllerFingerprintDeviceIdAndNonce()
    {
        string directory = CreateTemporaryDirectory();
        using LocalDeviceIdentity identity = LocalIdentityStore.LoadOrCreate(
            directory,
            "Proof Sender",
            DeviceCapabilities.AudioSend);
        byte[] controllerFingerprint = Enumerable.Range(0, 32)
            .Select(value => checked((byte)value))
            .ToArray();
        try
        {
            HelloRequest request = DeviceProof.Create(identity, controllerFingerprint);

            byte[] validated = DeviceProof.Validate(request, controllerFingerprint);

            Assert.Equal(identity.CertificateFingerprint, validated);
            Assert.Equal(32, request.ClientNonce.Length);
            byte[] payload =
            [
                .. Encoding.ASCII.GetBytes("ListenSphere-Device-Proof-v1\0"),
                .. controllerFingerprint,
                .. request.Device.DeviceId.ToByteArray(),
                .. request.ClientNonce.ToByteArray()
            ];
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(
                request.DeviceCertificate.ToByteArray());
            using RSA rsa = Assert.IsAssignableFrom<RSA>(certificate.GetRSAPublicKey());
            Assert.True(rsa.VerifyData(
                payload,
                request.IdentitySignature.Span,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Validate_RejectsEverySignedBindingMutation()
    {
        string directory = CreateTemporaryDirectory();
        using LocalDeviceIdentity identity = LocalIdentityStore.LoadOrCreate(
            directory,
            "Proof Sender",
            DeviceCapabilities.AudioSend);
        byte[] controllerFingerprint = RandomNumberGenerator.GetBytes(32);
        try
        {
            HelloRequest original = DeviceProof.Create(identity, controllerFingerprint);

            byte[] wrongController = controllerFingerprint.ToArray();
            wrongController[0] ^= 0x80;
            Assert.Throws<AuthenticationException>(() =>
                DeviceProof.Validate(original, wrongController));

            HelloRequest wrongNonce = original.Clone();
            wrongNonce.ClientNonce = FlipFirstByte(wrongNonce.ClientNonce);
            Assert.Throws<AuthenticationException>(() =>
                DeviceProof.Validate(wrongNonce, controllerFingerprint));

            HelloRequest wrongSignature = original.Clone();
            wrongSignature.IdentitySignature = FlipFirstByte(
                wrongSignature.IdentitySignature);
            Assert.Throws<AuthenticationException>(() =>
                DeviceProof.Validate(wrongSignature, controllerFingerprint));

            HelloRequest wrongDeviceId = original.Clone();
            wrongDeviceId.Device.DeviceId = FlipFirstByte(
                wrongDeviceId.Device.DeviceId);
            Assert.Throws<AuthenticationException>(() =>
                DeviceProof.Validate(wrongDeviceId, controllerFingerprint));

            HelloRequest wrongCertificateFingerprint = original.Clone();
            wrongCertificateFingerprint.Device.CertificateFingerprint = FlipFirstByte(
                wrongCertificateFingerprint.Device.CertificateFingerprint);
            Assert.Throws<AuthenticationException>(() =>
                DeviceProof.Validate(wrongCertificateFingerprint, controllerFingerprint));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static ByteString FlipFirstByte(ByteString source)
    {
        byte[] changed = source.ToByteArray();
        changed[0] ^= 0x01;
        return ByteString.CopyFrom(changed);
    }

    private static IReadOnlyDictionary<string, string> ReadVector() => File
        .ReadAllLines(Path.Combine(
            AppContext.BaseDirectory,
            "vectors",
            "device-proof-payload-v1.txt"))
        .Where(line => line.Length > 0)
        .Select(line => line.Split('=', 2))
        .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

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
