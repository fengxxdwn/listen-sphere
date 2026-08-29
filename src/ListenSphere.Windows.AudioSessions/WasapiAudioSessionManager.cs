using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ListenSphere.Windows.AudioSessions;

/// <summary>NAudio-based implementation for the default Windows render endpoint.</summary>
public sealed class WasapiAudioSessionManager : IWindowsAudioSessionManager
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private readonly object gate = new();
    private CancellationTokenSource? monitorLifetime;
    private Task? monitorTask;
    private bool disposed;

    public event EventHandler<AudioSessionsChangedEventArgs>? SessionsChanged;
    public event EventHandler<AudioSessionMonitoringFailedEventArgs>? MonitoringFailed;

    public async ValueTask<IReadOnlyList<WindowsAudioSession>> GetSessionsAsync(
        CancellationToken cancellationToken) =>
        await Task.Run(ReadSessions, cancellationToken).ConfigureAwait(false);

    public async ValueTask SetVolumeAsync(
        string sessionId,
        float volume,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var normalized = Math.Clamp(volume, 0f, 1f);
        await Task.Run(
            () => UpdateSession(sessionId, session =>
            {
                using var control = session.SimpleAudioVolume;
                control.Volume = normalized;
            }),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetMuteAsync(
        string sessionId,
        bool isMuted,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await Task.Run(
            () => UpdateSession(sessionId, session =>
            {
                using var control = session.SimpleAudioVolume;
                control.Mute = isMuted;
            }),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask StartMonitoringAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (monitorTask is not null)
            {
                return ValueTask.CompletedTask;
            }

            monitorLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            monitorTask = MonitorAsync(monitorLifetime.Token);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StopMonitoringAsync(CancellationToken cancellationToken)
    {
        Task? task;
        CancellationTokenSource? lifetime;
        lock (gate)
        {
            task = monitorTask;
            monitorTask = null;
            lifetime = monitorLifetime;
            monitorLifetime = null;
        }

        if (task is null)
        {
            return;
        }

        lifetime?.Cancel();
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true)
        {
            return;
        }
        finally
        {
            lifetime?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await StopMonitoringAsync(CancellationToken.None).ConfigureAwait(false);
        disposed = true;
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var sessions = await GetSessionsAsync(cancellationToken).ConfigureAwait(false);
                SessionsChanged?.Invoke(this, new AudioSessionsChangedEventArgs(sessions));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                MonitoringFailed?.Invoke(
                    this,
                    new AudioSessionMonitoringFailedEventArgs(exception));
            }

            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private static IReadOnlyList<WindowsAudioSession> ReadSessions()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
        {
            return [];
        }

        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        var manager = device.AudioSessionManager;
        try
        {
            manager.RefreshSessions();
            var collection = manager.Sessions;
            var sessions = new List<WindowsAudioSession>(collection.Count);
            for (var index = 0; index < collection.Count; index++)
            {
                using var session = collection[index];
                if (session.State == AudioSessionState.AudioSessionStateExpired)
                {
                    continue;
                }

                sessions.Add(CreateSnapshot(session));
            }

            return sessions
                .GroupBy(session => session.SessionId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(session => session.IsActive)
                .ThenBy(session => session.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            manager.Dispose();
        }
    }

    private static WindowsAudioSession CreateSnapshot(AudioSessionControl session)
    {
        var processId = checked((int)session.GetProcessID);
        var processInfo = GetProcessInfo(processId);
        var displayName = AudioSessionMetadata.ResolveDisplayName(
            session.DisplayName,
            session.IsSystemSoundsSession,
            processInfo.FileDescription,
            processInfo.ProcessName,
            processId);
        var sessionId =
            NullIfWhiteSpace(session.GetSessionInstanceIdentifier) ??
            NullIfWhiteSpace(session.GetSessionIdentifier) ??
            $"{processId}:{displayName}";
        using var volume = session.SimpleAudioVolume;
        var state = session.State;
        var peak = state == AudioSessionState.AudioSessionStateExpired
            ? 0
            : Math.Clamp(session.AudioMeterInformation.MasterPeakValue, 0, 1);

        return new WindowsAudioSession(
            sessionId,
            processId,
            displayName,
            processInfo.ProcessPath,
            NullIfWhiteSpace(session.IconPath),
            Math.Clamp(volume.Volume, 0, 1),
            volume.Mute,
            state == AudioSessionState.AudioSessionStateActive && peak > 0.0001f,
            peak);
    }

    private static void UpdateSession(string sessionId, Action<AudioSessionControl> update)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
        {
            throw new InvalidOperationException("没有可用的默认输出设备。");
        }

        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        var manager = device.AudioSessionManager;
        try
        {
            manager.RefreshSessions();
            var collection = manager.Sessions;
            for (var index = 0; index < collection.Count; index++)
            {
                using var session = collection[index];
                var candidate =
                    NullIfWhiteSpace(session.GetSessionInstanceIdentifier) ??
                    NullIfWhiteSpace(session.GetSessionIdentifier);
                if (!string.Equals(candidate, sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                update(session);
                return;
            }

            throw new InvalidOperationException("音频会话已经结束。");
        }
        finally
        {
            manager.Dispose();
        }
    }

    private static ProcessInfo GetProcessInfo(int processId)
    {
        if (processId <= 0)
        {
            return default;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            string? processPath = null;
            string? fileDescription = null;
            try
            {
                processPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(processPath))
                {
                    fileDescription = FileVersionInfo.GetVersionInfo(processPath).FileDescription;
                }
            }
            catch (Exception exception)
            {
                // Protected and packaged processes may deny MainModule access.
                Trace.TraceInformation(
                    "Process metadata is unavailable for PID {0}: {1}",
                    processId,
                    exception.Message);
            }

            return new ProcessInfo(processName, processPath, fileDescription);
        }
        catch (Exception exception)
        {
            Trace.TraceInformation(
                "Audio-session process {0} ended during enumeration: {1}",
                processId,
                exception.Message);
            return default;
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private readonly record struct ProcessInfo(
        string? ProcessName,
        string? ProcessPath,
        string? FileDescription);
}
