using System.Text.Json;

namespace ListenSphere.Configuration;

/// <summary>Persists versioned settings as UTF-8 JSON using same-volume atomic replacement.</summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public string? LastRecoveryPath { get; private set; }

    public async ValueTask<ListenSphereSettings> LoadAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return new ListenSphereSettings();
            }

            try
            {
                ListenSphereSettings settings = await ReadExistingAsync(cancellationToken)
                    .ConfigureAwait(false);
                LastRecoveryPath = null;
                return Normalize(settings);
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or NotSupportedException)
            {
                string recoveryPath =
                    $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.json";
                File.Move(path, recoveryPath);
                LastRecoveryPath = recoveryPath;
                return new ListenSphereSettings();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ListenSphereSettings> ReadExistingAsync(
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ListenSphereSettings settings =
            await JsonSerializer.DeserializeAsync<ListenSphereSettings>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("设置文件内容为空。");
        if (settings.Version <= 0 || settings.Version > ListenSphereSettings.CurrentVersion)
        {
            throw new InvalidDataException($"不支持的设置版本：{settings.Version}。");
        }

        return settings;
    }

    public async ValueTask SaveAsync(
        ListenSphereSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            ListenSphereSettings normalized = Normalize(settings) with
            {
                Version = ListenSphereSettings.CurrentVersion
            };
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    normalized,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            gate.Release();
        }
    }

    private static ListenSphereSettings Normalize(ListenSphereSettings settings) => settings with
    {
        MasterVolume = Math.Clamp(settings.MasterVolume, 0f, 1f),
        Scenes = (settings.Scenes ?? [])
            .Where(scene => scene.SceneId != Guid.Empty && !string.IsNullOrWhiteSpace(scene.Name))
            .Select(scene => scene with
            {
                Name = scene.Name.Trim(),
                MasterVolume = Math.Clamp(scene.MasterVolume, 0f, 1f),
                Channels = (scene.Channels ?? [])
                    .Where(channel => channel.ChannelId != Guid.Empty)
                    .Select(channel => channel with
                    {
                        DisplayName = channel.DisplayName.Trim(),
                        Volume = Math.Clamp(channel.Volume, 0f, 1f)
                    })
                    .ToArray()
            })
            .ToArray(),
        PairedDeviceIds = settings.PairedDeviceIds ?? []
    };
}
