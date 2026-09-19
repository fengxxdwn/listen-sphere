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

public sealed record ControlPeerEvent(
    DeviceDescriptor? Device,
    DeviceConnectionState State,
    string Message,
    DateTimeOffset Timestamp,
    Guid? AudioSessionId = null,
    string Transport = "Wireless",
    Guid? ChannelId = null,
    string? SourceName = null,
    string? SourceKind = null);

public sealed record OpenedAudioStream(
    Guid ChannelId,
    string SourceId,
    string DisplayName,
    string SourceKind,
    AudioSessionParameters Session);

public static class AudioStreamIdentity
{
    public static Guid CreateChannelId(Guid deviceId, string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        byte[] identity = System.Text.Encoding.UTF8.GetBytes(
            $"ListenSphere/remote/{deviceId:D}/{sourceId.Trim().ToUpperInvariant()}");
        return new Guid(SHA256.HashData(identity).AsSpan(0, 16));
    }
}

internal sealed record ControlServerClient(TcpClient Client, SslStream Stream);

internal sealed record AdditionalServerAudioStream(
    Guid ChannelId,
    string SourceId,
    string DisplayName,
    string SourceKind,
    AudioSessionParameters Session);

public enum ControlClientOutcome
{
    Connected,
    PairingRequired,
    PairingRejected
}

public sealed record ControlClientResult(
    ControlClientOutcome Outcome,
    DeviceDescriptor Controller,
    string Message,
    AudioSessionParameters? AudioSession = null);

public sealed record ControlClientStatistics(
    double LastRoundTripMilliseconds,
    long SuccessfulHeartbeats,
    long FailedHeartbeats);

internal sealed class ControllerRequestedDisconnectException(string message)
    : IOException(message);
