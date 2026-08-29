using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using ListenSphere.Device;
using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;

namespace ListenSphere.Network;

public static class ListenSphereDiscovery
{
    public const string ServiceType = "_listensphere._tcp";
    public const string QualifiedServiceName = "_listensphere._tcp.local";

    public static bool IsLocalNetworkAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        IPAddress normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (normalized.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = normalized.GetAddressBytes();
            return bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168);
        }

        if (normalized.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte first = normalized.GetAddressBytes()[0];
            return normalized.IsIPv6LinkLocal || (first & 0xFE) == 0xFC;
        }

        return false;
    }

    public static bool IsLocalMachineAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        IPAddress normalized = Normalize(address);
        if (IPAddress.IsLoopback(normalized))
        {
            return true;
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(candidate => candidate.GetIPProperties().UnicastAddresses)
            .Select(candidate => Normalize(candidate.Address))
            .Any(candidate => candidate.Equals(normalized));
    }

    /// <summary>
    /// Returns active private IPv4 addresses suitable for the manual-connect field.
    /// Unlike mDNS publication this includes Windows hotspot and other virtual LAN
    /// adapters, because multicast discovery may be unavailable on those interfaces.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetManualConnectAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(candidate => candidate.OperationalStatus == OperationalStatus.Up)
            .Where(candidate => candidate.NetworkInterfaceType is not
                NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(candidate => candidate.GetIPProperties().UnicastAddresses)
            .Select(candidate => Normalize(candidate.Address))
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Where(IsLocalNetworkAddress)
            .Distinct()
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();

    public static string FormatManualConnectEndpoints(
        IEnumerable<IPAddress> addresses,
        int port)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        return string.Join(
            " · ",
            addresses
                .Select(Normalize)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Where(IsLocalNetworkAddress)
                .Distinct()
                .OrderBy(address => address.ToString(), StringComparer.Ordinal)
                .Select(address => $"{address}:{port}"));
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

public sealed class MdnsControllerPublisher : IDisposable
{
    private readonly List<InterfaceAdvertisement> advertisements = [];
    private bool disposed;

    public MdnsControllerPublisher(LocalDeviceIdentity identity, int controlPort)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(controlPort);
        NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsEligibleInterface)
            .ToArray();
        if (interfaces.Length == 0)
        {
            throw new InvalidOperationException(
                "No active private LAN interface is available for ListenSphere discovery.");
        }

        try
        {
            foreach (NetworkInterface networkInterface in interfaces)
            {
                IPAddress[] addresses = networkInterface.GetIPProperties().UnicastAddresses
                    .Select(candidate => candidate.Address)
                    .Where(ListenSphereDiscovery.IsLocalNetworkAddress)
                    .Distinct()
                    .ToArray();
                if (addresses.Length == 0)
                {
                    continue;
                }

                ServiceProfile profile = CreateProfile(identity, controlPort, addresses);
                string interfaceId = networkInterface.Id;
                var multicast = new MulticastService(candidates =>
                    candidates.Where(candidate => string.Equals(
                        candidate.Id,
                        interfaceId,
                        StringComparison.OrdinalIgnoreCase)));
                multicast.Start();
                var discovery = new ServiceDiscovery(multicast);
                discovery.Advertise(profile);
                discovery.Announce(profile);
                advertisements.Add(new InterfaceAdvertisement(discovery, multicast, profile));
            }
        }
        catch
        {
            Dispose();
            throw;
        }

        if (advertisements.Count == 0)
        {
            throw new InvalidOperationException(
                "No active private LAN interface could publish ListenSphere discovery.");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (InterfaceAdvertisement advertisement in advertisements)
        {
            advertisement.Discovery.Unadvertise(advertisement.Profile);
            advertisement.Discovery.Dispose();
            advertisement.Multicast.Stop();
            advertisement.Multicast.Dispose();
        }

        advertisements.Clear();
        disposed = true;
    }

    private static ServiceProfile CreateProfile(
        LocalDeviceIdentity identity,
        int controlPort,
        IPAddress[] addresses)
    {
        var profile = new ServiceProfile(
            $"ListenSphere-{identity.Device.DeviceId:N}",
            ListenSphereDiscovery.ServiceType,
            checked((ushort)controlPort),
            addresses);
        profile.AddProperty(
            "pv",
            $"{identity.Device.ProtocolMajor}.{identity.Device.ProtocolMinor}");
        profile.AddProperty("id", identity.Device.DeviceId.ToString("D"));
        profile.AddProperty("name", identity.Device.DisplayName);
        profile.AddProperty(
            "platform",
            identity.Device.Platform.ToString().ToLowerInvariant());
        profile.AddProperty(
            "caps",
            ((ulong)identity.Device.Capabilities).ToString("x16", CultureInfo.InvariantCulture));
        return profile;
    }

    private sealed record InterfaceAdvertisement(
        ServiceDiscovery Discovery,
        MulticastService Multicast,
        ServiceProfile Profile);

    private static bool IsEligibleInterface(NetworkInterface candidate) =>
        candidate.OperationalStatus == OperationalStatus.Up &&
        candidate.NetworkInterfaceType is
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
        candidate.GetIPProperties().UnicastAddresses.Any(address =>
            ListenSphereDiscovery.IsLocalNetworkAddress(address.Address));
}

