using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using ListenSphere.Configuration;
using ListenSphere.Diagnostics;
using System.IO.Compression;
using Xunit;

namespace ListenSphere.Core.Tests;

public sealed class CoreContractTests
{
    [Fact]
    public void DefaultAudioFormat_IsFortyEightKilohertzStereoFloat()
    {
        var format = AudioFormat.Default;

        Assert.Equal(48_000, format.SampleRate);
        Assert.Equal(2, format.ChannelCount);
        Assert.Equal(AudioSampleFormat.Float32LittleEndian, format.SampleFormat);
        Assert.Equal(TimeSpan.FromMilliseconds(10), format.GetDuration(480));
    }

    [Fact]
    public void P0MixingDefaults_UseTenMillisecondFrames()
    {
        var options = new MixingOptions();

        Assert.Equal(10, options.FrameDurationMilliseconds);
        Assert.Equal(AudioFormat.Default, options.Format);
    }

    [Fact]
    public void Settings_StartAtCurrentVersion()
    {
        var settings = new ListenSphereSettings();

        Assert.Equal(ListenSphereSettings.CurrentVersion, settings.Version);
        Assert.Empty(settings.Scenes);
        Assert.Empty(settings.PairedDeviceIds);
    }

    [Fact]
    public void PcmGainProcessor_AppliesGainMuteAndValidatesAlignment()
    {
        float[] halfSamples = [1f, -0.5f, 0.25f];
        PcmGainProcessor.Apply(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(halfSamples.AsSpan()),
            0.5f);
        Assert.Equal([0.5f, -0.25f, 0.125f], halfSamples);

        float[] mutedSamples = [0.8f, -0.4f];
        PcmGainProcessor.Apply(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(mutedSamples.AsSpan()),
            0f);
        Assert.Equal([0f, 0f], mutedSamples);
        Assert.Throws<ArgumentException>(() => PcmGainProcessor.Apply(new byte[3], 1f));
    }

    [Fact]
    public async Task JsonSettingsStore_RoundTripsVersionedSceneAtomically()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "ListenSphere.Tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        var store = new JsonSettingsStore(path);
        Guid channelId = Guid.NewGuid();
        var expected = new ListenSphereSettings
        {
            PlaybackDeviceId = "endpoint-1",
            FollowSystemDefaultPlayback = false,
            MasterVolume = 0.65f,
            FirstRunCompleted = true,
            Scenes =
            [
                new SceneSettings(
                    Guid.NewGuid(),
                    "游戏",
                    [new ChannelSettings(channelId, "副电脑", 0.4f, true)],
                    "endpoint-1",
                    0.65f)
            ]
        };

