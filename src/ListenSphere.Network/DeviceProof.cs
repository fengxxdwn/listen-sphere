using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using ListenSphere.Protocol.V1;

namespace ListenSphere.Network;

public static class DeviceProof
{
    private const int FingerprintSize = 32;
    private const int NonceSize = 32;
    private static readonly byte[] Context =
        Encoding.ASCII.GetBytes("ListenSphere-Device-Proof-v1\0");

    public static HelloRequest Create(
        LocalDeviceIdentity identity,
        ReadOnlySpan<byte> controllerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (controllerFingerprint.Length != FingerprintSize)
        {
            throw new AuthenticationException("Controller certificate fingerprint is invalid.");
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        DeviceIdentity device = identity.ToProtocolIdentity();
        byte[] payload = BuildPayload(
            controllerFingerprint,
            device.DeviceId.Span,
            nonce);
        byte[] signature = Sign(identity.Certificate, payload);
        return new HelloRequest
        {
            Device = device,
            DeviceCertificate = ByteString.CopyFrom(identity.Certificate.RawData),
            ClientNonce = ByteString.CopyFrom(nonce),
            IdentitySignature = ByteString.CopyFrom(signature)
        };
    }

    public static byte[] Validate(
        HelloRequest request,
        ReadOnlySpan<byte> controllerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Device is null ||
            request.Device.DeviceId.Length != 16 ||
            request.Device.CertificateFingerprint.Length != FingerprintSize ||
            request.DeviceCertificate.IsEmpty ||
            request.ClientNonce.Length != NonceSize ||
            request.IdentitySignature.IsEmpty ||
            controllerFingerprint.Length != FingerprintSize)
        {
            throw new AuthenticationException("Device identity proof is incomplete.");
        }

        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(
            request.DeviceCertificate.ToByteArray());
        DateTime now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() ||
            now > certificate.NotAfter.ToUniversalTime())
        {
            throw new AuthenticationException("Device identity certificate is not currently valid.");
        }

        byte[] fingerprint = SHA256.HashData(certificate.RawData);
        if (!CryptographicOperations.FixedTimeEquals(
            fingerprint,
            request.Device.CertificateFingerprint.Span))
        {
            throw new AuthenticationException("Device certificate fingerprint does not match its identity.");
        }

        byte[] payload = BuildPayload(
            controllerFingerprint,
            request.Device.DeviceId.Span,
            request.ClientNonce.Span);
        if (!Verify(certificate, payload, request.IdentitySignature.Span))
        {
            throw new AuthenticationException("Device identity signature is invalid.");
        }

        return fingerprint;
    }

    private static byte[] BuildPayload(
        ReadOnlySpan<byte> controllerFingerprint,
        ReadOnlySpan<byte> deviceId,
        ReadOnlySpan<byte> nonce)
    {
        byte[] payload = new byte[
            Context.Length + controllerFingerprint.Length + deviceId.Length + nonce.Length];
        Span<byte> destination = payload;
        Context.CopyTo(destination);
        controllerFingerprint.CopyTo(destination[Context.Length..]);
        deviceId.CopyTo(destination[(Context.Length + controllerFingerprint.Length)..]);
        nonce.CopyTo(destination[(Context.Length + controllerFingerprint.Length + deviceId.Length)..]);
        return payload;
    }

    private static byte[] Sign(X509Certificate2 certificate, byte[] payload)
    {
        using RSA? rsa = certificate.GetRSAPrivateKey();
        if (rsa is not null)
        {
            return rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        using ECDsa? ecdsa = certificate.GetECDsaPrivateKey();
        if (ecdsa is not null)
        {
            return ecdsa.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }

        throw new AuthenticationException("Device identity does not contain a supported signing key.");
    }

    private static bool Verify(
        X509Certificate2 certificate,
        byte[] payload,
        ReadOnlySpan<byte> signature)
    {
        using RSA? rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
        {
            return rsa.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }

        using ECDsa? ecdsa = certificate.GetECDsaPublicKey();
        return ecdsa is not null &&
            ecdsa.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
    }
}
