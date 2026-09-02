using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using ListenSphere.Protocol.V1;
using Xunit;

namespace ListenSphere.Protocol.Tests;

public sealed class ProtocolCompatibilityTests
{
    [Fact]
    public void ProtocolConstants_KeepVersionAndCompatibilityContract()
    {
        Assert.Equal((ushort)1, ProtocolConstants.MajorVersion);
        Assert.Equal((ushort)1, ProtocolConstants.MinorVersion);
        Assert.True(ProtocolConstants.IsCompatible(
            new ProtocolVersion { Major = 1, Minor = uint.MaxValue }));
        Assert.False(ProtocolConstants.IsCompatible(
            new ProtocolVersion { Major = 2, Minor = 0 }));
        Assert.False(ProtocolConstants.IsCompatible(null));
        Assert.Equal(4, ControlFrameCodec.PrefixLength);
        Assert.Equal(1024 * 1024, ControlFrameCodec.MaximumMessageLength);
        Assert.Equal(60, AudioPacketHeader.Size);
        Assert.Equal(1_200, AudioPacketHeader.MaximumDatagramSize);
    }

    [Fact]
    public void ControlSchema_MatchesFrozenFieldNumberSnapshot()
    {
        string expected = File.ReadAllText(VectorPath("control-schema-v1.txt"))
            .ReplaceLineEndings("\n")
            .TrimEnd();

        string actual = CaptureSchema().ReplaceLineEndings("\n").TrimEnd();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void HelloEnvelope_WritesTheCrossPlatformGoldenVector()
    {
        var identity = new DeviceIdentity
        {
            DeviceId = ByteString.CopyFrom(Convert.FromHexString(
                "33221100554477668899AABBCCDDEEFF")),
            DisplayName = "Vector Sender",
            Platform = "Android",
            Capabilities = 2,
            CertificateFingerprint = ByteString.CopyFrom(Convert.FromHexString(
                "404142434445464748494A4B4C4D4E4F" +
                "505152535455565758595A5B5C5D5E5F"))
        };
        var envelope = new Envelope
        {
            Version = new ProtocolVersion { Major = 1, Minor = 1 },
            RequestId = 0x0102030405060708UL,
            HelloRequest = new HelloRequest
            {
                Device = identity,
                DeviceCertificate = ByteString.CopyFrom([1, 2, 3, 4]),
                ClientNonce = ByteString.CopyFrom(Enumerable.Range(0x20, 32)
                    .Select(value => checked((byte)value)).ToArray()),
                IdentitySignature = ByteString.CopyFrom([0xA0, 0xA1, 0xA2, 0xA3]),
                SourceId = "android-default",
                SourceName = "Vector Audio",
                SourceKind = "device_playback"
            }
        };
        string expected = File.ReadAllText(VectorPath("hello-envelope-v1.hex")).Trim();

        Assert.Equal(expected, Convert.ToHexStringLower(envelope.ToByteArray()));
        Envelope parsed = Envelope.Parser.ParseFrom(Convert.FromHexString(expected));
        Assert.Equal(envelope, parsed);
    }

    [Fact]
    public void DeviceProofPayload_UsesFrozenDotNetGuidAndHashLayout()
    {
        IReadOnlyDictionary<string, string> vector = File
            .ReadAllLines(VectorPath("device-proof-payload-v1.txt"))
            .Where(line => line.Length > 0)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        byte[] deviceIdBytes = Guid.Parse(vector["device_id"]).ToByteArray();
        byte[] payload =
        [
            .. Convert.FromHexString(vector["context"]),
            .. Convert.FromHexString(vector["controller_fingerprint"]),
            .. deviceIdBytes,
            .. Convert.FromHexString(vector["client_nonce"])
        ];

        Assert.Equal(vector["device_id_dotnet"], Convert.ToHexStringLower(deviceIdBytes));
        Assert.Equal(vector["payload"], Convert.ToHexStringLower(payload));
        Assert.Equal(
            vector["sha256"],
            Convert.ToHexStringLower(SHA256.HashData(payload)));
    }

    private static string CaptureSchema()
    {
        var lines = new List<string>();
        FileDescriptor file = ControlReflection.Descriptor;
        foreach (MessageDescriptor message in file.MessageTypes)
        {
            var proto = message.ToProto();
            string reservedNumbers = proto.ReservedRange.Count == 0
                ? "-"
                : string.Join(',', proto.ReservedRange.Select(range =>
                    $"{range.Start}-{range.End - 1}"));
            string reservedNames = proto.ReservedName.Count == 0
                ? "-"
                : string.Join(',', proto.ReservedName);
            lines.Add(
                $"message {message.Name} reserved_numbers={reservedNumbers} " +
                $"reserved_names={reservedNames}");
            foreach (FieldDescriptor field in message.Fields.InFieldNumberOrder())
            {
                lines.Add(
                    $"  {field.FieldNumber} {field.Name} {field.FieldType} " +
                    $"oneof={field.ContainingOneof?.Name ?? "-"}");
            }
        }

        foreach (EnumDescriptor enumeration in file.EnumTypes)
        {
            lines.Add($"enum {enumeration.Name}");
            foreach (EnumValueDescriptor value in enumeration.Values)
            {
                lines.Add($"  {value.Number} {value.Name}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string VectorPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "vectors", fileName);
}
