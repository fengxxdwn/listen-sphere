namespace ListenSphere.Windows.AudioSessions;

public sealed record WindowsAudioSession(
    string SessionId,
    int ProcessId,
    string DisplayName,
    string? ProcessPath,
    string? IconPath,
    float Volume,
    bool IsMuted,
    bool IsActive,
    float Peak,
    string? OutputDeviceId = null,
    string? OutputDeviceName = null);

public sealed class AudioSessionsChangedEventArgs(
    IReadOnlyList<WindowsAudioSession> sessions) : EventArgs
{
    public IReadOnlyList<WindowsAudioSession> Sessions { get; } = sessions;
}

public sealed class AudioSessionMonitoringFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

/// <summary>
/// Enumerates and controls Windows audio sessions across active render endpoints.
/// This controls existing system sessions and does not capture per-application PCM.
/// </summary>
public interface IWindowsAudioSessionManager : IAsyncDisposable
{
    event EventHandler<AudioSessionsChangedEventArgs>? SessionsChanged;
    event EventHandler<AudioSessionMonitoringFailedEventArgs>? MonitoringFailed;

    ValueTask<IReadOnlyList<WindowsAudioSession>> GetSessionsAsync(
        CancellationToken cancellationToken);

    ValueTask SetVolumeAsync(
        string sessionId,
        float volume,
        CancellationToken cancellationToken);

    ValueTask SetMuteAsync(
        string sessionId,
        bool isMuted,
        CancellationToken cancellationToken);

    ValueTask StartMonitoringAsync(CancellationToken cancellationToken);
    ValueTask StopMonitoringAsync(CancellationToken cancellationToken);
}
