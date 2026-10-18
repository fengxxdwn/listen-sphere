using ListenSphere.Controller;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquire_SignalsConcurrentOwnerAndAllowsRestartAfterRelease()
    {
        string name = $@"Local\ListenSphere.Controller.Tests.{Guid.NewGuid():N}";
        using var activationRequested = new ManualResetEventSlim();
        using SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(
            name,
            activationRequested.Set);

        Assert.NotNull(first);
        Assert.Null(SingleInstanceGuard.TryAcquire(name));
        Assert.True(activationRequested.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));

        first.Dispose();
        using SingleInstanceGuard? restarted = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(restarted);
    }
}
