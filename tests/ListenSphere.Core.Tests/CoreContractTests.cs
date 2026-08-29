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
            LocalSourceVolume = 1.5f,
            LocalSourceMuted = true,
            FirstRunCompleted = true,
            SenderSelectedApplicationKeys = [@"C:\Apps\Game.exe", @"c:\apps\game.exe", "  Discord  "],
            SenderCaptureMode = "system",
            SenderCaptureDeviceId = "  endpoint-sender  ",
            MicrophoneOutputDeviceId = "  cable-input  ",
            MicrophoneOutputVolume = 1.5f,
            MicrophoneOutputMuted = true,
            MicrophoneMonitoringEnabled = true,
            MicrophoneMonitoringDeviceId = "  speaker-monitor  ",
            AutomaticRoutingEnabled = true,
            AudioRoutingRules =
            [
                new AudioRoutingRuleSettings(
                    Guid.NewGuid(),
                    "  Discord 语音  ",
                    "  Discord  ",
                    "  语音  ",
                    SourceKind: "  application  ",
                    Priority: 1200)
            ],
            ChannelLayouts =
            [
                new ChannelLayoutSettings(channelId, true, 25_000),
                new ChannelLayoutSettings(channelId, false, 1)
            ],
            AudioOutputRoutes =
            [
                new AudioOutputRouteSettings(channelId, "  endpoint-secondary  "),
                new AudioOutputRouteSettings(channelId, "endpoint-secondary")
            ],
            Scenes =
            [
                new SceneSettings(
                    Guid.NewGuid(),
                    "游戏",
                    [new ChannelSettings(
                        channelId,
                        "副电脑",
                        0.4f,
                        true,
                        "语音清晰",
                        [-6, -4, -2, -1, 0, 2, 4, 3, 1, -1],
                        false,
                        "语音")],
                    "endpoint-1",
                    0.65f,
                    false,
                    [new GroupBusSettings("语音", 0.7f, true, "语音清晰")])
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
            Assert.Equal(1f, actual.LocalSourceVolume);
            Assert.True(actual.LocalSourceMuted);
            Assert.True(actual.FirstRunCompleted);
            Assert.Equal([@"C:\Apps\Game.exe", "Discord"], actual.SenderSelectedApplicationKeys);
            Assert.Equal("system", actual.SenderCaptureMode);
            Assert.Equal("endpoint-sender", actual.SenderCaptureDeviceId);
            Assert.Equal("cable-input", actual.MicrophoneOutputDeviceId);
            Assert.Equal(1f, actual.MicrophoneOutputVolume);
            Assert.True(actual.MicrophoneOutputMuted);
            Assert.True(actual.MicrophoneMonitoringEnabled);
            Assert.Equal("speaker-monitor", actual.MicrophoneMonitoringDeviceId);
            Assert.True(actual.AutomaticRoutingEnabled);
            AudioRoutingRuleSettings route = Assert.Single(actual.AudioRoutingRules);
            Assert.Equal("Discord 语音", route.Name);
            Assert.Equal("Discord", route.SourcePattern);
            Assert.Equal("语音", route.TargetGroup);
            Assert.Equal("application", route.SourceKind);
            Assert.Equal(1000, route.Priority);
            ChannelLayoutSettings layout = Assert.Single(actual.ChannelLayouts);
            Assert.Equal(channelId, layout.ChannelId);
            Assert.True(layout.IsPinned);
            Assert.Equal(10_000, layout.SortOrder);
            AudioOutputRouteSettings outputRoute = Assert.Single(actual.AudioOutputRoutes);
            Assert.Equal(channelId, outputRoute.ChannelId);
            Assert.Equal("endpoint-secondary", outputRoute.DeviceId);
            SceneSettings scene = Assert.Single(actual.Scenes);
            Assert.Equal("游戏", scene.Name);
            ChannelSettings channel = Assert.Single(scene.Channels);
            Assert.Equal(channelId, channel.ChannelId);
            Assert.Equal("语音清晰", channel.EqualizerPreset);
            float[] gains = Assert.IsType<float[]>(channel.EqualizerGains);
            Assert.Equal([-6f, -4f, -2f, -1f, 0f, 2f, 4f, 3f, 1f, -1f], gains);
            Assert.False(channel.EqualizerEnabled);
            Assert.Equal("语音", channel.ChannelGroup);
            GroupBusSettings group = Assert.Single(scene.GroupBuses!);
            Assert.Equal("语音", group.Name);
            Assert.Equal(0.7f, group.Volume);
            Assert.True(group.IsMuted);
            Assert.Equal("语音清晰", group.EqualizerPreset);
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
    public void AudioRoutingRules_SelectHighestPriorityCompatibleRule()
    {
        AudioRoutingRuleSettings[] rules =
        [
            new(Guid.NewGuid(), "浏览器媒体", "chrome", "媒体",
                AudioRouteMatchMode.Contains, Priority: 100),
            new(Guid.NewGuid(), "精确语音", "Chrome", "语音",
                AudioRouteMatchMode.Exact, SourceKind: "application", Priority: 900),
            new(Guid.NewGuid(), "蓝牙专用", "Chrome", "系统",
                AudioRouteMatchMode.Exact, Transport: "Bluetooth", Priority: 1000),
            new(Guid.NewGuid(), "已禁用", "Chrome", "自定义",
                Priority: 1000, IsEnabled: false)
        ];

        AudioRoutingRuleSettings? matched = AudioRoutingRuleEvaluator.Match(
            rules,
            new AudioRoutingContext("chrome", "Application", "Wireless"));

        Assert.NotNull(matched);
        Assert.Equal("精确语音", matched.Name);
        Assert.Equal("语音", matched.TargetGroup);
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
    public void MasterSoftLimiter_PreservesSafeSamplesAndSoftLimitsMixedPeaks()
    {
        const int sampleCount = 8;
        var mixer = new RemotePcmMixer(
            sampleCount * sizeof(float),
            hardClipOutput: false);
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        mixer.RegisterStream(first);
        mixer.RegisterStream(second);
        for (int index = 0; index < 2; index++)
        {
            mixer.Enqueue(first, CreateFloatFrame(sampleCount, 0.6f));
            mixer.Enqueue(second, CreateFloatFrame(sampleCount, 0.5f));
        }
        var output = new byte[sampleCount * sizeof(float)];

        Assert.True(mixer.TryMixNext(output));
        Span<float> mixed = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(output.AsSpan());
        Assert.All(mixed.ToArray(), sample => Assert.Equal(1.1f, sample, 3));

        var limiter = new MasterSoftLimiter();
        limiter.Process(output);

        Assert.All(mixed.ToArray(), sample => Assert.InRange(sample, 0.9f, 0.98f));
        Assert.Equal(sampleCount, limiter.Statistics.LimitedSamples);
        Assert.InRange(limiter.Statistics.MaximumInputPeak, 1.099f, 1.101f);

        float[] safe = [0.5f, -0.75f];
        limiter.Process(System.Runtime.InteropServices.MemoryMarshal.AsBytes(safe.AsSpan()));
        Assert.Equal([0.5f, -0.75f], safe);
        Assert.Throws<ArgumentException>(() => limiter.Process(new byte[3]));
    }

    [Fact]
    public void RemotePcmMixer_RebuffersAfterStreamUnderflow()
    {
        const int sampleCount = 8;
        var mixer = new RemotePcmMixer(
            sampleCount * sizeof(float),
            startupFrames: 2,
            maximumFrames: 8);
        Guid stream = Guid.NewGuid();
        byte[] frame = CreateFloatFrame(sampleCount, 0.25f);
        var output = new byte[sampleCount * sizeof(float)];
        mixer.RegisterStream(stream);
        mixer.Enqueue(stream, frame.ToArray());
        mixer.Enqueue(stream, frame.ToArray());

        Assert.True(mixer.TryMixNext(output));
        Assert.True(mixer.TryMixNext(output));
        Assert.False(mixer.TryMixNext(output));
        Assert.Equal(1, mixer.Statistics.StreamUnderflows);

        mixer.Enqueue(stream, frame.ToArray());
        Assert.False(mixer.TryMixNext(output));
        mixer.Enqueue(stream, frame.ToArray());
        Assert.True(mixer.TryMixNext(output));
    }

    [Fact]
    public void RemotePcmMixer_AppliesPerStreamBluetoothStartupBuffer()
    {
        const int sampleCount = 8;
        var mixer = new RemotePcmMixer(
            sampleCount * sizeof(float),
            startupFrames: 2,
            maximumFrames: 8);
        Guid stream = Guid.NewGuid();
        byte[] frame = CreateFloatFrame(sampleCount, 0.25f);
        var output = new byte[sampleCount * sizeof(float)];
        mixer.RegisterStream(stream, preferredStartupFrames: 4);

        for (int index = 0; index < 3; index++)
        {
            mixer.Enqueue(stream, frame.ToArray());
            Assert.False(mixer.TryMixNext(output));
        }

        mixer.Enqueue(stream, frame.ToArray());
        Assert.True(mixer.TryMixNext(output));
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
    public void ThreeBandEqualizer_AppliesBassGainAndKeepsSamplesBounded()
    {
        float[] samples = new float[4_800];
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            float value = 0.15f * MathF.Sin(2 * MathF.PI * 100 * frame / 48_000);
            samples[frame * 2] = value;
            samples[(frame * 2) + 1] = value;
        }

        float originalRms = AudioLevelCalculator.Calculate(samples).Rms;
        new ThreeBandEqualizer().Process(samples, 9, 0, 0);
        AudioLevel processed = AudioLevelCalculator.Calculate(samples);

        Assert.True(processed.Rms > originalRms);
        Assert.All(samples, sample => Assert.InRange(sample, -1f, 1f));
    }

    [Fact]
    public void GraphicEqualizer_AppliesTenBandGainAndKeepsSamplesBounded()
    {
        float[] samples = new float[9_600];
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            float value = 0.1f * MathF.Sin(2 * MathF.PI * 1_000 * frame / 48_000);
            samples[frame * 2] = value;
            samples[(frame * 2) + 1] = value;
        }
        float originalRms = AudioLevelCalculator.Calculate(samples).Rms;
        float[] gains = new float[10];
        gains[5] = 12;

        new GraphicEqualizer().Process(samples, gains);

        Assert.True(AudioLevelCalculator.Calculate(samples).Rms > originalRms);
        Assert.All(samples, sample => Assert.InRange(sample, -1f, 1f));
    }

    [Fact]
    public void ChannelDynamics_AppliesGateCompressionAndLimiterInOrder()
    {
        var processor = new ChannelDynamicsProcessor();
        float[] quiet = Enumerable.Repeat(0.001f, 960).ToArray();
        var settings = new ChannelDynamicsSettings(
            PreampDb: 6,
            NoiseGateEnabled: true,
            NoiseGateThresholdDb: -40,
            CompressorEnabled: true,
            CompressorThresholdDb: -18,
            CompressorRatio: 6,
            LimiterEnabled: true,
            LimiterCeilingDb: -3);

        ChannelDynamicsResult gated = processor.ProcessBeforeEqualizer(quiet, settings);
        Assert.True(gated.GateClosed);
        Assert.All(quiet, sample => Assert.InRange(MathF.Abs(sample), 0, 0.0005f));

        processor.Reset();
        float[] loud = Enumerable.Repeat(0.95f, 4_800).ToArray();
        ChannelDynamicsResult compressed = processor.ProcessBeforeEqualizer(loud, settings);
        Assert.True(compressed.GainReductionDb > 1);
        loud.AsSpan().Fill(1.2f);
        ChannelDynamicsResult limited = processor.ApplyLimiter(loud, settings, compressed);
        float ceiling = MathF.Pow(10f, -3f / 20f);
        Assert.True(limited.Limited);
        Assert.All(loud, sample => Assert.InRange(MathF.Abs(sample), 0, ceiling + 0.0001f));
    }

    [Fact]
    public void VoiceDucking_ActivatesForVoiceAndReturnsAttenuatedGain()
    {
        var ducking = new VoiceDuckingController(
            voiceThresholdDb: -45,
            holdMilliseconds: 200,
            attackMilliseconds: 1,
            releaseMilliseconds: 20);
        ducking.ObserveVoice(Enumerable.Repeat(0.2f, 960).ToArray());
        Assert.True(ducking.IsVoiceActive);

        _ = ducking.GetTargetGain(12);
        Thread.Sleep(5);
        float gain = ducking.GetTargetGain(12);
        Assert.InRange(gain, 0.2f, 0.6f);
    }

    [Fact]
    public async Task Settings_NormalizesStageThreeDynamicsParameters()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ListenSphere.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonSettingsStore(path);
            Guid channelId = Guid.NewGuid();
            await store.SaveAsync(
                new ListenSphereSettings
                {
                    Scenes =
                    [
                        new SceneSettings(
                            Guid.NewGuid(),
                            "Dynamics",
                            [new ChannelSettings(
                                channelId,
                                "Voice",
                                1,
                                false,
                                PreampDb: 99,
                                NoiseGateThresholdDb: -120,
                                CompressorThresholdDb: 10,
                                CompressorRatio: 99,
                                LimiterCeilingDb: 4,
                                VoiceDuckingReductionDb: 99)])
                    ]
                },
                TestContext.Current.CancellationToken);

            ListenSphereSettings loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            ChannelSettings channel = Assert.Single(Assert.Single(loaded.Scenes).Channels);
            Assert.Equal(12, channel.PreampDb);
            Assert.Equal(-80, channel.NoiseGateThresholdDb);
            Assert.Equal(0, channel.CompressorThresholdDb);
            Assert.Equal(20, channel.CompressorRatio);
            Assert.Equal(-0.1f, channel.LimiterCeilingDb);
            Assert.Equal(30, channel.VoiceDuckingReductionDb);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ImaAdpcm_RoundTripsFrameWithBoundedErrorAndFixedSize()
    {
        short[] source = new short[ImaAdpcmCodec.SamplesPerFrame];
        for (int index = 0; index < source.Length; index++)
            source[index] = (short)(12_000 * MathF.Sin(2 * MathF.PI * 440 * index / 48_000));
        var encoded = new byte[ImaAdpcmCodec.EncodedBytesPerFrame];
        var decoded = new short[ImaAdpcmCodec.SamplesPerFrame];

        ImaAdpcmCodec.Encode(source, encoded);
        ImaAdpcmCodec.Decode(encoded, decoded);

        Assert.Equal(244, encoded.Length);
        Assert.Equal(source[0], decoded[0]);
        double meanError = source.Zip(decoded, (left, right) => Math.Abs(left - right)).Average();
        Assert.InRange(meanError, 0, 1_500);
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
