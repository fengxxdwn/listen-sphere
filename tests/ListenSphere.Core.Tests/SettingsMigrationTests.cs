using System.Text.Json;
using ListenSphere.Configuration;
using Xunit;

namespace ListenSphere.Core.Tests;

public sealed class SettingsMigrationTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void Pipeline_UpgradesAndIsIdempotent(int version)
    {
        var pipeline = new SettingsMigrationPipeline();
        var original = new ListenSphereSettings { Version = version, MasterVolume = 0.37f };
        var migrated = pipeline.Migrate(original);
        Assert.Equal(15, migrated.Version);
        Assert.Equal(version, original.Version);
        Assert.Equal(0.37f, migrated.MasterVolume);
        Assert.Same(migrated, pipeline.Migrate(migrated));
    }

    [Fact]
    public void Steps_AreContinuousAndPreserveExplicitValues()
    {
        var pipeline = new SettingsMigrationPipeline();
        foreach (var step in pipeline.Steps)
        {
            var original = new ListenSphereSettings
            {
                Version = step.SourceVersion,
                MicrophoneOutputEnabled = true,
                LocalSourceVolume = 0.26f,
                ComputerMicrophoneDeviceId = "mic"
            };
            var result = step.Migrate(original);
            Assert.Equal(step.SourceVersion + 1, step.TargetVersion);
            Assert.Equal(step.TargetVersion, result.Version);
            Assert.Equal(0.26f, result.LocalSourceVolume);
            Assert.True(result.MicrophoneOutputEnabled);
            Assert.Equal("mic", result.ComputerMicrophoneDeviceId);
            Assert.Throws<ArgumentException>(() => step.Migrate(result));
        }
        Assert.Equal([10, 11, 12, 13, 14], pipeline.Steps.Select(s => s.SourceVersion));
    }

    [Fact]
    public void V10_ConvertsDuckingRolesWithoutMutatingInput()
    {
        var channels = new[] { "语音", "游戏", "媒体", "未分组" }
            .Select(group => new ChannelSettings(Guid.NewGuid(), group, 0.5f, false, ChannelGroup: group)).ToArray();
        var input = new ListenSphereSettings
        {
            Version = 10,
            Scenes = [new SceneSettings(Guid.NewGuid(), "旧场景", channels)]
        };
        var migrated = new SettingsMigrationPipeline().Migrate(input).Scenes[0].Channels;
        Assert.True(migrated[0].IsVoiceDuckingTrigger);
        Assert.False(migrated[0].IsVoiceDuckingTarget);
        Assert.True(migrated[1].IsVoiceDuckingTarget);
        Assert.True(migrated[2].IsVoiceDuckingTarget);
        Assert.False(migrated[3].IsVoiceDuckingTarget);
        Assert.False(channels[0].IsVoiceDuckingTrigger);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(999)]
    public async Task UnsupportedFiles_AreNeverRenamedOrOverwritten(int version)
    {
        using var fixture = new TempSettings();
        string json = $$"""{"version":{{version}},"masterVolume":0.37,"unknown":"preserve me"}""";
        await File.WriteAllTextAsync(fixture.Path, json, TestContext.Current.CancellationToken);
        var store = new JsonSettingsStore(fixture.Path);
        var settings = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1f, settings.MasterVolume);
        Assert.NotNull(store.CompatibilityWarning);
        Assert.Null(store.LastRecoveryPath);
        await store.SaveAsync(settings with { MasterVolume = 0.2f }, TestContext.Current.CancellationToken);
        Assert.Equal(json, await File.ReadAllTextAsync(fixture.Path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(fixture.Directory));
        // Independent writer cannot overwrite an unsupported file before Load either.
        await new JsonSettingsStore(fixture.Path).SaveAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal(json, await File.ReadAllTextAsync(fixture.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingVersion_IsProtected()
    {
        using var fixture = new TempSettings();
        await File.WriteAllTextAsync(fixture.Path, "{}", TestContext.Current.CancellationToken);
        var store = new JsonSettingsStore(fixture.Path);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(store.CompatibilityWarning);
        await store.SaveAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal("{}", await File.ReadAllTextAsync(fixture.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_MigratesThenNormalizes_WithoutWritingSource()
    {
        using var fixture = new TempSettings();
        string json = """{"version":10,"masterVolume":4,"localSourceVolume":-1,"scenes":null}""";
        await File.WriteAllTextAsync(fixture.Path, json, TestContext.Current.CancellationToken);
        var store = new JsonSettingsStore(fixture.Path);
        var result = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(15, result.Version);
        Assert.Equal(1, result.MasterVolume);
        Assert.Equal(0, result.LocalSourceVolume);
        Assert.Empty(result.Scenes);
        Assert.Equal(json, await File.ReadAllTextAsync(fixture.Path, TestContext.Current.CancellationToken));
        await store.SaveAsync(result, TestContext.Current.CancellationToken);
        Assert.Equal(15, (await store.LoadAsync(TestContext.Current.CancellationToken)).Version);
        Assert.Single(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public async Task InvalidOrCancelledSave_PreservesPreviousFile()
    {
        using var fixture = new TempSettings();
        var store = new JsonSettingsStore(fixture.Path);
        await store.SaveAsync(new() { MasterVolume = 0.42f }, TestContext.Current.CancellationToken);
        string before = await File.ReadAllTextAsync(fixture.Path, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.SaveAsync(new() { MasterVolume = float.NaN }, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.SaveAsync(new(), cancellation.Token));
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.Path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public async Task CorruptFile_IsBackedUp_AndDefaultsCanBeSaved()
    {
        using var fixture = new TempSettings();
        await File.WriteAllTextAsync(fixture.Path, "{broken", TestContext.Current.CancellationToken);
        var store = new JsonSettingsStore(fixture.Path);
        var result = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Null(store.CompatibilityWarning);
        Assert.NotNull(store.LastRecoveryPath);
        Assert.Equal("{broken", await File.ReadAllTextAsync(store.LastRecoveryPath, TestContext.Current.CancellationToken));
        await store.SaveAsync(result, TestContext.Current.CancellationToken);
        Assert.Equal(15, (await store.LoadAsync(TestContext.Current.CancellationToken)).Version);
    }

    [Fact]
    public async Task FutureSchemaWithChangedFieldTypes_IsPreserved()
    {
        using var fixture = new TempSettings();
        string json = """{"Version":16,"masterVolume":{"newFormat":true},"scenes":"new-schema"}""";
        await File.WriteAllTextAsync(fixture.Path, json, TestContext.Current.CancellationToken);
        var store = new JsonSettingsStore(fixture.Path);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(store.CompatibilityWarning);
        Assert.Null(store.LastRecoveryPath);
        await store.SaveAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal(json, await File.ReadAllTextAsync(fixture.Path, TestContext.Current.CancellationToken));
    }

    private sealed class TempSettings : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ListenSphere-R5-" + Guid.NewGuid());
        public string Path => System.IO.Path.Combine(Directory, "settings.json");
        public TempSettings() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