        try
        {
            await store.SaveAsync(expected, TestContext.Current.CancellationToken);
            ListenSphereSettings actual =
                await store.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ListenSphereSettings.CurrentVersion, actual.Version);
            Assert.Equal(expected.PlaybackDeviceId, actual.PlaybackDeviceId);
            Assert.False(actual.FollowSystemDefaultPlayback);
            Assert.Equal(expected.MasterVolume, actual.MasterVolume);
            Assert.True(actual.FirstRunCompleted);
            SceneSettings scene = Assert.Single(actual.Scenes);
            Assert.Equal("游戏", scene.Name);
            Assert.Equal(channelId, Assert.Single(scene.Channels).ChannelId);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task JsonSettingsStore_BacksUpCorruptJsonAndReturnsDefaults()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "ListenSphere.Tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            path,
            "{ definitely-not-json",
            TestContext.Current.CancellationToken);
        var store = new JsonSettingsStore(path);

        try
        {
            ListenSphereSettings settings =
                await store.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ListenSphereSettings.CurrentVersion, settings.Version);
            Assert.NotNull(store.LastRecoveryPath);
            Assert.True(File.Exists(store.LastRecoveryPath));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AdaptivePlaybackController_BoundsLatencyAndEstimatesDrift()
    {
        var controller = new AdaptivePlaybackController();

        Assert.Equal(BufferCorrection.RecordUnderrun, controller.EvaluateBuffer(0));
        Assert.Equal(BufferCorrection.None, controller.EvaluateBuffer(60));
        Assert.Equal(BufferCorrection.DropFrame, controller.EvaluateBuffer(101));
        Assert.Equal(0, controller.ObserveClock(0, TimeSpan.Zero));
        double drift = controller.ObserveClock(48_048, TimeSpan.FromSeconds(1));
        Assert.InRange(drift, 999, 1_001);
    }

    [Fact]
    public void RemotePcmMixer_AlignsStreamsMixesAndConcealsMissingInput()
    {
        const int sampleCount = 8;
        var mixer = new RemotePcmMixer(sampleCount * sizeof(float));
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        mixer.RegisterStream(first);
        mixer.RegisterStream(second);
        byte[] firstFrame = CreateFloatFrame(sampleCount, 0.4f);
        byte[] secondFrame = CreateFloatFrame(sampleCount, 0.7f);
        mixer.Enqueue(first, firstFrame.ToArray());
        mixer.Enqueue(first, firstFrame.ToArray());
        mixer.Enqueue(second, secondFrame.ToArray());
        mixer.Enqueue(second, secondFrame.ToArray());
        var output = new byte[sampleCount * sizeof(float)];

        Assert.True(mixer.TryMixNext(output));
        Assert.All(
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(output).ToArray(),
            sample => Assert.Equal(1f, sample));
        Assert.Equal(sampleCount, mixer.Statistics.ClippedSamples);

        Assert.True(mixer.TryMixNext(output));
        mixer.Enqueue(first, firstFrame.ToArray());
        Assert.True(mixer.TryMixNext(output));
        Assert.All(
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(output).ToArray(),
            sample => Assert.Equal(0.4f, sample));
        Assert.Equal(1, mixer.Statistics.StreamUnderflows);
    }

    [Fact]
    public async Task DiagnosticsArchive_FiltersSecretsAndContainsExpectedEntries()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "ListenSphere.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using (var diagnostics = new DiagnosticArchiveService(
                       Path.Combine(directory, "logs")))
            {
                diagnostics.Record(
                    DiagnosticSeverity.Warning,
                    "test.event",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = "network",
                        ["sessionKey"] = "secret-value",
                        ["pcmPayload"] = "audio-value"
                    });

                string path = await diagnostics.ExportAsync(
                    directory,
                    TestContext.Current.CancellationToken);
                using ZipArchive archive = ZipFile.OpenRead(path);
                Assert.NotNull(archive.GetEntry("runtime.json"));
                Assert.NotNull(archive.GetEntry("events.json"));
                Assert.NotNull(archive.GetEntry("privacy.txt"));
                ZipArchiveEntry logEntry = Assert.Single(
                    archive.Entries,
                    entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal));
                using var reader = new StreamReader(archive.GetEntry("events.json")!.Open());
                string events = await reader.ReadToEndAsync(
                    TestContext.Current.CancellationToken);
                Assert.Contains("network", events);
                Assert.DoesNotContain("secret-value", events);
                Assert.DoesNotContain("audio-value", events);
                using var logReader = new StreamReader(logEntry.Open());
                string persistedLog = await logReader.ReadToEndAsync(
                    TestContext.Current.CancellationToken);
                Assert.Contains("network", persistedLog);
                Assert.DoesNotContain("secret-value", persistedLog);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AudioLevelCalculator_ReturnsPeakRmsAndClipping()
    {
        var level = AudioLevelCalculator.Calculate([0.5f, -0.5f, 0.5f, -0.5f]);
        var clipping = AudioLevelCalculator.Calculate([0f, 1.01f]);

        Assert.Equal(0.5f, level.Peak);
        Assert.Equal(0.5f, level.Rms, precision: 5);
        Assert.False(level.IsClipping);
        Assert.True(clipping.IsClipping);
        Assert.Equal(AudioLevel.Silence, AudioLevelCalculator.Calculate([]));
    }

    [Fact]
    public void PcmFrameSlicer_ReassemblesArbitraryChunkBoundaries()
    {
        var slicer = new PcmFrameSlicer(AudioFormat.Default, 10);
        var input = Enumerable.Range(0, slicer.FrameBytes * 3)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var frames = new List<(byte[] Data, ulong Timestamp)>();
        void Save(AudioFrame frame) => frames.Add((frame.Data.ToArray(), frame.Timestamp));

        Assert.Equal(0, slicer.Append(input.AsSpan(0, 17), Save));
        Assert.Equal(1, slicer.Append(input.AsSpan(17, slicer.FrameBytes), Save));
        Assert.Equal(2, slicer.Append(input.AsSpan(17 + slicer.FrameBytes), Save));

        Assert.Equal(3, frames.Count);
        Assert.Equal(0UL, frames[0].Timestamp);
        Assert.Equal(480UL, frames[1].Timestamp);
        Assert.Equal(960UL, frames[2].Timestamp);
        Assert.Equal(input, frames.SelectMany(frame => frame.Data).ToArray());
    }

    private static byte[] CreateFloatFrame(int sampleCount, float value)
    {
        float[] samples = Enumerable.Repeat(value, sampleCount).ToArray();
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }
}
