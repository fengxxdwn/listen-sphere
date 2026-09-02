using System.Threading;

namespace ListenSphere.Controller;

internal sealed class SingleInstanceGuard : IDisposable
{
    private Mutex? mutex;

    private SingleInstanceGuard(Mutex mutex) => this.mutex = mutex;

    public static SingleInstanceGuard? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var candidate = new Mutex(true, name, out bool createdNew);
        if (createdNew)
        {
            return new SingleInstanceGuard(candidate);
        }

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

        owned.ReleaseMutex();
        owned.Dispose();
    }
}
