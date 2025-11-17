using Shouldly;

namespace Hugo.Tests.Primitives;

public sealed class RwMutexTests
{
    [Fact(Timeout = 15_000)]
    public async Task Dispose_ShouldBeIdempotentAndBlockFurtherUsage()
    {
        var mutex = new RwMutex();

        await using (await mutex.RLockAsync(TestContext.Current.CancellationToken)) { }

        mutex.Dispose();
        mutex.Dispose(); // second dispose should be ignored

        Should.Throw<ObjectDisposedException>(() => mutex.EnterWriteScope());
    }

    [Fact(Timeout = 15_000)]
    public async Task LockAsync_ShouldThrowObjectDisposedException_WhenDisposed()
    {
        var mutex = new RwMutex();
        mutex.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await mutex.LockAsync(TestContext.Current.CancellationToken));
    }
}
