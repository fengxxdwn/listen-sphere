using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ListenSphere.Protocol;

public static class AudioPayloadProtector
{
    public const int TagSize = 16;
    public const int SaltSize = 4;

    public static byte[] Protect(
        AudioPacketHeader template,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> sessionSalt)
    {
        var datagram = new byte[checked(AudioPacketHeader.Size + plaintext.Length + TagSize)];
        Protect(template, plaintext, key, sessionSalt, datagram);
        return datagram;
    }

    /// <summary>Writes into caller-owned storage; no plaintext or destination memory is retained.</summary>
    public static int Protect(
        AudioPacketHeader template,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> sessionSalt,
        Span<byte> destination)
    {
        ValidateCryptographicInputs(key, sessionSalt);
        var protectedLength = checked(plaintext.Length + TagSize);
        var header = template with
        {
            Flags = template.Flags | AudioPacketFlags.Encrypted,
            PayloadLength = checked((ushort)protectedLength)
        };

        int datagramLength = AudioPacketHeader.Size + protectedLength;
        if (destination.Length < datagramLength)
            throw new ArgumentException("Destination is too small for the protected datagram.", nameof(destination));
        Span<byte> datagram = destination[..datagramLength];
        var headerBytes = datagram[..AudioPacketHeader.Size];
        header.Write(headerBytes);
        var ciphertext = datagram.Slice(AudioPacketHeader.Size, plaintext.Length);
        var tag = datagram.Slice(AudioPacketHeader.Size + plaintext.Length, TagSize);
        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(header, sessionSalt, nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, headerBytes);
        return datagramLength;
    }

    public static bool TryUnprotect(
        ReadOnlySpan<byte> datagram,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> sessionSalt,
        out AudioPacketHeader header,
        out byte[] plaintext)
    {
        plaintext = [];
        ValidateCryptographicInputs(key, sessionSalt);
        if (!AudioPacketHeader.TryReadDatagram(datagram, out header, out var payload, out _) ||
            !header.Flags.HasFlag(AudioPacketFlags.Encrypted) ||
            payload.Length < TagSize)
        {
            return false;
        }

        var ciphertext = payload[..^TagSize];
        var tag = payload[^TagSize..];
        plaintext = new byte[ciphertext.Length];
        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(header, sessionSalt, nonce);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, datagram[..AudioPacketHeader.Size]);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            plaintext = [];
            return false;
        }
    }

    private static void BuildNonce(
        AudioPacketHeader header,
        ReadOnlySpan<byte> sessionSalt,
        Span<byte> nonce)
    {
        sessionSalt.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[4..], header.StreamId);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[8..], header.PacketSequence);
    }

    private static void ValidateCryptographicInputs(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> sessionSalt)
    {
        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException("AES-GCM keys must contain 16, 24, or 32 bytes.", nameof(key));
        }

        if (sessionSalt.Length != SaltSize)
        {
            throw new ArgumentException($"The session salt must contain {SaltSize} bytes.", nameof(sessionSalt));
        }
    }
}

