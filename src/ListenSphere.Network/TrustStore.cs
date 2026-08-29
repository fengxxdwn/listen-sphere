using System.Text.Json;
using System.Text.Json.Serialization;

namespace ListenSphere.Network;

public sealed record TrustedDevice(
    Guid DeviceId,
    string DisplayName,
    string Platform,
    ulong Capabilities,
    string CertificateFingerprint,
    DateTimeOffset PairedAt,
    DateTimeOffset LastSeen,
    string Transport = "Wireless",
    string[]? Transports = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> ObservedTransports =>
        Transports is { Length: > 0 } ? Transports : [Transport];

    public bool SupportsTransport(string transport) => ObservedTransports.Contains(
        transport,
        StringComparer.OrdinalIgnoreCase);
}

public interface ITrustedDeviceStore
{
    ValueTask<IReadOnlyList<TrustedDevice>> GetAllAsync(CancellationToken cancellationToken);

    ValueTask<TrustedDevice?> FindAsync(Guid deviceId, CancellationToken cancellationToken);

    ValueTask UpsertAsync(TrustedDevice device, CancellationToken cancellationToken);

    ValueTask RemoveAsync(Guid deviceId, CancellationToken cancellationToken);
}

public sealed class JsonTrustedDeviceStore : ITrustedDeviceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);

    public JsonTrustedDeviceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = path;
    }

    public async ValueTask<IReadOnlyList<TrustedDevice>> GetAllAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<TrustedDevice?> FindAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TrustedDevice> devices = await GetAllAsync(cancellationToken)
            .ConfigureAwait(false);
        return devices.FirstOrDefault(candidate => candidate.DeviceId == deviceId);
    }

    public async ValueTask UpsertAsync(
        TrustedDevice device,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<TrustedDevice> devices = await ReadUnsafeAsync(cancellationToken)
                .ConfigureAwait(false);
            TrustedDevice? existing = devices.FirstOrDefault(
                candidate => candidate.DeviceId == device.DeviceId);
            if (existing is not null && string.Equals(
                    existing.CertificateFingerprint,
                    device.CertificateFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                string[] transports = existing.ObservedTransports
                    .Concat(device.ObservedTransports)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(transport => transport, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                device = device with
                {
                    PairedAt = existing.PairedAt,
                    Transports = transports
                };
            }
            else
            {
                device = device with
                {
                    Transports = device.ObservedTransports
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                };
            }

            devices.RemoveAll(candidate => candidate.DeviceId == device.DeviceId);
            devices.Add(device);
            await WriteUnsafeAsync(devices, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask RemoveAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<TrustedDevice> devices = await ReadUnsafeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (devices.RemoveAll(candidate => candidate.DeviceId == deviceId) > 0)
            {
                await WriteUnsafeAsync(devices, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<List<TrustedDevice>> ReadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<TrustedDevice>>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false) ?? [];
    }

    private async ValueTask WriteUnsafeAsync(
        IReadOnlyList<TrustedDevice> devices,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = path + ".tmp";
        await using (FileStream stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                devices,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, true);
    }
}
