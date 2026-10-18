using GGdown.Services;

namespace GGdown.Tests.Services;

public class AppInstanceLockTests
{
    [Fact]
    public void Second_instance_cannot_recover_an_active_instances_database()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var first = AppInstanceLock.TryAcquire(root);
        Assert.NotNull(first);
        using (first)
        {
            using var second = AppInstanceLock.TryAcquire(root.ToLowerInvariant());
            Assert.Null(second);
        }
        using var next = AppInstanceLock.TryAcquire(root);
        Assert.NotNull(next);
    }
}
