namespace ListenSphere.Configuration;

public sealed class UnsupportedSettingsVersionException(int version) : Exception(
    $"设置版本 {version} 不兼容（支持 v10–v15）；本次使用默认设置，原文件保留且不会自动保存。")
{
    public int Version { get; } = version;
}

public sealed class SettingsMigrationPipeline
{
    public const int MinimumVersion = 10;
    public IReadOnlyList<ISettingsMigration> Steps { get; } =
        Array.AsReadOnly<ISettingsMigration>([
            new VersionStep(10), new VersionStep(11), new VersionStep(12),
            new VersionStep(13), new VersionStep(14)]);

    public ListenSphereSettings Migrate(ListenSphereSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Version < MinimumVersion || settings.Version > ListenSphereSettings.CurrentVersion)
            throw new UnsupportedSettingsVersionException(settings.Version);
        foreach (ISettingsMigration step in Steps)
            if (settings.Version == step.SourceVersion)
                settings = step.Migrate(settings);
        return settings;
    }

    private sealed class VersionStep(int sourceVersion) : ISettingsMigration
    {
        public int SourceVersion => sourceVersion;
        public int TargetVersion => sourceVersion + 1;

        public ListenSphereSettings Migrate(ListenSphereSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            if (settings.Version != SourceVersion)
                throw new ArgumentException("Migration source version does not match.", nameof(settings));
            // v11 introduced explicit ducking roles. Later additive fields receive
            // historical defaults from the existing configuration contract initializers.
            return settings with
            {
                Version = TargetVersion,
                Scenes = SourceVersion == 10
                    ? (settings.Scenes ?? []).Where(scene => scene is not null).Select(scene => scene with
                    {
                        Channels = (scene.Channels ?? []).Where(channel => channel is not null)
                            .Select(channel => channel with
                            {
                                IsVoiceDuckingTrigger = channel.ChannelGroup?.Trim() == "语音",
                                IsVoiceDuckingTarget = channel.ChannelGroup?.Trim() is "游戏" or "媒体"
                            }).ToArray()
                    }).ToArray()
                    : settings.Scenes
            };
        }
    }
}
