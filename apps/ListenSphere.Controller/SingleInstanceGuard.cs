using System.Threading;

namespace ListenSphere.Controller;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly EventWaitHandle activationEvent;
    private readonly RegisteredWaitHandle activationRegistration;
    private Mutex? mutex;

    private SingleInstanceGuard(
        Mutex mutex,
        EventWaitHandle activationEvent,
        Action activationRequested)
    {
        this.mutex = mutex;
        this.activationEvent = activationEvent;
        activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            activationEvent,
            static (state, _) => ((Action)state!).Invoke(),
            activationRequested,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public static SingleInstanceGuard? TryAcquire(
        string name,
        Action? activationRequested = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var candidate = new Mutex(true, name, out bool createdNew);
        if (createdNew)
        {
            var activationEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                $"{name}.Activate");
            return new SingleInstanceGuard(
                candidate,
                activationEvent,
                activationRequested ?? (static () => { }));
        }

        using var existingActivationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            $"{name}.Activate");
        existingActivationEvent.Set();
        candidate.Dispose();
        return null;
    }

    public void Dispose()
    {
        Mutex? owned = Interlocked.Exchange(ref mutex, null);
        if (owned is null)
        {
            return;
        }

        activationRegistration.Unregister(null);
        activationEvent.Dispose();
        owned.ReleaseMutex();
        owned.Dispose();
    }
}
