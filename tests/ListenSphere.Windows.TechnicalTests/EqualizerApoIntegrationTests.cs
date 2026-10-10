using System.IO;
using ListenSphere.Controller.Services;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class EqualizerApoIntegrationTests
{
    [Fact]
    public async Task ApplyAsync_PreservesUserConfigurationAndWritesManagedFilters()
    {
        using var fixture = new EqualizerApoFixture();
        await File.WriteAllTextAsync(
            fixture.MainConfigPath,
            "# user configuration\r\nPreamp: -1 dB\r\n",
            TestContext.Current.CancellationToken);
        var integration = new EqualizerApoIntegration(fixture.Root);

        await integration.ApplyAsync(
            true,
            [5, 4, 3, 2, 0, -1, -1, 0, 1, 1],
            TestContext.Current.CancellationToken);
        await integration.ApplyAsync(
            true,
            [5, 4, 3, 2, 0, -1, -1, 0, 1, 1],
            TestContext.Current.CancellationToken);

        string main = await File.ReadAllTextAsync(
            fixture.MainConfigPath,
            TestContext.Current.CancellationToken);
        Assert.Contains("# user configuration", main);
        Assert.Equal(1, CountOccurrences(main, "Include: ListenSphere.txt"));
        string managed = await File.ReadAllTextAsync(
            fixture.ManagedConfigPath,
            TestContext.Current.CancellationToken);
        Assert.Contains("Preamp: -5.0 dB", managed);
        Assert.Contains("Filter 1: ON PK Fc 20 Hz Gain 5.0 dB Q 1.35", managed);
        Assert.Contains("Filter 10: ON PK Fc 20000 Hz Gain 1.0 dB Q 1.35", managed);
        Assert.True(integration.Snapshot.IsSynchronized);
    }

    [Fact]
    public async Task ApplyAsync_WhenBypassed_RemovesManagedFiltersOnly()
    {
        using var fixture = new EqualizerApoFixture();
        var integration = new EqualizerApoIntegration(fixture.Root);

        await integration.ApplyAsync(
            false,
            new float[10],
            TestContext.Current.CancellationToken);

        string managed = await File.ReadAllTextAsync(
            fixture.ManagedConfigPath,
            TestContext.Current.CancellationToken);
        Assert.Contains("Equalizer bypassed", managed);
        Assert.DoesNotContain("Filter 1:", managed);
        Assert.True(integration.Snapshot.IsSynchronized);
    }

    [Fact]
    public async Task ApplyAsync_WhenNotInstalled_ReportsUnavailableWithoutWriting()
    {
        string missing = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"));
        var integration = new EqualizerApoIntegration(missing);

        await integration.ApplyAsync(
            true,
            new float[10],
            TestContext.Current.CancellationToken);

        Assert.False(integration.Snapshot.IsInstalled);
        Assert.False(integration.Snapshot.IsSynchronized);
        Assert.Contains("未检测到", integration.Snapshot.StatusText);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task ApplyAsync_WhenManagedConfigurationIsTemporarilyLocked_RetriesAndSucceeds()
    {
        using var fixture = new EqualizerApoFixture();
        var integration = new EqualizerApoIntegration(fixture.Root);
        await integration.ApplyAsync(
            true,
            new float[10],
            TestContext.Current.CancellationToken);

        Task applyTask;
        using (var lockedFile = new FileStream(
            fixture.ManagedConfigPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            applyTask = integration.ApplyAsync(
                true,
                [3, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        await applyTask;

        string managed = await File.ReadAllTextAsync(
            fixture.ManagedConfigPath,
            TestContext.Current.CancellationToken);
        Assert.Contains("Filter 1: ON PK Fc 20 Hz Gain 3.0 dB Q 1.35", managed);
        Assert.True(integration.Snapshot.IsSynchronized);
    }

    private static int CountOccurrences(string value, string pattern) =>
        (value.Length - value.Replace(pattern, string.Empty, StringComparison.Ordinal).Length) /
        pattern.Length;

    private sealed class EqualizerApoFixture : IDisposable
    {
        public EqualizerApoFixture()
        {
            Root = Path.Combine(
                AppContext.BaseDirectory,
                "equalizer-apo-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllBytes(Path.Combine(Root, "DeviceSelector.exe"), []);
        }

        public string Root { get; }
        public string ConfigDirectory => Path.Combine(Root, "config");
        public string MainConfigPath => Path.Combine(ConfigDirectory, "config.txt");
        public string ManagedConfigPath => Path.Combine(ConfigDirectory, "ListenSphere.txt");

        public void Dispose() => Directory.Delete(Root, true);
    }
}
