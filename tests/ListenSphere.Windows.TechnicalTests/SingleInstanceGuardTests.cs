using ListenSphere.Controller;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquire_RejectsConcurrentOwnerAndAllowsRestartAfterRelease()
    {
        string name = $@"Local\ListenSphere.Controller.Tests.{Guid.NewGuid():N}";
        using SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(name);

        Assert.NotNull(first);
        Assert.Null(SingleInstanceGuard.TryAcquire(name));

        first.Dispose();
        using SingleInstanceGuard? restarted = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(restarted);
    }
}
