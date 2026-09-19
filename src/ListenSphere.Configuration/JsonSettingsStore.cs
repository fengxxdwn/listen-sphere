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

    private readonly SettingsMigrationPipeline migrations = new();
    public string? CompatibilityWarning { get; private set; }

    public string? LastRecoveryPath { get; private set; }

    public async ValueTask<ListenSphereSettings> LoadAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastRecoveryPath = null;
            CompatibilityWarning = null;
            if (!File.Exists(path))
            {
                return new ListenSphereSettings();
            }

            try
            {
                ListenSphereSettings settings = await ReadExistingAsync(cancellationToken)
                    .ConfigureAwait(false);
                LastRecoveryPath = null;
                return Prepare(settings);
            }
            catch (UnsupportedSettingsVersionException exception)
            {
                CompatibilityWarning = exception.Message;
                return new ListenSphereSettings();
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
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("设置文件必须是 JSON 对象。");
        int version = 0;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "version", StringComparison.OrdinalIgnoreCase))
                {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out version))
                    throw new JsonException("设置版本必须是整数。");
            }
        }
        // Inspect the envelope before decoding fields: future schemas may change types.
        if (version < SettingsMigrationPipeline.MinimumVersion ||
            version > ListenSphereSettings.CurrentVersion)
            throw new UnsupportedSettingsVersionException(version);
        return document.RootElement.Deserialize<ListenSphereSettings>(
            SerializerOptions) ?? throw new InvalidDataException("设置文件内容为空。");
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
            if (CompatibilityWarning is not null)
                return;
            // Protect unsupported files even if Save is called before Load.
            if (File.Exists(path))
            {
                try { migrations.Migrate(await ReadExistingAsync(cancellationToken).ConfigureAwait(false)); }
                catch (UnsupportedSettingsVersionException exception)
                {
                    CompatibilityWarning = exception.Message;
                    return;
                }
            }
            ListenSphereSettings normalized = Prepare(settings);
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

    private ListenSphereSettings Prepare(ListenSphereSettings settings)
    {
        ListenSphereSettings normalized = Normalize(migrations.Migrate(settings));
        // Validate every numeric field before writing/replacing any file.
        try { JsonSerializer.SerializeToUtf8Bytes(normalized, SerializerOptions); }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("设置包含非法数值。", exception);
        }
        return normalized;
    }

    private static ListenSphereSettings Normalize(ListenSphereSettings settings) => settings with
    {
        MasterVolume = Math.Clamp(settings.MasterVolume, 0f, 1f),
        LocalSourceVolume = Math.Clamp(settings.LocalSourceVolume, 0f, 1f),
        Scenes = (settings.Scenes ?? [])
            .Where(scene => scene is not null && scene.SceneId != Guid.Empty && !string.IsNullOrWhiteSpace(scene.Name))
            .Select(scene => scene with
            {
                Name = scene.Name.Trim(),
                MasterVolume = Math.Clamp(scene.MasterVolume, 0f, 1f),
                Channels = (scene.Channels ?? [])
                    .Where(channel => channel is not null && channel.ChannelId != Guid.Empty)
                    .Select(channel => channel with
                    {
                        DisplayName = channel.DisplayName?.Trim() ?? "未命名声道",
                        Volume = Math.Clamp(channel.Volume, 0f, 1f),
                        EqualizerPreset = string.IsNullOrWhiteSpace(channel.EqualizerPreset)
                            ? "原声"
                            : channel.EqualizerPreset.Trim(),
                        EqualizerGains = channel.EqualizerGains is { Length: 10 }
                            ? channel.EqualizerGains
                                .Select(gain => Math.Clamp(gain, -20f, 20f))
                                .ToArray()
                            : null,
                        ChannelGroup = string.IsNullOrWhiteSpace(channel.ChannelGroup)
                            ? "未分组"
                            : channel.ChannelGroup.Trim(),
                        PreampDb = Math.Clamp(channel.PreampDb, -24f, 12f),
                        NoiseGateThresholdDb = Math.Clamp(channel.NoiseGateThresholdDb, -80f, -10f),
                        CompressorThresholdDb = Math.Clamp(channel.CompressorThresholdDb, -40f, 0f),
                        CompressorRatio = Math.Clamp(channel.CompressorRatio, 1f, 20f),
                        LimiterCeilingDb = Math.Clamp(channel.LimiterCeilingDb, -12f, -0.1f),
                        VoiceDuckingReductionDb = Math.Clamp(channel.VoiceDuckingReductionDb, 0f, 30f)
                    })
                    .ToArray(),
                GroupBuses = (scene.GroupBuses ?? [])
                    .Where(group => group is not null && !string.IsNullOrWhiteSpace(group.Name))
                    .Select(group => group with
                    {
                        Name = group.Name.Trim(),
                        Volume = Math.Clamp(group.Volume, 0f, 1f),
                        EqualizerPreset = string.IsNullOrWhiteSpace(group.EqualizerPreset)
                            ? "原声"
                            : group.EqualizerPreset.Trim()
                    })
                    .ToArray()
            })
            .ToArray(),
        PairedDeviceIds = settings.PairedDeviceIds ?? [],
        SenderSelectedApplicationKeys = (settings.SenderSelectedApplicationKeys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(128)
            .ToArray(),
        SenderCaptureMode = string.Equals(
            settings.SenderCaptureMode,
            "system",
            StringComparison.OrdinalIgnoreCase)
                ? "system"
                : "applications",
        SenderCaptureDeviceId = string.IsNullOrWhiteSpace(settings.SenderCaptureDeviceId)
            ? null
            : settings.SenderCaptureDeviceId.Trim(),
        MicrophoneOutputDeviceId = string.IsNullOrWhiteSpace(settings.MicrophoneOutputDeviceId)
            ? null
            : settings.MicrophoneOutputDeviceId.Trim(),
        ComputerMicrophoneDeviceId = string.IsNullOrWhiteSpace(settings.ComputerMicrophoneDeviceId)
            ? null
            : settings.ComputerMicrophoneDeviceId.Trim(),
        MicrophoneMonitoringDeviceId = string.IsNullOrWhiteSpace(settings.MicrophoneMonitoringDeviceId)
            ? null
            : settings.MicrophoneMonitoringDeviceId.Trim(),
        MicrophoneOutputVolume = Math.Clamp(settings.MicrophoneOutputVolume, 0f, 1f),
        AudioRoutingRules = (settings.AudioRoutingRules ?? [])
            .Where(rule =>
                rule is not null && rule.RuleId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(rule.Name) &&
                !string.IsNullOrWhiteSpace(rule.SourcePattern) &&
                !string.IsNullOrWhiteSpace(rule.TargetGroup))
            .GroupBy(rule => rule.RuleId)
            .Select(group => group.First())
            .Select(rule => rule with
            {
                Name = rule.Name.Trim(),
                SourcePattern = rule.SourcePattern.Trim(),
                TargetGroup = rule.TargetGroup.Trim(),
                SourceKind = string.IsNullOrWhiteSpace(rule.SourceKind)
                    ? null
                    : rule.SourceKind!.Trim(),
                Transport = string.IsNullOrWhiteSpace(rule.Transport)
                    ? null
                    : rule.Transport!.Trim(),
                Priority = Math.Clamp(rule.Priority, 0, 1000)
            })
            .OrderByDescending(rule => rule.Priority)
            .Take(128)
            .ToArray(),
        ChannelLayouts = (settings.ChannelLayouts ?? [])
            .Where(layout => layout is not null && layout.ChannelId != Guid.Empty)
            .GroupBy(layout => layout.ChannelId)
            .Select(group => group.First() with
            {
                SortOrder = Math.Clamp(group.First().SortOrder, 0, 10_000)
            })
            .OrderByDescending(layout => layout.IsPinned)
            .ThenBy(layout => layout.SortOrder)
            .Take(256)
            .ToArray(),
        AudioOutputRoutes = (settings.AudioOutputRoutes ?? [])
            .Where(route => route is not null && route.ChannelId != Guid.Empty && !string.IsNullOrWhiteSpace(route.DeviceId))
            .Select(route => route with
            {
                DeviceId = route.DeviceId.Trim(),
                ChannelName = string.IsNullOrWhiteSpace(route.ChannelName)
                    ? null
                    : route.ChannelName.Trim()
            })
            .Distinct()
            .Take(512)
            .ToArray()
    };
}
