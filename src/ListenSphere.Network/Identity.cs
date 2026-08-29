using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Google.Protobuf;
using ListenSphere.Device;
using ListenSphere.Protocol;
using ListenSphere.Protocol.V1;

namespace ListenSphere.Network;

public sealed class LocalDeviceIdentity : IDisposable
{
    internal LocalDeviceIdentity(
        DeviceDescriptor device,
        X509Certificate2 certificate)
    {
        Device = device;
        Certificate = certificate;
        CertificateFingerprint = SHA256.HashData(certificate.RawData);
    }

    public DeviceDescriptor Device { get; }

    public X509Certificate2 Certificate { get; }

    public byte[] CertificateFingerprint { get; }

    public DeviceIdentity ToProtocolIdentity() =>
        new()
        {
            DeviceId = ByteString.CopyFrom(Device.DeviceId.ToByteArray()),
            DisplayName = Device.DisplayName,
            Platform = Device.Platform.ToString(),
            Capabilities = (ulong)Device.Capabilities,
            CertificateFingerprint = ByteString.CopyFrom(CertificateFingerprint)
        };

    public void Dispose() => Certificate.Dispose();
}

public static class LocalIdentityStore
{
    private const string CertificateFileName = "identity.pfx";
    private const string MetadataFileName = "identity.json";

    public static LocalDeviceIdentity LoadOrCreate(
        string directory,
        string displayName,
        DeviceCapabilities capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Directory.CreateDirectory(directory);

        string certificatePath = Path.Combine(directory, CertificateFileName);
        string metadataPath = Path.Combine(directory, MetadataFileName);
        if (File.Exists(certificatePath) && File.Exists(metadataPath))
        {
            IdentityMetadata metadata = JsonSerializer.Deserialize<IdentityMetadata>(
                File.ReadAllText(metadataPath))
                ?? throw new InvalidDataException("ListenSphere identity metadata is empty.");
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                password: null,
                X509KeyStorageFlags.UserKeySet |
                    X509KeyStorageFlags.PersistKeySet |
                    X509KeyStorageFlags.Exportable);
            return Create(metadata.DeviceId, displayName, capabilities, certificate);
        }

        Guid deviceId = Guid.NewGuid();
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=ListenSphere-{deviceId:N}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new("1.3.6.1.5.5.7.3.1"),
                    new("1.3.6.1.5.5.7.3.2")
                },
                true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using X509Certificate2 generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        File.WriteAllBytes(certificatePath, pfx);
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(new IdentityMetadata(deviceId)));

        var loaded = X509CertificateLoader.LoadPkcs12(
            pfx,
            password: null,
            X509KeyStorageFlags.UserKeySet |
                X509KeyStorageFlags.PersistKeySet |
                X509KeyStorageFlags.Exportable);
        return Create(deviceId, displayName, capabilities, loaded);
    }

    private static LocalDeviceIdentity Create(
        Guid deviceId,
        string displayName,
        DeviceCapabilities capabilities,
        X509Certificate2 certificate) =>
        new(
            new DeviceDescriptor(
                deviceId,
                displayName,
                DevicePlatform.Windows,
                capabilities,
                ProtocolConstants.MajorVersion,
                ProtocolConstants.MinorVersion),
            certificate);

    private sealed record IdentityMetadata(Guid DeviceId);
}

public static class ProtocolIdentity
{
    public static DeviceDescriptor ToDescriptor(DeviceIdentity identity)
    {
        if (identity.DeviceId.Length != 16)
        {
            throw new InvalidDataException("Device identity does not contain a valid UUID.");
        }

        if (!Enum.TryParse(identity.Platform, true, out DevicePlatform platform))
        {
            platform = DevicePlatform.Unknown;
        }

        return new DeviceDescriptor(
            new Guid(identity.DeviceId.Span),
            identity.DisplayName,
            platform,
            (DeviceCapabilities)identity.Capabilities,
            ProtocolConstants.MajorVersion,
            ProtocolConstants.MinorVersion);
    }
}
