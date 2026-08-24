using System.Net;
using ListenSphere.Device;
using ListenSphere.Protocol.V1;

namespace ListenSphere.Network;

public sealed record DiscoveredController(DeviceDescriptor Device, IPEndPoint ControlEndpoint);

/// <summary>Discovers ListenSphere controllers on the local network.</summary>
public interface IDiscoveryService : IAsyncDisposable
{
    IAsyncEnumerable<DiscoveredController> DiscoverAsync(CancellationToken cancellationToken);
}

/// <summary>Creates and revokes pinned, trusted device relationships.</summary>
public interface IPairingService
{
    ValueTask<DeviceStatus> PairAsync(
        DiscoveredController controller,
        string oneTimeCode,
        CancellationToken cancellationToken);

    ValueTask RevokeAsync(Guid deviceId, CancellationToken cancellationToken);
}

/// <summary>Exchanges framed Protobuf messages over an authenticated control channel.</summary>
public interface IControlConnection : IAsyncDisposable
{
    DeviceDescriptor RemoteDevice { get; }
    ValueTask SendAsync(Envelope message, CancellationToken cancellationToken);
    IAsyncEnumerable<Envelope> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>Sends and receives bounded ListenSphere audio datagrams.</summary>
public interface IAudioDatagramTransport : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken);
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken);
}
