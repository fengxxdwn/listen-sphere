using ListenSphere.Configuration;
using Serilog;

namespace ListenSphere.Controller.Services;

public sealed class ControllerSettingsCoordinator(ISettingsStore settingsStore) : IAsyncDisposable
{
    private readonly SemaphoreSlim persistenceGate = new(1, 1);
    private readonly object debounceGate = new();
    private CancellationTokenSource? debounce;
    private Task? pendingDebounce;
    private Func<ListenSphereSettings, ListenSphereSettings>? snapshotFactory;
    private long persistenceRequest;
    private bool disposing;
    private bool disposed;

    public event EventHandler<string>? SaveFailed;

    public ListenSphereSettings Current { get; private set; } = new();
    public string? RecoveryPath =>
        settingsStore is JsonSettingsStore json ? json.LastRecoveryPath : null;

    public async Task<ListenSphereSettings> LoadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Current = await settingsStore.LoadAsync(cancellationToken);
        return Current;
    }

    public void UseDefaults()
    {
        ThrowIfDisposed();
        Current = new ListenSphereSettings();
    }

    public void ConfigureSnapshotFactory(
        Func<ListenSphereSettings, ListenSphereSettings> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ThrowIfDisposed();
        snapshotFactory = factory;
    }

    public void QueueSave(TimeSpan? delay = null)
    {
        lock (debounceGate)
        {
            ThrowIfUnavailable();
            debounce?.Cancel();
            debounce = new CancellationTokenSource();
            Task next = PersistAfterDelayAsync(
                debounce,
                delay ?? TimeSpan.FromMilliseconds(300));
            pendingDebounce = pendingDebounce is null
                ? next
                : Task.WhenAll(pendingDebounce, next);
        }
    }

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await PersistLatestAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SaveFailed?.Invoke(this, $"保存设置失败：{exception.Message}");
            Log.Warning(exception, "Failed to persist controller settings");
            return false;
        }
    }

    private async Task PersistAfterDelayAsync(
        CancellationTokenSource cancellation,
        TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);
            await PersistLatestAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            SaveFailed?.Invoke(this, $"保存设置失败：{exception.Message}");
            Log.Warning(exception, "Failed to persist controller settings");
        }
        finally
        {
            lock (debounceGate)
            {
                if (ReferenceEquals(debounce, cancellation))
                {
                    debounce = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task PersistLatestAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Func<ListenSphereSettings, ListenSphereSettings> factory = snapshotFactory ??
            throw new InvalidOperationException("Settings snapshot factory is not configured.");
        long request = Interlocked.Increment(ref persistenceRequest);
        ListenSphereSettings next = factory(Current) with
        {
            Version = ListenSphereSettings.CurrentVersion
        };

        await persistenceGate.WaitAsync(cancellationToken);
        try
        {
            if (request != Volatile.Read(ref persistenceRequest))
            {
                return;
            }

            await settingsStore.SaveAsync(next, cancellationToken);
            if (request == Volatile.Read(ref persistenceRequest))
            {
                Current = next;
            }
        }
        finally
        {
            persistenceGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (debounceGate)
        {
            if (disposed || disposing)
            {
                return;
            }

            disposing = true;
            debounce?.Cancel();
            debounce = null;
            pending = pendingDebounce;
        }

        if (pending is not null)
        {
            await pending;
        }

        await persistenceGate.WaitAsync();
        persistenceGate.Release();
        lock (debounceGate)
        {
            disposed = true;
        }
        persistenceGate.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    private void ThrowIfUnavailable() =>
        ObjectDisposedException.ThrowIf(disposed || disposing, this);
}