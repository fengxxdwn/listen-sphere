using System.IO;
using ListenSphere.Configuration;
using ListenSphere.Controller.Presentation;
using ListenSphere.Controller.Services;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class R4ControllerTests
{
    [Fact]
    public async Task SettingsCoordinator_DebouncesAndPersistsLatestSnapshot()
    {
        var store = new FakeSettingsStore();
        await using var coordinator = new ControllerSettingsCoordinator(store);
        float desiredVolume = 0.2f;
        coordinator.ConfigureSnapshotFactory(current => current with
        {
            MasterVolume = desiredVolume
        });

        coordinator.QueueSave(TimeSpan.FromMilliseconds(80));
        desiredVolume = 0.75f;
        coordinator.QueueSave(TimeSpan.FromMilliseconds(80));

        ListenSphereSettings saved = await store.Saved.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(0.75f, saved.MasterVolume);
        Assert.Equal(ListenSphereSettings.CurrentVersion, saved.Version);
    }

    [Fact]
    public async Task SettingsCoordinator_DisposeCancelsAndDrainsQueuedSave()
    {
        var store = new FakeSettingsStore();
        var coordinator = new ControllerSettingsCoordinator(store);
        coordinator.ConfigureSnapshotFactory(current => current with
        {
            MasterVolume = 0.3f
        });
        coordinator.QueueSave(TimeSpan.FromSeconds(10));

        await coordinator.DisposeAsync();

        Assert.Equal(0, store.SaveCount);
        Assert.Throws<ObjectDisposedException>(() => coordinator.QueueSave());
    }
    [Fact]
    public async Task SettingsCoordinator_LoadsAndReportsRecoveryState()
    {
        var expected = new ListenSphereSettings { MasterVolume = 0.42f };
        var store = new FakeSettingsStore(expected);
        await using var coordinator = new ControllerSettingsCoordinator(store);

        ListenSphereSettings loaded = await coordinator.LoadAsync(
            TestContext.Current.CancellationToken);

        Assert.Same(expected, loaded);
        Assert.Same(expected, coordinator.Current);
    }

    [Fact]
    public void SceneService_NormalizesImportedSceneWithoutChangingWireContracts()
    {
        var service = new SceneService();
        var existing = new[]
        {
            new SceneSettings(Guid.NewGuid(), "游戏", [])
        };
        var imported = new SceneSettings(
            Guid.Empty,
            "游戏",
            [new ChannelSettings(
                Guid.NewGuid(),
                "  ",
                2f,
                false,
                EqualizerGains: Enumerable.Repeat(30f, 10).ToArray(),
                PreampDb: 30f)],
            MasterVolume: -1f);

        SceneSettings normalized = service.NormalizeImported(imported, existing);

        Assert.NotEqual(Guid.Empty, normalized.SceneId);
        Assert.Equal("游戏 2", normalized.Name);
        Assert.Equal(0f, normalized.MasterVolume);
        Assert.Equal("未命名声道", normalized.Channels[0].DisplayName);
        Assert.Equal(1f, normalized.Channels[0].Volume);
        Assert.Equal(12f, normalized.Channels[0].PreampDb);
        Assert.All(normalized.Channels[0].EqualizerGains!, gain => Assert.Equal(20f, gain));
    }

    [Fact]
    public async Task SceneSerializationService_RoundTripsExistingFormat()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"listen-sphere-r4-{Guid.NewGuid():N}.json");
        var service = new SceneSerializationService();
        var expected = new SceneSettings(
            Guid.NewGuid(),
            "会议",
            [new ChannelSettings(Guid.NewGuid(), "麦克风", 0.6f, false)],
            "device-id",
            0.8f,
            true,
            [new GroupBusSettings("语音")]);
        try
        {
            await service.WriteAsync(path, expected, TestContext.Current.CancellationToken);
            SceneSettings actual = await service.ReadAsync(
                path,
                TestContext.Current.CancellationToken);

            Assert.Equal(expected.SceneId, actual.SceneId);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.PlaybackDeviceId, actual.PlaybackDeviceId);
            Assert.Equal(expected.Channels[0], actual.Channels[0]);
            Assert.Equal(expected.GroupBuses![0], actual.GroupBuses![0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FirstRunViewModel_FinishCommandPersistsCompletion()
    {
        var persisted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new FirstRunViewModel(() =>
        {
            persisted.SetResult();
            return Task.FromResult(true);
        });
        model.Initialize(completed: false);

        model.FinishCommand.Execute(null);
        await persisted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.False(model.IsVisible);
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private readonly ListenSphereSettings loaded;

        public FakeSettingsStore(ListenSphereSettings? loaded = null) =>
            this.loaded = loaded ?? new ListenSphereSettings();

        public int SaveCount { get; private set; }
        public TaskCompletionSource<ListenSphereSettings> Saved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ListenSphereSettings> LoadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(loaded);

        public ValueTask SaveAsync(
            ListenSphereSettings settings,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            Saved.TrySetResult(settings);
            return ValueTask.CompletedTask;
        }
    }
}