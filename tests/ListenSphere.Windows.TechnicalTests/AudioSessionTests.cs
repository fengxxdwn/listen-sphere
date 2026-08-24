using ListenSphere.Windows.AudioSessions;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class AudioSessionTests
{
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
