using ListenSphere.Windows.AudioSessions;
using ListenSphere.Controller;
using ListenSphere.Controller.Presentation;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class AudioSessionTests
{
    [Fact]
    public void SessionVolume_IgnoresStaleSnapshotUntilWindowsConfirmsUserValue()
    {
        var snapshot = new WindowsAudioSession(
            "session-1",
            42,
            "播放器",
            null,
            null,
            0.5f,
            false,
            true,
            0.25f);
        float requestedVolume = 0;
        var item = new AudioSessionItemViewModel(
            snapshot,
            (_, volume) => requestedVolume = volume,
            (_, _) => { });

        item.VolumePercent = 80;
        item.Update(snapshot);

        Assert.Equal(0.8f, requestedVolume, 3);
        Assert.Equal(80, item.VolumePercent);

        item.Update(snapshot with { Volume = 0.8f });

        Assert.Equal(80, item.VolumePercent);
    }

    [Fact]
    public void ApplicationCard_AggregatesEndpointSessionsAndControlsEverySession()
    {
        const string processPath = @"C:\Apps\Player\player.exe";
        var realtek = new WindowsAudioSession(
            "realtek\u001fsession",
            42,
            "播放器",
            processPath,
            null,
            0.4f,
            false,
            false,
            0.1f,
            "realtek",
            "Speaker (Realtek Audio)");
        var headset = realtek with
        {
            SessionId = "headset\u001fsession",
            Volume = 0.7f,
            IsActive = true,
            Peak = 0.65f,
            OutputDeviceId = "headset",
            OutputDeviceName = "扬声器 (Headset)"
        };
        var volumeRequests = new List<(string SessionId, float Volume)>();
        var muteRequests = new List<(string SessionId, bool IsMuted)>();
        var item = new AudioSessionItemViewModel(
            [realtek, headset],
            (sessionId, volume) => volumeRequests.Add((sessionId, volume)),
            (sessionId, muted) => muteRequests.Add((sessionId, muted)));

        Assert.Equal(
            AudioSessionItemViewModel.CreateApplicationIdentityKey(realtek),
            AudioSessionItemViewModel.CreateApplicationIdentityKey(headset));
        Assert.Equal(2, item.SessionIds.Count);
        Assert.Contains("2 个设备", item.WindowsOutputText, StringComparison.Ordinal);
        Assert.True(item.IsActive);
        Assert.Equal(65, item.PeakPercent, 3);
        Assert.Equal(70, item.VolumePercent, 3);

        item.VolumePercent = 80;
        item.IsMuted = true;

        Assert.Equal(2, volumeRequests.Count);
        Assert.All(volumeRequests, request => Assert.Equal(0.8f, request.Volume, 3));
        Assert.Equal(2, muteRequests.Count);
        Assert.All(muteRequests, request => Assert.True(request.IsMuted));
    }

    [Fact]
    public void LocalSourceVolume_PreservesApplicationRatiosWhenReduced()
    {
        float[] currentVolumes = [80, 50, 20];

        float[] scaled = currentVolumes
            .Select(volume => LocalSessionsViewModel.ScaleVolumeProportionally(
                volume,
                previousMasterPercent: 100,
                nextMasterPercent: 40,
                restoreVolumePercent: volume))
            .ToArray();

        Assert.Equal([32f, 20f, 8f], scaled);
        Assert.Equal(currentVolumes[0] / currentVolumes[1], scaled[0] / scaled[1], 3);
        Assert.Equal(currentVolumes[1] / currentVolumes[2], scaled[1] / scaled[2], 3);
    }

    [Fact]
    public void LocalSourceVolume_UsesSavedApplicationVolumeWhenRaisedFromZero()
    {
        float restored = LocalSessionsViewModel.ScaleVolumeProportionally(
            currentVolumePercent: 0,
            previousMasterPercent: 0,
            nextMasterPercent: 50,
            restoreVolumePercent: 70);

        Assert.Equal(35, restored, 3);
    }

    [Theory]
    [InlineData("Browser", false, "File", "process", 42, "Browser")]
    [InlineData("", false, "File Description", "process", 42, "File Description")]
    [InlineData("", false, "", "process", 42, "process")]
    [InlineData("", false, "", "", 42, "进程 42")]
    [InlineData("ignored", true, "ignored", "ignored", 0, "系统声音")]
    public void Metadata_UsesStableDisplayNameFallbacks(
        string sessionName,
        bool systemSounds,
        string fileDescription,
        string processName,
        int processId,
        string expected)
    {
        var actual = AudioSessionMetadata.ResolveDisplayName(
            sessionName,
            systemSounds,
            fileDescription,
            processName,
            processId);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SessionManager_EnumeratesValidSnapshots()
    {
        await using var manager = new WasapiAudioSessionManager();

        var sessions = await manager.GetSessionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            sessions.Count,
            sessions.Select(session => session.SessionId)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(sessions, session =>
        {
            Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
            Assert.False(string.IsNullOrWhiteSpace(session.DisplayName));
            Assert.InRange(session.Volume, 0, 1);
            Assert.InRange(session.Peak, 0, 1);
        });
    }

    [Fact]
    public async Task SessionMonitor_PublishesAndStopsCleanly()
    {
        await using var manager = new WasapiAudioSessionManager();
        var cancellationToken = TestContext.Current.CancellationToken;
        var published = new TaskCompletionSource<IReadOnlyList<WindowsAudioSession>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.SessionsChanged += (_, args) => published.TrySetResult(args.Sessions);

        await manager.StartMonitoringAsync(cancellationToken);
        var sessions = await published.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        await manager.StopMonitoringAsync(cancellationToken);

        Assert.NotNull(sessions);
    }
}
