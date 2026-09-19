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

public static class ControlTransportPreface
{
    public const int Size = 5;

    public static bool TryParse(ReadOnlySpan<byte> value, out string transport)
    {
        transport = "Wireless";
        if (value.Length != Size || !value[..4].SequenceEqual("LSTH"u8))
        {
            return false;
        }

        transport = value[4] switch
        {
            3 => "Wired",
            2 => "Bluetooth",
            _ => "Wireless"
        };
        return true;
    }
    internal static async ValueTask<string> ReadConnectionTransportAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        byte[] firstByte = new byte[1];
        int received = await client.Client.ReceiveAsync(
            firstByte,
            SocketFlags.Peek,
            cancellationToken).ConfigureAwait(false);
        if (received == 0 || firstByte[0] != (byte)'L')
        {
            return "Wireless";
        }

        byte[] preface = new byte[ControlTransportPreface.Size];
        await client.GetStream().ReadExactlyAsync(preface, cancellationToken).ConfigureAwait(false);
        if (!ControlTransportPreface.TryParse(preface, out string transport))
        {
            throw new InvalidDataException("ListenSphere transport preface is invalid.");
        }

        return transport;
    }

}
