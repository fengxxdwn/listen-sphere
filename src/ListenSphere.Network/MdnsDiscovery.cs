using System.Globalization;
using System.Net;
using System.Threading.Channels;
using ListenSphere.Device;
using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;

namespace ListenSphere.Network;

public static class ListenSphereDiscovery
{
    public const string ServiceType = "_listensphere._tcp";
    public const string QualifiedServiceName = "_listensphere._tcp.local";
}

public sealed class MdnsControllerPublisher : IDisposable
{
    private readonly ServiceDiscovery discovery;
    private readonly ServiceProfile profile;
    private bool disposed;

    public MdnsControllerPublisher(LocalDeviceIdentity identity, int controlPort)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(controlPort);
        profile = new ServiceProfile(
            $"ListenSphere-{identity.Device.DeviceId:N}",
            ListenSphereDiscovery.ServiceType,
            checked((ushort)controlPort));
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

        discovery = new ServiceDiscovery();
        discovery.Advertise(profile);
        discovery.Announce(profile);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        discovery.Unadvertise(profile);
        discovery.Dispose();
        disposed = true;
    }
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
