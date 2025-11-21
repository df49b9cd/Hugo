
namespace Hugo.Tests;

public sealed class RwMutexTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask LockAsync_ShouldWaitUntilReadersExit()
    {
        using var mutex = new RwMutex();

        var reader1 = await mutex.RLockAsync(TestContext.Current.CancellationToken);
        var reader2 = await mutex.RLockAsync(TestContext.Current.CancellationToken);

        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writerTask = Task.Run(async () =>
        {
            writerStarted.SetResult();
            await using var write = await mutex.LockAsync(TestContext.Current.CancellationToken);
            writerEntered.SetResult();
        }, TestContext.Current.CancellationToken);

        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var premature = await Task.WhenAny(writerEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
        _ = premature.ShouldNotBe(writerEntered.Task);

        await reader1.DisposeAsync();
        await reader2.DisposeAsync();

        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await writerTask;
    }

    [Fact(Timeout = 5_000)]
    public async ValueTask RLockAsync_ShouldSupportCancellation()
    {
        using var mutex = new RwMutex();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await mutex.RLockAsync(cts.Token));

        // After cancellation, lock acquisition should still succeed.
        var releaser = await mutex.RLockAsync(TestContext.Current.CancellationToken);
        await releaser.DisposeAsync();
    }
}
