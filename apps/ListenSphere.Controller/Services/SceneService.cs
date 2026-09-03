using System.IO;
using System.Security.Cryptography;
using System.Text;
using ListenSphere.Configuration;
using ListenSphere.Controller.Presentation;

namespace ListenSphere.Controller.Services;

public interface ISceneService
{
    IReadOnlyList<ChannelSettings> CaptureChannels(
        LocalSessionsViewModel localSessions,
        ControllerNetworkViewModel network);
    SceneSettings Save(
        IList<SceneSettings> scenes,
        string name,
        LocalSessionsViewModel localSessions,
        ControllerNetworkViewModel network);
    Task ApplyAsync(
        SceneSettings scene,
        LocalSessionsViewModel localSessions,
        ControllerNetworkViewModel network);
    void Delete(IList<SceneSettings> scenes, SceneSettings scene);
    SceneSettings? Rename(
        IList<SceneSettings> scenes,
        SceneSettings scene,
        string name);
    SceneSettings Duplicate(
        IList<SceneSettings> scenes,
        SceneSettings scene,
        string baseName);
    SceneSettings NormalizeImported(
        SceneSettings scene,
        IReadOnlyCollection<SceneSettings> existingScenes);
    string SanitizeFileName(string name);
}

public sealed class SceneService : ISceneService
{
    public IReadOnlyList<ChannelSettings> CaptureChannels(
        LocalSessionsViewModel localSessions,
        ControllerNetworkViewModel network)
    {
        IEnumerable<ChannelSettings> local = localSessions.CaptureChannels();
        IEnumerable<ChannelSettings> remote = network.RemoteChannels.Select(channel =>
            new ChannelSettings(
                channel.ChannelId,
                channel.DisplayName,
                channel.VolumePercent / 100,
                channel.IsMuted,
                channel.SelectedEqualizerPreset.Name,
                channel.EqualizerGains.ToArray(),
                channel.IsEqualizerEnabled,
                channel.SelectedChannelGroup,
                channel.PreampDb,
                channel.NoiseGateEnabled,
                channel.NoiseGateThresholdDb,
                channel.CompressorEnabled,
                channel.CompressorThresholdDb,
                channel.CompressorRatio,
                channel.LimiterEnabled,
                channel.LimiterCeilingDb,
                channel.IsVoiceDuckingTrigger,
                channel.IsVoiceDuckingTarget,
                channel.VoiceDuckingReductionDb));
        return local.Concat(remote).ToArray();
    }

    public SceneSettings Save(
        IList<SceneSettings> scenes,
        string name,
        LocalSessionsViewModel localSessions,
        ControllerNetworkViewModel network)
    {
        SceneSettings? existing = scenes.FirstOrDefault(scene =>
            string.Equals(scene.Name, name, StringComparison.CurrentCultureIgnoreCase));
        var saved = new SceneSettings(
            existing?.SceneId ?? Guid.NewGuid(),
            name,
            CaptureChannels(localSessions, network),
            network.SelectedPlaybackDevice?.Id,
            network.MasterVolumePercent / 100,
            network.FollowSystemDefaultPlayback,
            network.CaptureGroupBusSettings());
        if (existing is null)
        {
            scenes.Add(saved);
        }
        else
        {
            scenes[scenes.IndexOf(existing)] = saved;
        }
        return saved;
    }

    public async Task ApplyAsync(
        SceneSettings scene,
        LocalSessionsViewModel localSessions,
        ControllerNetworkViewModel network)
    {
        foreach (AudioSessionItemViewModel session in localSessions.Sessions)
        {
            ChannelSettings? saved = scene.Channels.FirstOrDefault(channel =>
                channel.ChannelId == session.RoutingChannelId) ??
                scene.Channels.FirstOrDefault(channel =>
                    channel.ChannelId == CreateLocalChannelId(session.DisplayName));
            if (saved is not null)
            {
                session.VolumePercent = saved.Volume * 100;
                session.IsMuted = saved.IsMuted;
            }
        }

        await network.ApplyAudioSettingsAsync(
            scene.PlaybackDeviceId,
            scene.MasterVolume,
            scene.FollowSystemDefaultPlayback,
            scene.Channels,
            scene.GroupBuses ?? []);
    }

    public void Delete(IList<SceneSettings> scenes, SceneSettings scene) =>
        scenes.Remove(scene);

    public SceneSettings? Rename(
        IList<SceneSettings> scenes,
        SceneSettings scene,
        string name)
    {
        if (scenes.Any(item => item.SceneId != scene.SceneId &&
            string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return null;
        }

        SceneSettings renamed = scene with { Name = name };
        scenes[scenes.IndexOf(scene)] = renamed;
        return renamed;
    }

    public SceneSettings Duplicate(
        IList<SceneSettings> scenes,
        SceneSettings scene,
        string baseName)
    {
        string name = CreateUniqueSceneName(baseName, scenes);
        SceneSettings copy = scene with { SceneId = Guid.NewGuid(), Name = name };
        scenes.Add(copy);
        return copy;
    }

    public SceneSettings NormalizeImported(
        SceneSettings scene,
        IReadOnlyCollection<SceneSettings> existingScenes)
    {
        if (string.IsNullOrWhiteSpace(scene.Name))
        {
            throw new InvalidDataException("预设缺少名称。");
        }

        ChannelSettings[] channels = (scene.Channels ?? [])
            .Where(channel => channel.ChannelId != Guid.Empty)
            .Take(256)
            .Select(channel => channel with
            {
                DisplayName = string.IsNullOrWhiteSpace(channel.DisplayName)
                    ? "未命名声道"
                    : channel.DisplayName.Trim(),
                Volume = Math.Clamp(channel.Volume, 0f, 1f),
                PreampDb = Math.Clamp(channel.PreampDb, -24f, 12f),
                NoiseGateThresholdDb = Math.Clamp(channel.NoiseGateThresholdDb, -80f, -10f),
                CompressorThresholdDb = Math.Clamp(channel.CompressorThresholdDb, -40f, 0f),
                CompressorRatio = Math.Clamp(channel.CompressorRatio, 1f, 20f),
                LimiterCeilingDb = Math.Clamp(channel.LimiterCeilingDb, -12f, -0.1f),
                VoiceDuckingReductionDb = Math.Clamp(channel.VoiceDuckingReductionDb, 0f, 30f),
                EqualizerGains = channel.EqualizerGains is { Length: 10 }
                    ? channel.EqualizerGains
                        .Select(value => Math.Clamp(value, -20f, 20f))
                        .ToArray()
                    : null
            })
            .ToArray();
        return scene with
        {
            SceneId = Guid.NewGuid(),
            Name = CreateUniqueSceneName(scene.Name.Trim(), existingScenes),
            Channels = channels,
            MasterVolume = Math.Clamp(scene.MasterVolume, 0f, 1f)
        };
    }

    public string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(name.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized)
            ? "ListenSphere-Preset"
            : sanitized;
    }

    private static string CreateUniqueSceneName(
        string baseName,
        IEnumerable<SceneSettings> scenes)
    {
        string candidate = baseName;
        for (var suffix = 2; scenes.Any(scene => string.Equals(
                 scene.Name,
                 candidate,
                 StringComparison.CurrentCultureIgnoreCase)); suffix++)
        {
            candidate = $"{baseName} {suffix}";
        }
        return candidate;
    }

    private static Guid CreateLocalChannelId(string displayName)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"ListenSphere/local/{displayName.Trim().ToUpperInvariant()}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}