public sealed class MdnsDiscoveryService : IDiscoveryService
{
    private readonly MulticastService multicast = new();
    private bool disposed;

    public async IAsyncEnumerable<DiscoveredController> DiscoverAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var channel = Channel.CreateBounded<DiscoveredController>(
            new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        EventHandler<MessageEventArgs> handler = (_, eventArgs) =>
        {
            if (TryParse(eventArgs, out DiscoveredController? controller) &&
                controller is not null)
            {
                channel.Writer.TryWrite(controller);
            }
        };
        multicast.AnswerReceived += handler;
        multicast.Start();

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            multicast.SendQuery(
                ListenSphereDiscovery.QualifiedServiceName,
                DnsClass.IN,
                DnsType.PTR);
            Task<bool> nextQuery = timer.WaitForNextTickAsync(cancellationToken).AsTask();
            Task<DiscoveredController> nextController =
                channel.Reader.ReadAsync(cancellationToken).AsTask();
            while (!cancellationToken.IsCancellationRequested)
            {
                Task completed = await Task.WhenAny(nextQuery, nextController)
                    .ConfigureAwait(false);
                if (completed == nextController)
                {
                    yield return await nextController.ConfigureAwait(false);
                    nextController = channel.Reader.ReadAsync(cancellationToken).AsTask();
                }
                else
                {
                    if (!await nextQuery.ConfigureAwait(false))
                    {
                        yield break;
                    }

                    multicast.SendQuery(
                        ListenSphereDiscovery.QualifiedServiceName,
                        DnsClass.IN,
                        DnsType.PTR);
                    nextQuery = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                }
            }
        }
        finally
        {
            multicast.AnswerReceived -= handler;
            multicast.Stop();
            channel.Writer.TryComplete();
        }
    }

    private static bool TryParse(
        MessageEventArgs eventArgs,
        out DiscoveredController? controller)
    {
        controller = null;
        IEnumerable<ResourceRecord> records = eventArgs.Message.Answers
            .Concat(eventArgs.Message.AdditionalRecords);
        SRVRecord? service = records
            .OfType<SRVRecord>()
            .FirstOrDefault(record =>
                record.Name.ToString().EndsWith(
                    ListenSphereDiscovery.QualifiedServiceName,
                    StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            return false;
        }

        TXTRecord? text = records
            .OfType<TXTRecord>()
            .FirstOrDefault(record => record.Name == service.Name);
        if (text is null)
        {
            return false;
        }

        Dictionary<string, string> properties = text.Strings
            .Select(value => value.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        if (!properties.TryGetValue("id", out string? idText) ||
            !Guid.TryParse(idText, out Guid deviceId) ||
            !properties.TryGetValue("pv", out string? versionText))
        {
            return false;
        }

        string[] versionParts = versionText.Split('.', 2);
        if (versionParts.Length != 2 ||
            !ushort.TryParse(versionParts[0], CultureInfo.InvariantCulture, out ushort major) ||
            !ushort.TryParse(versionParts[1], CultureInfo.InvariantCulture, out ushort minor))
        {
            return false;
        }

        _ = properties.TryGetValue("name", out string? displayName);
        _ = properties.TryGetValue("platform", out string? platformText);
        _ = properties.TryGetValue("caps", out string? capabilitiesText);
        _ = Enum.TryParse(platformText, true, out DevicePlatform platform);
        _ = ulong.TryParse(
            capabilitiesText,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out ulong capabilities);

        var descriptor = new DeviceDescriptor(
            deviceId,
            string.IsNullOrWhiteSpace(displayName) ? "ListenSphere Controller" : displayName,
            platform,
            (DeviceCapabilities)capabilities,
            major,
            minor);
        controller = new DiscoveredController(
            descriptor,
            new IPEndPoint(eventArgs.RemoteEndPoint.Address, service.Port));
        return true;
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            multicast.Dispose();
            disposed = true;
        }

        return ValueTask.CompletedTask;
    }
}
